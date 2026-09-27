using System.Diagnostics;

namespace SolutionDeployer.Core.Publishing;

/// <summary>How long one step of a job took, e.g. "preview build".</summary>
public sealed record StepTiming(string Step, TimeSpan Duration);

/// <summary>
/// Collects how long each step of one job (preview build, change check, backup, publish …) took, so a slow
/// deploy shows where its time went. Thread-safe; a step is recorded even when it throws.
/// </summary>
public sealed class StepTimings
{
    private readonly List<StepTiming> _steps = [];

    public IReadOnlyList<StepTiming> Steps
    {
        get
        {
            lock (_steps)
                return [.. _steps];
        }
    }

    public void Add(string step, TimeSpan duration)
    {
        lock (_steps)
            _steps.Add(new StepTiming(step, duration));
    }

    public async Task<T> MeasureAsync<T>(string step, Func<Task<T>> action)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Add(step, Stopwatch.GetElapsedTime(started));
        }
    }

    public async Task MeasureAsync(string step, Func<Task> action) =>
        await MeasureAsync(step, async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

    /// <summary>e.g. "2m 31s total — preview build 1m 12s · change check 24s · deploy 49s".</summary>
    public static string Describe(TimeSpan total, IReadOnlyList<StepTiming> steps) =>
        steps.Count == 0
            ? $"{Format(total)} total"
            : $"{Format(total)} total — {string.Join(" · ", steps.Select(s => $"{s.Step} {Format(s.Duration)}"))}";

    /// <summary>"850ms", "12.3s", "2m 05s", "1h 03m".</summary>
    public static string Format(TimeSpan duration) => duration switch
    {
        { TotalSeconds: < 1 } => $"{duration.TotalMilliseconds:F0}ms",
        { TotalMinutes: < 1 } => $"{duration.TotalSeconds:F1}s",
        { TotalHours: < 1 } => $"{(int)duration.TotalMinutes}m {duration.Seconds:D2}s",
        _ => $"{(int)duration.TotalHours}h {duration.Minutes:D2}m",
    };
}
