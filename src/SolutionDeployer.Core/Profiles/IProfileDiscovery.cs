using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Profiles;

/// <summary>
/// Discovers publish profiles for a project by scanning conventional locations.
/// </summary>
public interface IProfileDiscovery
{
    IReadOnlyList<PublishProfile> DiscoverProfiles(string projectFilePath);
}
