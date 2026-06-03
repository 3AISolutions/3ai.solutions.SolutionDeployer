using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Profiles;

namespace SolutionDeployer.Core.Solutions;

/// <summary>
/// Parses .sln and .slnx using the official <c>Microsoft.VisualStudio.SolutionPersistence</c>
/// serializer (the same one that backs <c>dotnet sln</c>).
/// </summary>
public sealed class SolutionParser(IProfileDiscovery profileDiscovery) : ISolutionParser
{
    private static readonly string[] PublishableExtensions = [".csproj", ".fsproj", ".vbproj"];

    public async Task<DeploymentSolution> ParseAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(solutionPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Solution file not found.", fullPath);

        var serializer = SolutionSerializers.GetSerializerByMoniker(fullPath)
            ?? throw new NotSupportedException($"No solution serializer handles '{fullPath}'. Expected .sln or .slnx.");

        SolutionModel model = await serializer.OpenAsync(fullPath, cancellationToken).ConfigureAwait(false);

        var solutionDir = Path.GetDirectoryName(fullPath)!;
        var projects = new List<DeploymentProject>();

        foreach (SolutionProjectModel projectModel in model.SolutionProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = projectModel.FilePath.Replace('\\', Path.DirectorySeparatorChar);
            var projectPath = Path.GetFullPath(Path.Combine(solutionDir, relative));

            if (!PublishableExtensions.Contains(Path.GetExtension(projectPath), StringComparer.OrdinalIgnoreCase))
                continue;

            if (!File.Exists(projectPath))
                continue;

            var profiles = profileDiscovery.DiscoverProfiles(projectPath);

            projects.Add(new DeploymentProject
            {
                Name = Path.GetFileNameWithoutExtension(projectPath),
                ProjectPath = projectPath,
                Profiles = profiles,
            });
        }

        return new DeploymentSolution
        {
            SolutionPath = fullPath,
            Projects = projects
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }
}
