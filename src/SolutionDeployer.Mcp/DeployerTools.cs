using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace SolutionDeployer.Mcp;

/// <summary>The MCP tools: list what can be deployed, deploy it, follow the run, and roll back.</summary>
[McpServerToolType]
public sealed class DeployerTools(IDeployerHost host, RunRegistry runs)
{
    private const int MaxWaitSeconds = 50;
    private const int DefaultLogLines = 40;

    [McpServerTool(Name = "list_targets", Title = "List deploy targets", ReadOnly = true, Idempotent = true)]
    [Description("Lists the solutions and projects added to Solution Deployer and their deploy targets: publish profiles " +
                 "and deployment scripts. Use a target's id with deploy, list_backups and get_release_summary. " +
                 "credentialsReady=false means the target needs a password that is not saved in the app; save it there first.")]
    public async Task<DeployerSnapshot> ListTargets(
        [Description("Optional text to match against source, project, target or destination names.")] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await Call(() => host.GetTargetsAsync(cancellationToken));
        if (string.IsNullOrWhiteSpace(filter))
            return snapshot;

        bool Match(string? text) => text?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) == true;
        var sources = snapshot.Sources
            .Select(s => Match(s.Name) || Match(s.Path)
                ? s
                : s with { Targets = s.Targets.Where(t => Match(t.Name) || Match(t.Project) || Match(t.Destination)).ToList() })
            .Where(s => s.Targets.Count > 0)
            .ToList();
        return snapshot with { Sources = sources };
    }

    [McpServerTool(Name = "refresh_targets", Title = "Reload targets from disk", Idempotent = true)]
    [Description("Re-reads every added solution and project from disk, picking up new or changed publish profiles and scripts. " +
                 "Returns the refreshed target list.")]
    public async Task<DeployerSnapshot> RefreshTargets(CancellationToken cancellationToken = default)
    {
        await Call(async () =>
        {
            await host.RefreshAsync(cancellationToken);
            return true;
        });
        return await Call(() => host.GetTargetsAsync(cancellationToken));
    }

    [McpServerTool(Name = "deploy", Title = "Deploy targets", Destructive = true, OpenWorld = true)]
    [Description("Builds and publishes the given targets to their servers, exactly as 'Deploy Selected' does in the app. " +
                 "This changes live servers: confirm the targets with the user first. Returns immediately with a run; " +
                 "follow it with get_run until its state is no longer 'running'. Only one deploy or restore runs at a time.")]
    public async Task<RunSnapshot> Deploy(
        [Description("Ids of the targets to deploy, from list_targets.")] string[] targetIds,
        [Description("Deploy unrelated targets in parallel. Defaults to the app's setting.")] bool? runInParallel = null,
        [Description("Snapshot each target before publishing, so it can be restored. Defaults to the app's setting.")] bool? backupBeforePublish = null,
        CancellationToken cancellationToken = default)
    {
        if (targetIds is not { Length: > 0 })
            throw new McpException("Pass at least one target id (see list_targets).");

        var run = await Call(() => host.StartDeployAsync(
            new DeployRequest(targetIds.Distinct().ToList(), runInParallel, backupBeforePublish), cancellationToken));
        runs.Add(run);
        return run.Snapshot(DefaultLogLines);
    }

    [McpServerTool(Name = "get_run", Title = "Get deploy/restore progress", ReadOnly = true)]
    [Description("Reports a deploy or restore run: its state (running, succeeded, failed, cancelled), each target's " +
                 "result and the latest output lines. Set waitSeconds to wait for the run to finish before answering.")]
    public async Task<RunSnapshot> GetRun(
        [Description("The run id returned by deploy or restore_backup. Omit for the most recent run.")] string? runId = null,
        [Description($"Wait up to this many seconds (max 50) for the run to finish. 0 answers at once.")] int waitSeconds = 0,
        [Description("How many of the latest output lines to include (default 40, max 500).")] int logLines = DefaultLogLines,
        CancellationToken cancellationToken = default)
    {
        var run = FindRun(runId);
        await run.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds)), cancellationToken);
        return run.Snapshot(Math.Clamp(logLines, 0, 500));
    }

    [McpServerTool(Name = "cancel_run", Title = "Cancel a run")]
    [Description("Stops a running deploy or restore. Targets already published stay published.")]
    public RunSnapshot CancelRun(
        [Description("The run id. Omit for the most recent run.")] string? runId = null)
    {
        var run = FindRun(runId);
        if (!run.RequestCancel())
            throw new McpException($"Run {run.Id} already finished ({run.State.ToString().ToLowerInvariant()}).");
        return run.Snapshot(DefaultLogLines);
    }

    [McpServerTool(Name = "list_backups", Title = "List a target's snapshots", ReadOnly = true, Idempotent = true)]
    [Description("Lists the snapshots taken of a target before its earlier deploys, newest first. " +
                 "Pass a snapshot's id to restore_backup to roll the target back.")]
    public Task<IReadOnlyList<BackupInfo>> ListBackups(
        [Description("The target id, from list_targets.")] string targetId,
        CancellationToken cancellationToken = default) =>
        Call(() => host.ListBackupsAsync(targetId, cancellationToken));

    [McpServerTool(Name = "restore_backup", Title = "Restore a snapshot", Destructive = true, OpenWorld = true)]
    [Description("Rolls a target's server back to how it was before the snapshot's deploy. A partial (Web Deploy) " +
                 "snapshot also undoes every newer deploy. This changes live servers: confirm with the user first. " +
                 "Returns a run to follow with get_run.")]
    public async Task<RunSnapshot> RestoreBackup(
        [Description("The target id, from list_targets.")] string targetId,
        [Description("The snapshot id, from list_backups.")] string backupId,
        CancellationToken cancellationToken = default)
    {
        var run = await Call(() => host.StartRestoreAsync(targetId, backupId, cancellationToken));
        runs.Add(run);
        return run.Snapshot(DefaultLogLines);
    }

    [McpServerTool(Name = "get_release_summary", Title = "What changed since the last deploy", ReadOnly = true, Idempotent = true)]
    [Description("Lists the git commits a publish profile's project and the projects it references gained since that " +
                 "profile was last deployed from this app. Useful before deploying, or to write release notes.")]
    public Task<string> GetReleaseSummary(
        [Description("The id of a publish-profile target, from list_targets.")] string targetId,
        CancellationToken cancellationToken = default) =>
        Call(() => host.GetReleaseSummaryAsync(targetId, cancellationToken));

    private DeploymentRun FindRun(string? runId) =>
        runs.Find(runId) ?? throw new McpException(string.IsNullOrWhiteSpace(runId)
            ? "No deploy or restore has been started over MCP yet."
            : $"No run with id '{runId}'. Only the {RunRegistry.Capacity} most recent runs are kept.");

    /// <summary>Turns the host's caller-facing errors into tool errors the client shows as-is.</summary>
    private static async Task<T> Call<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (DeployerException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
