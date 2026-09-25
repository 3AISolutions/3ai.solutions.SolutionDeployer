using System.Collections.Concurrent;
using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Projects;

namespace SolutionDeployer.Core.Publishing;

/// <summary>Output line tagged with the job that produced it.</summary>
public readonly record struct JobOutput(string JobId, string JobDisplayName, OutputLine Line);

public sealed class DeploymentRunOptions
{
    /// <summary>
    /// Run unrelated projects concurrently rather than one-at-a-time. Jobs whose builds touch a common
    /// project — the same project, or a shared <c>ProjectReference</c> — still run sequentially, since
    /// concurrent builds of one project collide.
    /// </summary>
    public bool RunInParallel { get; init; }

    /// <summary>Max concurrent publishes when <see cref="RunInParallel"/> is true.</summary>
    public int MaxParallelism { get; init; } = 4;

    /// <summary>In sequential mode, stop after the first failed job.</summary>
    public bool StopOnFirstFailure { get; init; }

    /// <summary>Snapshot the current deployment before each publish or script run (where supported).</summary>
    public bool BackupBeforePublish { get; init; }
}

/// <summary>
/// Runs a batch of <see cref="PublishJob"/>s (any combination of project+profile selections),
/// streaming tagged output and reporting per-job results as they complete.
/// </summary>
public sealed class DeploymentRunner(IPublishEngineFactory engineFactory, IBackupService backupService)
{
    public async Task<IReadOnlyList<PublishResult>> RunAsync(
        IReadOnlyList<PublishJob> jobs,
        DeploymentRunOptions options,
        Action<JobOutput> onOutput,
        Action<PublishResult> onJobCompleted,
        CancellationToken cancellationToken = default)
    {
        if (jobs.Count == 0)
            return [];

        var results = new ConcurrentBag<PublishResult>();

        async Task RunOne(PublishJob job)
        {
            var engine = engineFactory.Get(job.Engine);
            void Sink(OutputLine line) => onOutput(new JobOutput(job.Id, job.DisplayName, line));

            if (options.BackupBeforePublish)
                await TryBackupAsync(job, Sink, cancellationToken).ConfigureAwait(false);

            PublishResult result;
            try
            {
                result = await engine.PublishAsync(job, Sink, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new PublishResult
                {
                    JobId = job.Id,
                    DisplayName = job.DisplayName,
                    Status = PublishStatus.Cancelled,
                    ErrorMessage = "Cancelled.",
                };
            }
            catch (Exception ex)
            {
                Sink(OutputLine.Error(ex.Message));
                result = new PublishResult
                {
                    JobId = job.Id,
                    DisplayName = job.DisplayName,
                    Status = PublishStatus.Failed,
                    ErrorMessage = ex.Message,
                };
            }

            results.Add(result);
            onJobCompleted(result);
        }

        if (options.RunInParallel)
        {
            // Parallelize across unrelated builds only: two jobs that build a common project — the same
            // one, or a shared ProjectReference such as a common class library — would restore and build
            // into the same obj/bin folders at once and fail, so those run one after another.
            var byProject = GroupByBuildOverlap(jobs);

            await Parallel.ForEachAsync(
                byProject,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism),
                    CancellationToken = cancellationToken,
                },
                async (projectJobs, ct) =>
                {
                    foreach (var job in projectJobs)
                    {
                        ct.ThrowIfCancellationRequested();
                        await RunOne(job).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
        }
        else
        {
            foreach (var job in jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RunOne(job).ConfigureAwait(false);

                if (options.StopOnFirstFailure &&
                    results.FirstOrDefault(r => r.JobId == job.Id)?.IsSuccess == false)
                {
                    break;
                }
            }
        }

        // Preserve the input order in the returned summary.
        var byId = results.ToDictionary(r => r.JobId);
        return jobs
            .Where(j => byId.ContainsKey(j.Id))
            .Select(j => byId[j.Id])
            .ToList();
    }

    /// <summary>
    /// Splits jobs into groups that may run in parallel with each other: jobs whose builds share any
    /// project (see <see cref="ProjectGraph.BuildClosure"/>) end up in the same group, in their original
    /// order. E.g. two web apps that both reference one class library land together.
    /// </summary>
    internal static List<List<PublishJob>> GroupByBuildOverlap(IReadOnlyList<PublishJob> jobs)
    {
        // Union-find over job indices, joining any two jobs that build a common project file.
        var parent = Enumerable.Range(0, jobs.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        var firstBuilder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var closures = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < jobs.Count; i++)
        {
            var projectPath = Path.GetFullPath(jobs[i].Project.ProjectPath);
            if (!closures.TryGetValue(projectPath, out var closure))
                closures[projectPath] = closure = ProjectGraph.BuildClosure(projectPath);

            foreach (var project in closure)
            {
                if (firstBuilder.TryGetValue(project, out var other))
                    parent[Find(i)] = Find(other);
                else
                    firstBuilder[project] = i;
            }
        }

        return Enumerable.Range(0, jobs.Count)
            .GroupBy(Find)
            .Select(group => group.Select(i => jobs[i]).ToList())
            .ToList();
    }

    /// <summary>
    /// Best-effort backup before a publish. Skips scripts without a backup target and unsupported
    /// profiles, and treats a
    /// backup failure as a logged warning rather than aborting the publish.
    /// </summary>
    private async Task TryBackupAsync(PublishJob job, Action<OutputLine> sink, CancellationToken cancellationToken)
    {
        var owner = BackupOwner.ForJob(job);

        // A script without a backup target simply isn't backed up — not worth a "skipped" line every run.
        if (owner is null || owner.Script?.BackupKind == ScriptBackupKind.None)
            return;

        if (!backupService.CanBackUp(owner, out var reason))
        {
            sink(OutputLine.Info($"[backup] Skipped — {reason}"));
            return;
        }

        try
        {
            await backupService.BackUpAsync(job, sink, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sink(OutputLine.Error($"[backup] Failed: {ex.Message}. Proceeding with publish."));
        }
    }
}
