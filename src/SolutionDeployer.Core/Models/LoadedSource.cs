namespace SolutionDeployer.Core.Models;

/// <summary>A source that has been resolved from disk into its (filtered) set of publishable projects.</summary>
public sealed class LoadedSource
{
    public required DeploymentSource Source { get; init; }

    public required IReadOnlyList<DeploymentProject> Projects { get; init; }

    /// <summary>True when the underlying file no longer exists on disk.</summary>
    public bool IsMissing { get; init; }

    /// <summary>Set when the source could not be parsed.</summary>
    public string? Error { get; init; }
}
