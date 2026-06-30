namespace SolutionDeployer.Core.Backup;

/// <summary>A package made available on local disk for restore, and whether it is a throwaway copy.</summary>
public readonly record struct DownloadedPackage(string LocalPath, bool IsTemporary);

/// <summary>
/// Where snapshot packages and their manifests are persisted. Implementations differ only in storage
/// (local disk vs. S3-compatible object storage); capture/restore of the deployment itself is handled
/// by <see cref="BackupService"/>.
/// </summary>
public interface IBackupStore
{
    /// <summary>Identifier of the backing destination (e.g. "local" or an S3 target id).</summary>
    string TargetId { get; }

    /// <summary>Human-readable description for logging (e.g. "local disk" or "s3://bucket/prefix").</summary>
    string Description { get; }

    /// <summary>The storage key/path for a package file within a profile's snapshot set.</summary>
    string ResolveKey(string profileKey, string fileName);

    /// <summary>Existing snapshots for a profile, newest first (sequence, then time, then id).</summary>
    Task<IReadOnlyList<DeploymentBackup>> ListAsync(string profileKey, CancellationToken cancellationToken = default);

    /// <summary>Persists a package (currently at <paramref name="localPackagePath"/>) and its manifest.</summary>
    Task SaveAsync(DeploymentBackup backup, string localPackagePath, CancellationToken cancellationToken = default);

    /// <summary>Makes a snapshot's package available on local disk for restore.</summary>
    Task<DownloadedPackage> DownloadAsync(DeploymentBackup backup, CancellationToken cancellationToken = default);

    /// <summary>Removes a snapshot's package and manifest.</summary>
    Task DeleteAsync(DeploymentBackup backup, CancellationToken cancellationToken = default);
}
