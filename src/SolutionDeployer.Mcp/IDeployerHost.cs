namespace SolutionDeployer.Mcp;

/// <summary>
/// What the MCP tools drive: the running app. Implemented by the desktop app over its main window state, so a
/// deploy started from Claude is the same deploy the window shows (status dots, output, backups, history).
/// Implementations throw <see cref="DeployerException"/> with a message meant for the caller.
/// </summary>
public interface IDeployerHost
{
    /// <summary>Every added source with its deploy targets, as the window shows them.</summary>
    Task<DeployerSnapshot> GetTargetsAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-reads every added source from disk (new profiles, edited scripts …).</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts deploying the given targets and returns the run, which completes in the background.</summary>
    Task<DeploymentRun> StartDeployAsync(DeployRequest request, CancellationToken cancellationToken = default);

    /// <summary>The snapshots taken of a target before earlier deploys, newest first.</summary>
    Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(string targetId, CancellationToken cancellationToken = default);

    /// <summary>Starts rolling a target back to a snapshot and returns the run.</summary>
    Task<DeploymentRun> StartRestoreAsync(string targetId, string backupId, CancellationToken cancellationToken = default);

    /// <summary>The git commits a profile's project (and its references) gained since it was last deployed.</summary>
    Task<string> GetReleaseSummaryAsync(string targetId, CancellationToken cancellationToken = default);
}

/// <summary>A request the host can't carry out, with a message meant for the caller.</summary>
public sealed class DeployerException(string message) : Exception(message);

public sealed record DeployRequest(IReadOnlyList<string> TargetIds, bool? RunInParallel = null, bool? BackupBeforePublish = null);

public sealed record DeployerSnapshot(
    bool Busy,
    bool Loading,
    bool RunInParallel,
    bool BackupBeforePublish,
    IReadOnlyList<SourceInfo> Sources);

public sealed record SourceInfo(string Name, string Path, string Kind, string? Problem, IReadOnlyList<TargetInfo> Targets);

public sealed record TargetInfo(
    string Id,
    string Kind,
    string Name,
    string Project,
    string? Method,
    string? Engine,
    string? Destination,
    bool RequiresCredentials,
    bool CredentialsReady,
    bool SupportsBackup,
    string? Problem,
    string? LastStatus,
    string? LastResult);

public sealed record BackupInfo(string Id, long Sequence, DateTimeOffset CreatedUtc, string Kind, bool IsPartial, string Description, string? Source);
