using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Projects;

namespace SolutionDeployer.Core.Solutions;

/// <inheritdoc />
public sealed class SourceLoader(ISolutionParser solutionParser, IProjectLoader projectLoader) : ISourceLoader
{
    public async Task<LoadedSource> LoadAsync(DeploymentSource source, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(source.Path))
            return new LoadedSource { Source = source, Projects = [], IsMissing = true };

        try
        {
            if (source.Kind == SourceKind.Solution)
            {
                var solution = await solutionParser.ParseAsync(source.Path, cancellationToken).ConfigureAwait(false);

                // Issue #8: when added from a solution, omit projects that have no publish profiles.
                var withProfiles = solution.Projects
                    .Where(p => p.Profiles.Count > 0)
                    .ToList();

                return new LoadedSource { Source = source, Projects = withProfiles };
            }

            var project = projectLoader.Load(source.Path);
            return new LoadedSource { Source = source, Projects = [project] };
        }
        catch (Exception ex)
        {
            return new LoadedSource { Source = source, Projects = [], Error = ex.Message };
        }
    }
}
