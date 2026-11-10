using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// Backs the "Export / restore settings" window: the automatic export (file + optional password), a one-off
/// export, and restoring from an export.
/// </summary>
public partial class SettingsBackupViewModel : ObservableObject
{
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly ICredentialStore _credentialStore;
    private readonly SettingsAutoExporter _autoExporter;
    private readonly Func<string?, Task<string?>> _pickSavePath;
    private readonly Func<string?, Task<string?>> _pickOpenPath;

    /// <param name="pickSavePath">Asks where to save an export, starting from the given path.</param>
    /// <param name="pickOpenPath">Asks for an export to restore, starting from the given path.</param>
    /// <param name="canRestore">False while a deploy runs: the restore is offered but refused.</param>
    public SettingsBackupViewModel(
        SettingsStore settingsStore,
        AppSettings settings,
        ICredentialStore credentialStore,
        SettingsAutoExporter autoExporter,
        Func<string?, Task<string?>> pickSavePath,
        Func<string?, Task<string?>> pickOpenPath,
        bool canRestore)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _credentialStore = credentialStore;
        _autoExporter = autoExporter;
        _pickSavePath = pickSavePath;
        _pickOpenPath = pickOpenPath;
        CanRestore = canRestore;

        CredentialStoreAvailable = credentialStore.IsAvailable;
        HasSavedAutoExportPassword = CredentialStoreAvailable &&
                                     !string.IsNullOrEmpty(credentialStore.Get(SettingsTransfer.AutoExportPasswordKey));

