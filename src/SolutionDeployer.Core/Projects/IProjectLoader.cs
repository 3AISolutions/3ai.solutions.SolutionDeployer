using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Projects;

/// <summary>Loads a single project file into a <see cref="DeploymentProject"/> with its profiles.</summary>
public interface IProjectLoader
{
    DeploymentProject Load(string projectFilePath);
}
