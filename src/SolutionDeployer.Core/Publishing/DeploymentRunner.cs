using System.Collections.Concurrent;
using System.Diagnostics;
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

    /// <summary>
    /// With <see cref="BackupBeforePublish"/>, deploy a Web Deploy profile's backup preview build as it is instead
    /// of building the project a second time to publish it (see <see cref="PreparedDeployment"/>).
    /// </summary>
    public bool DeployBackupPreview { get; init; } = true;
}

/// <summary>
/// Runs a batch of <see cref="PublishJob"/>s (any combination of project+profile selections),
/// streaming tagged output and reporting per-job results as they complete. Each job's result carries how
/// long it took, step by step.
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

            var started = Stopwatch.GetTimestamp();
            var timings = new StepTimings();

            PublishResult result;
            PreparedDeployment? prepared = null;
            try
            {
                if (options.BackupBeforePublish)
                {
                    prepared = await TryBackupAsync(job, options.DeployBackupPreview, timings, Sink, cancellationToken)
                        .ConfigureAwait(false);
                }

                result = await PublishAsync(job, engine, prepared, timings, Sink, cancellationToken).ConfigureAwait(false);
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
            finally
            {
                prepared?.Dispose();
            }

            result = WithTimings(result, Stopwatch.GetElapsedTime(started), timings.Steps);
            Sink(OutputLine.Info($"[timing] {result.TimingText}"));

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
    /// Publishes the job: deploys the backup's <paramref name="prepared"/> build when there is one, and
    /// otherwise — or when that deploy fails — runs the job's engine.
    /// </summary>
    private static async Task<PublishResult> PublishAsync(
        PublishJob job,
        IPublishEngine engine,
        PreparedDeployment? prepared,
        StepTimings timings,
        Action<OutputLine> sink,
        CancellationToken cancellationToken)
    {
        var step = job.Script is null ? $"{job.Engine.ToString().ToLowerInvariant()} publish" : "script";

        if (prepared is { IsUpToDate: true })
        {
            sink(OutputLine.Info("[publish] The server already has this build — nothing to deploy."));
            return new PublishResult { JobId = job.Id, DisplayName = job.DisplayName, Status = PublishStatus.Succeeded };
        }

        if (prepared is not null)
        {
            var deployed = await timings.MeasureAsync("deploy", () => prepared.DeployAsync(sink, cancellationToken))
                .ConfigureAwait(false);
            if (deployed.IsSuccess)
                return deployed;

            // A partly applied sync is fine: the publish converges the server on the same build, and the backup
            // was taken before either touched it.
            sink(OutputLine.Error(
                $"[publish] Deploying the preview build failed ({deployed.ErrorMessage}) — publishing the usual way instead."));
        }

        return await timings.MeasureAsync(step, () => engine.PublishAsync(job, sink, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="result"/> timed as a whole job: <see cref="PublishResult.Duration"/> becomes the job's
    /// wall-clock time and <see cref="PublishResult.Steps"/> its breakdown.
    /// </summary>
    internal static PublishResult WithTimings(PublishResult result, TimeSpan total, IReadOnlyList<StepTiming> steps)
    {
        // Name the time no step accounts for (e.g. listing earlier snapshots in a remote store) when it's noticeable.
        var unaccounted = total - steps.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration);
        if (steps.Count > 0 && unaccounted > TimeSpan.FromSeconds(2))
            steps = [.. steps, new StepTiming("other", unaccounted)];

        return new PublishResult
        {
            JobId = result.JobId,
            DisplayName = result.DisplayName,
            Status = result.Status,
            ExitCode = result.ExitCode,
            Duration = total,
            Steps = steps,
            CommandLine = result.CommandLine,
            ErrorMessage = result.ErrorMessage,
        };
    }

    /// <summary>
    /// Best-effort backup before a publish. Skips scripts without a backup target and unsupported
    /// profiles, and treats a backup failure as a logged warning rather than aborting the publish.
    /// Returns the backup's build when the publish can deploy it instead of building again.
    /// </summary>
    private async Task<PreparedDeployment?> TryBackupAsync(
        PublishJob job,
        bool deployPreview,
        StepTimings timings,
        Action<OutputLine> sink,
        CancellationToken cancellationToken)
    {
        var owner = BackupOwner.ForJob(job);
        if (owner is null)
            return null;

        // Said every run: with backups on, a script that silently isn't backed up looks as if it were.
        if (owner.Script?.BackupKind == ScriptBackupKind.None)
        {
            sink(OutputLine.Info(
                "[backup] Skipped — this script has no backup set up. Choose one under Backup in the script's settings."));
            return null;
        }

        if (!backupService.CanBackUp(owner, out var reason))
        {
            sink(OutputLine.Info($"[backup] Skipped — {reason}"));
            return null;
        }

        try
        {
            var backup = await backupService.BackUpForPublishAsync(job, sink, timings, cancellationToken).ConfigureAwait(false);
            if (deployPreview)
                return backup.Deployment;

            backup.Deployment?.Dispose();
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sink(OutputLine.Error($"[backup] Failed: {ex.Message}. Proceeding with publish."));
            return null;
        }
    }
}
