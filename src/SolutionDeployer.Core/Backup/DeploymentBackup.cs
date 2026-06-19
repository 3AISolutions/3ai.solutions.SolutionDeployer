using System.Text.Json.Serialization;

namespace SolutionDeployer.Core.Backup;

/// <summary>How a backup snapshot was captured (and therefore how it must be restored).</summary>
public enum BackupKind
{
    /// <summary>Pulled from / pushed to a remote server via Web Deploy (msdeploy.exe).</summary>
    MsDeploy,

    /// <summary>Zipped from / extracted to a local or UNC folder.</summary>
    FileSystem,
}

/// <summary>
/// A single point-in-time snapshot of a deployment target, persisted as a zip package with a JSON
/// manifest sidecar. Backups are grouped per publish profile (see <see cref="ProfileKey"/>) so the
/// UI can list "previous deployments" for a given profile and restore any of them.
/// </summary>
public sealed class DeploymentBackup
{
    public required string Id { get; init; }

    /// <summary>Stable folder key identifying the owning profile (project + profile + path hash).</summary>
    public required string ProfileKey { get; init; }

    public required string ProfileName { get; init; }

    public required string ProjectName { get; init; }

    public required BackupKind Kind { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Absolute path to the snapshot zip on disk.</summary>
    public required string PackagePath { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>The server/site or folder the snapshot came from, for display only.</summary>
    public string? Target { get; init; }

    [JsonIgnore]
    public string SizeText => SizeBytes switch
    {
        >= 1L << 30 => $"{SizeBytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{SizeBytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{SizeBytes / (double)(1L << 10):F1} KB",
        _ => $"{SizeBytes} B",
    };

    /// <summary>e.g. "2026-06-19 14:05:31 · 12.4 MB".</summary>
    [JsonIgnore]
    public string DisplayName => $"{CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {SizeText}";
}
