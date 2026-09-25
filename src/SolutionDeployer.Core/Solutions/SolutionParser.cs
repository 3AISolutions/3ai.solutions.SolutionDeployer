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
                SolutionPath = fullPath,
                SolutionTargetName = SolutionTargetName(projectModel),
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

    // The characters MSBuild replaces with '_' when naming a solution's per-project targets.
    private static readonly char[] TargetNameCharsToCleanse = ['%', '$', '@', ';', '.', '(', ')', '\''];

    /// <summary>
    /// The target MSBuild generates for a project when building a solution: its solution folders and
    /// name, joined with '\', each cleansed the way MSBuild does (e.g. <c>eBooking\SpiderNet_eBooking</c>).
    /// </summary>
    internal static string SolutionTargetName(SolutionProjectModel project)
    {
        var segments = new List<string> { Cleanse(project.ActualDisplayName) };
        for (var folder = project.Parent; folder is not null; folder = folder.Parent)
            segments.Insert(0, Cleanse(folder.Name));

        return string.Join('\\', segments);

        static string Cleanse(string name)
        {
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(TargetNameCharsToCleanse, chars[i]) >= 0)
                    chars[i] = '_';
            }

            return new string(chars);
        }
    }
}