        var config = settings.AutoExport;
        _autoExportEnabled = config.Enabled;
        _autoExportPath = config.FilePath ?? string.Empty;
        _autoExportEncrypt = config.Encrypt && CredentialStoreAvailable;
        _autoExportIncludeSecrets = config.IncludeSecrets;
        _autoExportStatus = DescribeAutoExport(config);
    }

    public bool CredentialStoreAvailable { get; }

    /// <summary>An automatic-export password is already saved; leaving the fields empty keeps it.</summary>
    public bool HasSavedAutoExportPassword { get; }

    public bool CanRestore { get; }

    public string AutoExportPasswordHint => HasSavedAutoExportPassword ? "Saved (leave empty to keep it)" : "Password";

    /// <summary>The settings read from the restored export; set when the window closes after a restore.</summary>
    public SettingsImport? Restored { get; private set; }

    /// <summary>The path of the restored export, for the status message.</summary>
    public string? RestoredFrom { get; private set; }

    public event Action? CloseRequested;

    // ---- Automatic export -------------------------------------------------

    [ObservableProperty]
    private bool _autoExportEnabled;

    [ObservableProperty]
    private string _autoExportPath;

    [ObservableProperty]
    private bool _autoExportEncrypt;

    [ObservableProperty]
    private bool _autoExportIncludeSecrets;

    [ObservableProperty]
    private string _autoExportPassword = string.Empty;

    [ObservableProperty]
    private string _autoExportPasswordConfirm = string.Empty;

    [ObservableProperty]
    private string? _autoExportStatus;

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand]
    private async Task BrowseAutoExportPathAsync()
    {
        var path = await _pickSavePath(string.IsNullOrWhiteSpace(AutoExportPath) ? null : AutoExportPath);
        if (!string.IsNullOrEmpty(path))
            AutoExportPath = path;
    }

    /// <summary>Saves the automatic-export settings, exports once to prove they work, then closes.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        var path = AutoExportPath.Trim();
        if (AutoExportEnabled)
        {
            if (path.Length == 0)
            {
                AutoExportStatus = "Choose the file to export to.";
                return;
            }

            if (AutoExportEncrypt)
            {
                if (AutoExportPassword.Length == 0 && !HasSavedAutoExportPassword)
                {
                    AutoExportStatus = "Enter a password, or turn off password protection.";
                    return;
                }

                if (AutoExportPassword != AutoExportPasswordConfirm)
                {
                    AutoExportStatus = "The passwords don't match.";
                    return;
                }
            }
        }

        if (AutoExportEncrypt && AutoExportPassword.Length > 0)
            _credentialStore.Set(SettingsTransfer.AutoExportPasswordKey, AutoExportPassword);
        else if (!AutoExportEncrypt)
            _credentialStore.Delete(SettingsTransfer.AutoExportPasswordKey);

        _settings.AutoExport = new SettingsAutoExport
        {
            Enabled = AutoExportEnabled,
            FilePath = path.Length == 0 ? null : path,
            Encrypt = AutoExportEncrypt,
            IncludeSecrets = AutoExportIncludeSecrets,
        };
        _settingsStore.Save(_settings);

        if (!AutoExportEnabled)
        {
            CloseRequested?.Invoke();
            return;
        }

        // Export straight away, so a bad path or a missing password shows up here and not silently later.
        IsBusy = true;
        AutoExportStatus = "Exporting…";
        var snapshot = SettingsTransfer.Clone(_settings);
        var status = await Task.Run(() => _autoExporter.ExportNow(snapshot));
        IsBusy = false;

        if (status is { Succeeded: false })
        {
            AutoExportStatus = status.Message;
            return;
        }

        CloseRequested?.Invoke();
    }

    // ---- Export now ------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanIncludeExportSecrets))]
    private string _exportPassword = string.Empty;

    [ObservableProperty]
    private string _exportPasswordConfirm = string.Empty;

    /// <summary>Include the saved passwords; only possible in a password-protected export.</summary>
    [ObservableProperty]
    private bool _exportIncludeSecrets = true;

    public bool CanIncludeExportSecrets => ExportPassword.Length > 0;

    [ObservableProperty]
    private string? _exportStatus;

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (ExportPassword != ExportPasswordConfirm)
        {
            ExportStatus = "The passwords don't match.";
            return;
        }

        var start = string.IsNullOrWhiteSpace(AutoExportPath)
            ? SettingsTransfer.DefaultFileName
            : Path.Combine(Path.GetDirectoryName(AutoExportPath) ?? string.Empty, SettingsTransfer.DefaultFileName);
        var path = await _pickSavePath(start);
        if (string.IsNullOrEmpty(path))
            return;

        IsBusy = true;
        ExportStatus = "Exporting…";
        var snapshot = SettingsTransfer.Clone(_settings);
        var password = ExportPassword;
        var includeSecrets = ExportIncludeSecrets && password.Length > 0;
        try
        {
            var secretCount = await Task.Run(() =>
            {
                var secrets = includeSecrets ? SettingsTransfer.CollectSecrets(snapshot, _credentialStore) : null;
                SettingsTransfer.Export(snapshot, path, password, secrets);
                return secrets?.Count ?? 0;
            });
            var what = secretCount > 0 ? $" (password-protected, with {secretCount} saved password(s))"
                : password.Length > 0 ? " (password-protected)" : string.Empty;
            ExportStatus = $"✔ Exported{what} to {path}";
            ExportPassword = ExportPasswordConfirm = string.Empty;
        }
        catch (Exception ex)
        {
            ExportStatus = $"✘ Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Restore ---------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestoreFile))]
    private string? _restorePath;

    [ObservableProperty]
    private string? _restoreInfo;

    [ObservableProperty]
    private bool _restoreNeedsPassword;

    [ObservableProperty]
    private string _restorePassword = string.Empty;

    [ObservableProperty]
    private string? _restoreStatus;

    public bool HasRestoreFile => !string.IsNullOrEmpty(RestorePath);

    [RelayCommand]
    private async Task BrowseRestoreAsync()
    {
        var start = RestorePath ?? (string.IsNullOrWhiteSpace(AutoExportPath) ? null : AutoExportPath);
        var path = await _pickOpenPath(start);
        if (string.IsNullOrEmpty(path))
            return;

        RestorePassword = string.Empty;
        RestoreStatus = null;
        try
        {
            var info = SettingsTransfer.ReadInfo(path);
            RestorePath = path;
            RestoreNeedsPassword = info.IsEncrypted;
            RestoreInfo = Describe(info);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            RestorePath = null;
            RestoreNeedsPassword = false;
            RestoreInfo = null;
            RestoreStatus = $"✘ {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (RestorePath is not { } path)
            return;

        if (!CanRestore)
        {
            RestoreStatus = "✘ A deploy is running. Restore once it has finished.";
            return;
        }

        IsBusy = true;
        RestoreStatus = "Reading…";
        var password = RestorePassword;
        try
        {
            Restored = await Task.Run(() => SettingsTransfer.Import(path, password));
            RestoredFrom = path;
            CloseRequested?.Invoke();
        }
        catch (Exception ex) when (ex is SettingsPasswordException or InvalidDataException or IOException
                                       or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            RestoreStatus = $"✘ {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private string? DescribeAutoExport(SettingsAutoExport config)
    {
        if (_autoExporter.LastStatus is { } status)
            return $"{status.At:g}: {status.Message}";

        if (config.Enabled && !string.IsNullOrEmpty(config.FilePath) && File.Exists(config.FilePath))
            return $"Last exported {File.GetLastWriteTime(config.FilePath):g}.";

        return null;
    }

    private static string Describe(SettingsExportInfo info)
    {
        var text = $"Exported {info.ExportedUtc.ToLocalTime():g}";
        if (!string.IsNullOrEmpty(info.MachineName))
            text += $" on {info.MachineName}";
        if (!string.IsNullOrEmpty(info.AppVersion))
            text += $" (v{info.AppVersion})";
        return text + (info.IsEncrypted ? " · password-protected" : string.Empty);
    }
}
