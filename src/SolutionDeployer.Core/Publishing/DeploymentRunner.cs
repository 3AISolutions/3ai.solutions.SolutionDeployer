using System.Collections.Concurrent;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Publishing;

/// <summary>Output line tagged with the job that produced it.</summary>
public readonly record struct JobOutput(string JobId, string JobDisplayName, OutputLine Line);

public sealed class DeploymentRunOptions
{
    /// <summary>Run selected jobs concurrently rather than one-at-a-time.</summary>
    public bool RunInParallel { get; init; }

    /// <summary>Max concurrent publishes when <see cref="RunInParallel"/> is true.</summary>
    public int MaxParallelism { get; init; } = 4;

    /// <summary>In sequential mode, stop after the first failed job.</summary>
    public bool StopOnFirstFailure { get; init; }
}

/// <summary>
/// Runs a batch of <see cref="PublishJob"/>s (any combination of project+profile selections),
/// streaming tagged output and reporting per-job results as they complete.
/// </summary>
public sealed class DeploymentRunner(IPublishEngineFactory engineFactory)
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
            await Parallel.ForEachAsync(
                jobs,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism),
                    CancellationToken = cancellationToken,
                },
                async (job, _) => await RunOne(job).ConfigureAwait(false)).ConfigureAwait(false);
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
}
