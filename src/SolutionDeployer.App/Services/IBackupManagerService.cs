using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.Services;

/// <summary>Shows the modal "Manage backups" window: every snapshot across all destinations, and cleanup.</summary>
public interface IBackupManagerService
{
    /// <param name="knownOwners">The profiles and scripts currently loaded, to identify older snapshots.</param>
    /// <returns>True when any snapshot was deleted.</returns>
    Task<bool> ShowAsync(AppSettings settings, IReadOnlyCollection<BackupOwner> knownOwners);
}
