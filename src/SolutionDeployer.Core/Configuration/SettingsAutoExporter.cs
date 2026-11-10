using System.Text.Json;

namespace SolutionDeployer.Core.Configuration;

/// <summary>Where and how settings are exported automatically (<see cref="AppSettings.AutoExport"/>).</summary>
public sealed class SettingsAutoExport
{
    public bool Enabled { get; set; }

    /// <summary>The export file, replaced on every export.</summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Encrypt the export with the key kept in the credential store under
    /// <see cref="SettingsTransfer.AutoExportPasswordKey"/>.
    /// </summary>
    public bool Encrypt { get; set; }

    /// <summary>With <see cref="Encrypt"/>, also export the saved passwords the settings refer to.</summary>
    public bool IncludeSecrets { get; set; } = true;
}

/// <summary>The outcome of the latest automatic export.</summary>
public sealed record SettingsAutoExportStatus(DateTimeOffset At, bool Succeeded, string Message);

/// <summary>
/// Keeps the automatic export (<see cref="AppSettings.AutoExport"/>) up to date: every settings save schedules an
/// export a moment later (so a burst of saves writes once), and nothing is written while the exportable settings
/// are unchanged — saving the window placement, say, doesn't rewrite the file.
/// </summary>
public sealed class SettingsAutoExporter : IDisposable
{
    private readonly SettingsStore _store;
    private readonly ICredentialStore _credentials;
    private readonly TimeSpan _delay;
    private readonly Timer _timer;
    private readonly object _pendingGate = new();
    private readonly object _exportGate = new();
    private string? _pendingJson;
    private string? _lastExportKey;
    private bool _disposed;

    public SettingsAutoExporter(SettingsStore store, ICredentialStore credentials, TimeSpan? delay = null)
    {
        _store = store;
        _credentials = credentials;
        _delay = delay ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(_ => Flush());
        _store.Saved += OnSaved;
    }

    /// <summary>The latest automatic export's outcome, or null before the first one.</summary>
    public SettingsAutoExportStatus? LastStatus { get; private set; }

    /// <summary>Raised (on a background thread) when <see cref="LastStatus"/> changes.</summary>
    public event Action? StatusChanged;

    /// <summary>Writes any scheduled export now instead of waiting.</summary>
    public void Flush()
    {
        string? json;
        lock (_pendingGate)
        {
            json = _pendingJson;
            _pendingJson = null;
        }

        if (json is null)
            return;

        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json);
        }
        catch (JsonException)
        {
            return;
        }

        if (settings is not null)
            Export(settings, force: false);
    }

    /// <summary>
    /// Exports <paramref name="settings"/> to its automatic export file now, even when nothing changed (after the
    /// destination or password was changed, say). Returns null when automatic export is off.
    /// </summary>
    public SettingsAutoExportStatus? ExportNow(AppSettings settings)
    {
        lock (_pendingGate)
            _pendingJson = null;
        return Export(settings, force: true);
    }

    private void OnSaved(string json)
    {
        lock (_pendingGate)
        {
            if (_disposed)
                return;
            _pendingJson = json;
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    private SettingsAutoExportStatus? Export(AppSettings settings, bool force)
    {
        var config = settings.AutoExport;
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.FilePath))
            return null;

        lock (_exportGate)
        {
            SettingsAutoExportStatus status;
            string? password = null;
            if (config.Encrypt)
            {
                password = _credentials.IsAvailable ? _credentials.Get(SettingsTransfer.AutoExportPasswordKey) : null;
                if (string.IsNullOrEmpty(password))
                {
                    // Never fall back to writing the settings unprotected.
                    status = new SettingsAutoExportStatus(DateTimeOffset.Now, false,
                        "Not exported: the export password isn't saved on this computer. Set it again under Export / restore settings.");
                    SetStatus(status);
                    return status;
                }
            }

            var exportJson = SettingsTransfer.ToExportJson(settings);
            var secrets = config.Encrypt && config.IncludeSecrets
                ? SettingsTransfer.CollectSecrets(settings, _credentials)
                : null;

            // A hash, so the secrets aren't kept around in memory just to notice a change.
            var key = ExportKey(config, password, exportJson, secrets);
            if (!force && key == _lastExportKey)
                return null;

            try
            {
                SettingsTransfer.ExportJson(exportJson, config.FilePath, password, secrets);
                _lastExportKey = key;
                var what = secrets is { Count: > 0 } ? $"with {secrets.Count} saved password(s)"
                    : config.Encrypt ? "password-protected" : null;
                status = new SettingsAutoExportStatus(DateTimeOffset.Now, true,
                    $"Exported{(what is null ? string.Empty : $" ({what})")} to {config.FilePath}");
            }
            catch (Exception ex)
            {
                status = new SettingsAutoExportStatus(DateTimeOffset.Now, false, $"Export failed: {ex.Message}");
            }

            SetStatus(status);
            return status;
        }
    }

    private static string ExportKey(
        SettingsAutoExport config, string? password, string exportJson, IReadOnlyDictionary<string, string>? secrets)
    {
        var text = new System.Text.StringBuilder()
            .Append(config.FilePath).Append('\0').Append(password).Append('\0').Append(exportJson);
        foreach (var (k, v) in secrets ?? new Dictionary<string, string>())
            text.Append('\0').Append(k).Append('\0').Append(v);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
    }

    private void SetStatus(SettingsAutoExportStatus status)
    {
        LastStatus = status;
        StatusChanged?.Invoke();
    }

    /// <summary>Stops listening for saves and writes any export still scheduled.</summary>
    public void Dispose()
    {
        lock (_pendingGate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _store.Saved -= OnSaved;
        _timer.Dispose();
        Flush();
    }
}
