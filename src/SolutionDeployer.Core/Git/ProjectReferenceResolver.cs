using System.Xml.Linq;

namespace SolutionDeployer.Core.Git;

/// <summary>
/// Resolves the transitive closure of <c>&lt;ProjectReference&gt;</c>s for a project, so a release
/// summary can cover the deployed project and everything it depends on (across repositories).
/// </summary>
public static class ProjectReferenceResolver
{
    /// <summary>
    /// Returns the deployed project followed by its transitive project dependencies, de-duplicated by
    /// full path. Missing files and cycles are handled gracefully; order is deterministic (root first,
    /// then breadth-first discovery).
    /// </summary>
    public static IReadOnlyList<string> ResolveClosure(string projectPath)
    {
        var root = Path.GetFullPath(projectPath);
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        queue.Enqueue(root);
        seen.Add(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            ordered.Add(current);

            foreach (var reference in ReadProjectReferences(current))
            {
                if (seen.Add(reference))
                    queue.Enqueue(reference);
            }
        }

        return ordered;
    }

    private static IEnumerable<string> ReadProjectReferences(string projectPath)
    {
        if (!File.Exists(projectPath))
            yield break;

        XDocument doc;
        try
        {
            doc = XDocument.Load(projectPath);
        }
        catch
        {
            yield break;
        }

        var projectDir = Path.GetDirectoryName(projectPath)!;
        foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
                continue;

            // MSBuild uses backslashes in Include paths; normalise for non-Windows.
            var normalized = include.Replace('\\', Path.DirectorySeparatorChar);
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(projectDir, normalized));
            }
            catch
            {
                continue;
            }

            yield return full;
        }
    }
}
