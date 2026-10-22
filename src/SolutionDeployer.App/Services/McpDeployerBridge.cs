using Avalonia.Threading;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Mcp;

namespace SolutionDeployer.App.Services;

/// <summary>
/// Lets the MCP tools drive the main window's state: they see the same sources and rows, and a deploy they start
/// is the window's deploy (status dots, output, backups, release history). Everything touching the view models
/// runs on the UI thread.
/// </summary>
public sealed class McpDeployerBridge(MainWindowViewModel viewModel) : IDeployerHost
{
    public Task<DeployerSnapshot> GetTargetsAsync(CancellationToken cancellationToken = default) =>
        OnUi(() =>
        {
            var seen = new HashSet<string>();
            var sources = viewModel.Sources.Select(source => new SourceInfo(
                    source.Name,
                    source.Path,
                    source.Kind == SourceKind.Solution ? "solution" : "project",
                    source.Problem,
                    // A project added both on its own and through a solution is listed (and deployed) once.
                    source.Projects.SelectMany(Describe).Where(t => seen.Add(t.Id)).ToList()))
                .ToList();

            return new DeployerSnapshot(
                viewModel.IsRunning, viewModel.IsLoading, viewModel.RunInParallel, viewModel.BackupBeforePublish, sources);
        });

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        OnUi(async () =>
        {
            if (viewModel.RemoteBusyReason is { } busy)
                throw new DeployerException(busy);
            await viewModel.RefreshAllCommand.ExecuteAsync(null);
            return true;
        });

    public Task<DeploymentRun> StartDeployAsync(DeployRequest request, CancellationToken cancellationToken = default) =>
        OnUi(() =>
        {
            var targets = request.TargetIds.Select(Find).ToList();
            var run = new DeploymentRun("deploy", targets.Select(t => (IdOf(t), t.Name)));
            Start(run, viewModel.StartRemoteDeploy(targets, request.RunInParallel, request.BackupBeforePublish, new Observer(run)));
            return run;
        });

    public Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(string targetId, CancellationToken cancellationToken = default) =>
        OnUi(async () =>
        {
            var host = FindBackupHost(targetId);
            var backups = await viewModel.ListBackupsAsync(host);
            return (IReadOnlyList<BackupInfo>)backups
                .Select(b => new BackupInfo(
                    b.Id, b.Sequence, b.CreatedUtc, b.Kind.ToString(), b.IsPartial, b.DisplayName, b.Target))
                .ToList();
        });

    public Task<DeploymentRun> StartRestoreAsync(string targetId, string backupId, CancellationToken cancellationToken = default) =>
        OnUi(async () =>
        {
            var host = FindBackupHost(targetId);
            var backups = await viewModel.ListBackupsAsync(host);
            var backup = backups.FirstOrDefault(b => b.Id == backupId)
                         ?? throw new DeployerException($"{host.Name} has no snapshot '{backupId}' (see list_backups).");

            var run = new DeploymentRun("restore", [(targetId, host.Name)]);
            Start(run, viewModel.StartRemoteRestore(host, backup, new Observer(run)));
            return run;
        });

    public Task<string> GetReleaseSummaryAsync(string targetId, CancellationToken cancellationToken = default) =>
        OnUi(async () => Find(targetId) is ProfileViewModel profile
            ? await viewModel.BuildReleaseSummaryTextAsync(profile)
            : throw new DeployerException("Release summaries are only kept for publish profiles, not scripts."));

    private void Start(DeploymentRun run, string? error)
    {
        if (error is not null)
            throw new DeployerException(error);
        run.OnCancel(() => Dispatcher.UIThread.Post(() =>
        {
            if (viewModel.CancelCommand.CanExecute(null))
                viewModel.CancelCommand.Execute(null);
        }));
    }

    private IEnumerable<TargetInfo> Describe(ProjectViewModel project)
    {
        foreach (var profile in project.Profiles)
        {
            yield return new TargetInfo(
                IdOf(profile), "profile", profile.Name, project.Name,
                profile.Profile.WebPublishMethod, profile.EngineLabel, NullIfEmpty(profile.Target),
                profile.RequiresCredentials, !profile.RequiresCredentials || profile.Password.Length > 0,
                profile.SupportsBackup, null, LastStatus(profile), NullIfEmpty(profile.ResultText));
        }

        foreach (var script in project.ScriptTargets)
        {
            yield return new TargetInfo(
                IdOf(script), "script", script.Name, project.Name,
                "Script", null, script.Summary,
                script.RequiresCredentials, !script.RequiresCredentials || script.Password.Length > 0,
                script.SupportsBackup, script.Problem, LastStatus(script), NullIfEmpty(script.ResultText));
        }
    }

    private static string? LastStatus(ISelectableTarget target) =>
        target.IsQueued ? StatusName(target.Status) : null;

    private BackupHostViewModel Find(string targetId) =>
        viewModel.Sources.SelectMany(s => s.Projects)
            .SelectMany(p => p.Profiles.Cast<BackupHostViewModel>().Concat(p.ScriptTargets))
            .FirstOrDefault(t => string.Equals(IdOf(t), targetId?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new DeployerException($"No target with id '{targetId}'. Call list_targets for the current ids.");

    private BackupHostViewModel FindBackupHost(string targetId)
    {
        var host = Find(targetId);
        return host.SupportsBackup
            ? host
            : throw new DeployerException($"{host.Name} can't be backed up or restored.");
    }

    public static string IdOf(BackupHostViewModel target) => target switch
    {
        ProfileViewModel profile => TargetIds.ForProfile(profile.Profile.FilePath),
        ScriptTargetViewModel script => TargetIds.ForScript(script.Target.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    private static string StatusName(PublishStatus status) => status.ToString().ToLowerInvariant();

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    private static Task<T> OnUi<T>(Func<T> action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    private static Task<T> OnUi<T>(Func<Task<T>> action) => Dispatcher.UIThread.InvokeAsync(action);

    /// <summary>Feeds a view-model run into the MCP run record.</summary>
    private sealed class Observer(DeploymentRun run) : IRunObserver
    {
        public void Output(string line) => run.AppendLog(line);

        public void TargetChanged(BackupHostViewModel target, PublishStatus status, string? result) =>
            run.SetTargetStatus(IdOf(target), StatusName(status), result);

        public void Completed(PublishStatus status, string message)
        {
            // Targets the run never got to (cancelled, or an aborted run) shouldn't stay "pending".
            foreach (var target in run.Snapshot(0).Targets.Where(t => t.Status is "pending" or "running"))
                run.SetTargetStatus(target.Id, StatusName(PublishStatus.Cancelled), target.Result);

            run.Complete(status switch
            {
                PublishStatus.Succeeded => RunState.Succeeded,
                PublishStatus.Cancelled => RunState.Cancelled,
                _ => RunState.Failed,
            }, message);
        }
    }
}
