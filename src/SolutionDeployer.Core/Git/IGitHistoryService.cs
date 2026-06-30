namespace SolutionDeployer.Core.Git;

/// <summary>
/// Produces "what changed since the last deploy" summaries from git, for a project and its transitive
/// project dependencies (each resolved against its own repository).
/// </summary>
public interface IGitHistoryService
{
    /// <summary>Whether a usable <c>git</c> executable is available.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a release summary for <paramref name="deployedProjectPath"/> and its dependencies, listing
    /// commits since the SHA recorded for each project in <paramref name="previousShas"/>.
    /// </summary>
    Task<ReleaseSummary> BuildSummaryAsync(
        string deployedProjectPath,
        string deployedProjectName,
        IReadOnlyDictionary<string, string> previousShas,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures the current HEAD SHA of each project in the closure (keyed by project path) so it can be
    /// stored as the baseline for the next summary. Projects not in a git repo are omitted.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> CaptureShasAsync(
        string deployedProjectPath,
        CancellationToken cancellationToken = default);
}
