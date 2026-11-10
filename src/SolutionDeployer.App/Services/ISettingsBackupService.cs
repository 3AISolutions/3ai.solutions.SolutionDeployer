using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.Services;

/// <summary>Settings (and any saved passwords) read from an export the user chose to restore.</summary>
public sealed record RestoredSettings(SettingsImport Import, string FilePath);

/// <summary>Shows the modal "Export / restore settings" window.</summary>
public interface ISettingsBackupService
{
    /// <param name="canRestore">False while a deploy runs, so a restore can't swap settings under it.</param>
    /// <returns>The settings to restore, or null when the user didn't restore.</returns>
    Task<RestoredSettings?> ShowAsync(AppSettings settings, bool canRestore);
}
