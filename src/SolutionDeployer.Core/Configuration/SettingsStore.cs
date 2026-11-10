using System.Text.Json;

namespace SolutionDeployer.Core.Configuration;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under the per-user application data folder.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    public SettingsStore(string? overridePath = null)
    {
        if (overridePath is not null)
        {
            _filePath = overridePath;
        }
        else
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "3ai.SolutionDeployer");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "settings.json");
        }
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt settings — fall back to defaults rather than crashing.
        }

        return new AppSettings();
    }

    /// <summary>The settings file this store reads and writes.</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Raised after every successful save, with the JSON that was written: a snapshot that stays valid however the
    /// settings object changes afterwards.
    /// </summary>
    public event Action<string>? Saved;

    public void Save(AppSettings settings)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Non-fatal: settings persistence is best-effort.
            return;
        }

        Saved?.Invoke(json);
    }
}
