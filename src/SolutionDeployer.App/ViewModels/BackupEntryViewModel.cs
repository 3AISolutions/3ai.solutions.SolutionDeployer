using SolutionDeployer.Core.Backup;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// A single restorable snapshot shown under a profile's or script's "Restore" menu. Carries its owning
/// row so a restore can reuse that row's current credentials.
/// </summary>
public sealed class BackupEntryViewModel(BackupHostViewModel parent, DeploymentBackup backup)
{
    public BackupHostViewModel Parent { get; } = parent;

    public DeploymentBackup Backup { get; } = backup;

    public string DisplayName => Backup.DisplayName;
}
