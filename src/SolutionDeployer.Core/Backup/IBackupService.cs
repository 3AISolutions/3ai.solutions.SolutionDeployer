using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Backup;

/// <summary>
/// Captures and restores point-in-time snapshots of a deployment target. Snapshots are files only:
/// a restore returns the previous deployed <em>content</em>, not database state or server config.
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// Whether <paramref name="owner"/> can be backed up, and if not, a human-readable reason.
    /// True for FileSystem profiles with a resolvable destination, MSDeploy profiles when msdeploy.exe
    /// is available, and scripts with a configured backup target.
    /// </summary>
    bool CanBackUp(BackupOwner owner, out string? reason);

    /// <summary>
    /// Captures the current deployment for <paramref name="job"/> into a new snapshot, pruning older
    /// snapshots the retention policy expires. For MSDeploy profiles only the files the publish is about to
    /// change are captured. Returns the created backup, or null when there was nothing to back up (e.g.
    /// a first-time deployment, or a publish that changes nothing). Throws on a genuine backup failure.
    /// </summary>
    Task<DeploymentBackup?> BackUpAsync(PublishJob job, Action<OutputLine> onOutput, CancellationToken cancellationToken = default);

    /// <summary>Lists existing snapshots for an owner (from its configured destination), newest first.</summary>
    Task<IReadOnlyList<DeploymentBackup>> ListAsync(BackupOwner owner, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a previously-captured snapshot to its original target. Restoring a partial (MSDeploy
    /// profile) snapshot also rolls back every newer snapshot first, so the target returns to its state
    /// before that deploy. MSDeploy restores require <paramref name="credentials"/>; FileSystem ones ignore them.
    /// </summary>
    Task RestoreAsync(
        DeploymentBackup backup,
        BackupOwner owner,
        PublishCredentials credentials,
        bool allowUntrustedCertificate,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What <see cref="DeleteAsync"/> removes for <paramref name="backup"/>: the snapshot plus every older partial
    /// snapshot of the same owner, which could no longer be restored without it. Oldest first.
    /// </summary>
    Task<IReadOnlyList<DeploymentBackup>> GetDeletionSetAsync(DeploymentBackup backup, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a snapshot (package + manifest) together with the rest of its <see cref="GetDeletionSetAsync"/>,
    /// oldest first. Returns false if they could not all be removed.
    /// </summary>
    Task<bool> DeleteAsync(DeploymentBackup backup, CancellationToken cancellationToken = default);
}

/// <summary>Profile-based shorthands for <see cref="IBackupService"/>.</summary>
public static class BackupServiceExtensions
{
    public static bool CanBackUp(this IBackupService service, PublishProfile profile, string projectDirectory, out string? reason) =>
        service.CanBackUp(BackupOwner.ForProfile(profile, projectDirectory), out reason);

    public static Task<IReadOnlyList<DeploymentBackup>> ListAsync(
        this IBackupService service, PublishProfile profile, string projectDirectory, CancellationToken cancellationToken = default) =>
        service.ListAsync(BackupOwner.ForProfile(profile, projectDirectory), cancellationToken);

    public static Task RestoreAsync(
        this IBackupService service,
        DeploymentBackup backup,
        PublishProfile profile,
        string projectDirectory,
        PublishCredentials credentials,
        bool allowUntrustedCertificate,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken = default) =>
        service.RestoreAsync(backup, BackupOwner.ForProfile(profile, projectDirectory), credentials,
            allowUntrustedCertificate, onOutput, cancellationToken);
}
