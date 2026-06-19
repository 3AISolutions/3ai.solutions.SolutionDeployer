using SolutionDeployer.Core.Backup;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// A single restorable snapshot shown under a profile's "Restore" menu. Carries its owning profile so
/// a restore can reuse that profile's current credentials.
/// </summary>
public sealed class BackupEntryViewModel(ProfileViewModel parent, DeploymentBackup backup)
{
    public ProfileViewModel Parent { get; } = parent;

    public DeploymentBackup Backup { get; } = backup;

    public string DisplayName => Backup.DisplayName;
}
