using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Solutions;

/// <summary>
/// Parses a .sln / .slnx file into a <see cref="DeploymentSolution"/> with projects and their profiles.
/// </summary>
public interface ISolutionParser
{
    Task<DeploymentSolution> ParseAsync(string solutionPath, CancellationToken cancellationToken = default);
}
