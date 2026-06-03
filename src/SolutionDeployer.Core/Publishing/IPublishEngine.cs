using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Publishing;

/// <summary>
/// Executes a single <see cref="PublishJob"/> using a particular build tool.
/// </summary>
public interface IPublishEngine
{
    PublishEngineKind Kind { get; }

    /// <summary>Whether this engine can run on the current machine.</summary>
    bool IsAvailable(out string? unavailableReason);

    Task<PublishResult> PublishAsync(
        PublishJob job,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken = default);
}
