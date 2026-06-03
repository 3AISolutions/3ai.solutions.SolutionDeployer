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

    public TimeSpan Duration { get; init; }

    /// <summary>The command line that was executed (with secrets redacted), for diagnostics.</summary>
    public string? CommandLine { get; init; }

    public string? ErrorMessage { get; init; }

    public bool IsSuccess => Status == PublishStatus.Succeeded;
}
