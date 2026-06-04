using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Solutions;

/// <summary>
/// Resolves a <see cref="DeploymentSource"/> (solution or project) into its publishable projects.
/// Solutions are filtered to projects that have at least one publish profile (issue #8); a standalone
/// project is always included.
/// </summary>
public interface ISourceLoader
{
    Task<LoadedSource> LoadAsync(DeploymentSource source, CancellationToken cancellationToken = default);
}
