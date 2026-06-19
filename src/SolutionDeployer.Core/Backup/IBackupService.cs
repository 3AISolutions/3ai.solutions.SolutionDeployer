using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Backup;

/// <summary>
/// Captures and restores point-in-time snapshots of a deployment target. Snapshots are files only:
/// a restore returns the previous deployed <em>content</em>, not database state or server config.
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// Whether <paramref name="profile"/> can be backed up, and if not, a human-readable reason.
    /// True for FileSystem profiles with a resolvable destination and MSDeploy profiles when
    /// msdeploy.exe is available.
    /// </summary>
    bool CanBackUp(PublishProfile profile, string projectDirectory, out string? reason);

    /// <summary>
    /// Captures the current deployment for <paramref name="job"/> (which must be a profile job) into a
    /// new snapshot, pruning older snapshots beyond the retention limit. Returns the created backup, or
    /// null when there was nothing to back up (e.g. a first-time deployment with no existing content).
    /// Throws on a genuine backup failure.
    /// </summary>
    Task<DeploymentBackup?> BackUpAsync(PublishJob job, Action<OutputLine> onOutput, CancellationToken cancellationToken = default);

    /// <summary>Lists existing snapshots for a profile, newest first.</summary>
    IReadOnlyList<DeploymentBackup> List(PublishProfile profile, string projectDirectory);

    /// <summary>
    /// Restores a previously-captured snapshot to its original target. MSDeploy restores require
    /// <paramref name="credentials"/>; FileSystem restores ignore them.
    /// </summary>
    Task RestoreAsync(
        DeploymentBackup backup,
        PublishProfile profile,
        string projectDirectory,
        PublishCredentials credentials,
        bool allowUntrustedCertificate,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a snapshot (zip + manifest). Returns false if it could not be removed.</summary>
    bool Delete(DeploymentBackup backup);
}
