using System.Text.Json.Serialization;

namespace SolutionDeployer.Core.Backup;

/// <summary>How a backup snapshot was captured (and therefore how it must be restored).</summary>
public enum BackupKind
{
    /// <summary>Pulled from / pushed to a remote server via Web Deploy (msdeploy.exe).</summary>
    MsDeploy,

    /// <summary>Zipped from / extracted to a local or UNC folder.</summary>
    FileSystem,

    /// <summary>
    /// Only the server files a Web Deploy publish was about to update or delete, plus the list of files
    /// it was about to add. Restoring rolls that one deployment back.
    /// </summary>
    MsDeployPartial,

    /// <summary>
    /// Like <see cref="MsDeployPartial"/>, but for a script, which may deploy to several Web Deploy
    /// targets: see <see cref="DeploymentBackup.Targets"/>. The package holds one inner package per target.
    /// </summary>
    ScriptPartial,
}

/// <summary>What a <see cref="BackupKind.ScriptPartial"/> snapshot saved from one Web Deploy target.</summary>
public sealed class BackupTargetChanges
{
    /// <summary>
    /// msdeploy <c>computerName</c> of the target the files were saved from, e.g.
    /// <c>https://host:8172/msdeploy.axd?site=MySite</c>.
    /// </summary>
    public required string ComputerName { get; init; }

    /// <summary>
    /// Other servers running the same application, whose <c>-WhatIf</c> reported exactly the same changes: the
    /// files are saved once (from <see cref="ComputerName"/>) and the restore is applied to every one of them.
    /// </summary>
    public IReadOnlyList<string> ReplicaComputerNames { get; init; } = [];

    /// <summary>Every server this entry is restored to.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AllComputerNames => [ComputerName, .. ReplicaComputerNames];

    /// <summary>Server paths the deploy was about to update or delete (in this target's inner package).</summary>
    public IReadOnlyList<string> SavedPaths { get; init; } = [];

    /// <summary>Server paths the deploy was about to add.</summary>
    public IReadOnlyList<string> AddedPaths { get; init; } = [];

    /// <summary>The inner package's name inside the snapshot zip; null when nothing had to be saved.</summary>
    public string? PackageEntry { get; init; }
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

    /// <summary>
    /// Who the snapshot belongs to: the profile's file path, or the script's <see cref="Models.ScriptTarget.Id"/>.
    /// Lets cleanup tell whether the owner still exists. Null on snapshots taken before it was recorded.
    /// </summary>
    public string? OwnerId { get; init; }

    public required BackupKind Kind { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    /// Monotonic per-profile sequence number (1, 2, 3 …). Used for ordering and display so that
    /// snapshots taken within the same clock-second stay distinct and correctly ordered — the
    /// timestamp alone is not unique enough to sort or prune by.
    /// </summary>
    public long Sequence { get; init; }

    /// <summary>
    /// Storage key for the snapshot zip: an absolute path for local disk, or an object key for an S3
    /// destination. Interpreted by the owning <see cref="IBackupStore"/>.
    /// </summary>
    public required string PackagePath { get; init; }

    /// <summary>The destination this snapshot lives in ("local" or an S3 target id).</summary>
    public string StorageTargetId { get; init; } = Configuration.S3BackupTarget.LocalId;

    /// <summary>
    /// Fingerprint of the captured payload (entry names + sizes, excluding volatile MSDeploy package
    /// metadata). Lets a new snapshot be recognised as identical to the previous one — i.e. the
    /// deployment content did not actually change.
    /// </summary>
    public string? ContentHash { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>The server/site or folder the snapshot came from, for display only.</summary>
    public string? Target { get; init; }

    /// <summary>
    /// <see cref="BackupKind.MsDeployPartial"/> only: server paths (as msdeploy names them) the deploy was
    /// about to update or delete. These are what the package holds.
    /// </summary>
    public IReadOnlyList<string> SavedPaths { get; init; } = [];

    /// <summary><see cref="BackupKind.MsDeployPartial"/> only: server paths the deploy was about to add.</summary>
    public IReadOnlyList<string> AddedPaths { get; init; } = [];

    /// <summary><see cref="BackupKind.ScriptPartial"/> only: what was saved from each target the script deploys to.</summary>
    public IReadOnlyList<BackupTargetChanges> Targets { get; init; } = [];

    /// <summary>
    /// A partial snapshot only undoes its own deploy, so restoring it first rolls back every newer snapshot:
    /// deleting a snapshot leaves every older partial one unrestorable.
    /// </summary>
    [JsonIgnore]
    public bool IsPartial => Kind is BackupKind.MsDeployPartial or BackupKind.ScriptPartial;

    /// <summary>e.g. "4 saved, 1 added" for a partial snapshot; empty otherwise.</summary>
    [JsonIgnore]
    public string ChangeText => Kind switch
    {
        BackupKind.MsDeployPartial => $" · {SavedPaths.Count} saved, {AddedPaths.Count} added",
        BackupKind.ScriptPartial =>
            $" · {Targets.Sum(t => t.SavedPaths.Count)} saved, {Targets.Sum(t => t.AddedPaths.Count)} added" +
            (Targets.Sum(t => t.AllComputerNames.Count) is var servers && servers > 1 ? $" on {servers} servers" : string.Empty),
        _ => string.Empty,
    };

    [JsonIgnore]
    public string SizeText => FormatSize(SizeBytes);

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };

    /// <summary>e.g. "#3 · 2026-06-19 14:05:31 · 12.4 MB". The leading #N keeps same-second snapshots distinct.</summary>
    [JsonIgnore]
    public string DisplayName => $"#{Sequence} · {CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {SizeText}{ChangeText}";
}
