namespace SolutionDeployer.Core.Models;

/// <summary>
/// A parsed solution (.sln / .slnx) and its publishable projects.
/// </summary>
public sealed class DeploymentSolution
{
    /// <summary>Absolute path to the solution file.</summary>
    public required string SolutionPath { get; init; }

    public string Name => Path.GetFileNameWithoutExtension(SolutionPath);

    public IReadOnlyList<DeploymentProject> Projects { get; init; } = [];

    public override string ToString() => Name;
}
