using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Profiles;

namespace SolutionDeployer.Core.Projects;

/// <summary>Builds a <see cref="DeploymentProject"/> from a single project file (profiles included even if none).</summary>
public sealed class ProjectLoader(IProfileDiscovery profileDiscovery) : IProjectLoader
{
    public static readonly string[] PublishableExtensions = [".csproj", ".fsproj", ".vbproj"];

    public DeploymentProject Load(string projectFilePath)
    {
        var fullPath = Path.GetFullPath(projectFilePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Project file not found.", fullPath);

        if (!PublishableExtensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"'{Path.GetFileName(fullPath)}' is not a publishable project (.csproj/.fsproj/.vbproj).");

        return new DeploymentProject
        {
            Name = Path.GetFileNameWithoutExtension(fullPath),
            ProjectPath = fullPath,
            Profiles = profileDiscovery.DiscoverProfiles(fullPath),
        };
    }
}
