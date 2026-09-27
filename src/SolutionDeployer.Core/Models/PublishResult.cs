using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Models;

public enum PublishStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// Outcome of a single <see cref="PublishJob"/>.
/// </summary>
public sealed class PublishResult
{
    public required string JobId { get; init; }

    public required string DisplayName { get; init; }

    public required PublishStatus Status { get; init; }

    public int ExitCode { get; init; }

    /// <summary>
    /// Wall-clock time of the whole job once <c>DeploymentRunner</c> has run it (backup included); an engine
    /// reports just its own process time.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Where <see cref="Duration"/> went, step by step (preview build, change check, backup, publish …).</summary>
    public IReadOnlyList<StepTiming> Steps { get; init; } = [];

    /// <summary>e.g. "2m 31s total — preview build 1m 12s · change check 24s · deploy 49s".</summary>
    public string TimingText => StepTimings.Describe(Duration, Steps);

    /// <summary>The command line that was executed (with secrets redacted), for diagnostics.</summary>
    public string? CommandLine { get; init; }

    public string? ErrorMessage { get; init; }

    public bool IsSuccess => Status == PublishStatus.Succeeded;
}
