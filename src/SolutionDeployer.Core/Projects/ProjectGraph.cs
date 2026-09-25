using System.Xml.Linq;

namespace SolutionDeployer.Core.Projects;

/// <summary>Follows a project's <c>ProjectReference</c>s.</summary>
public static class ProjectGraph
{
    /// <summary>
    /// Every project file a build of <paramref name="projectPath"/> touches: the project itself plus its
    /// <c>ProjectReference</c>s, transitively (full paths). Unreadable or missing files end the walk there.
    /// </summary>
    public static IReadOnlySet<string> BuildClosure(string projectPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(projectPath));

        while (pending.Count > 0)
        {
            var path = pending.Pop();
            if (!seen.Add(path) || !File.Exists(path))
                continue;

            try
            {
                var directory = Path.GetDirectoryName(path)!;
                foreach (var reference in XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
                {
                    foreach (var include in (reference.Attribute("Include")?.Value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        // A reference built from MSBuild properties can't be resolved without evaluating the project.
                        if (include.Contains("$("))
                            continue;

                        pending.Push(Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar))));
                    }
                }
            }
            catch
            {
                // Not a readable project file; nothing more to follow from it.
            }
        }

        return seen;
    }
}
