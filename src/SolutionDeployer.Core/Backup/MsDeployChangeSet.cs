using System.Text.RegularExpressions;

namespace SolutionDeployer.Core.Backup;

/// <summary>
/// What an <c>msdeploy -verb:sync … -whatif</c> run reports it would do on the destination. Paths are
/// kept exactly as msdeploy prints them (e.g. <c>site\bin\App.dll</c>) so they can be fed straight back
/// to msdeploy as <c>contentPath</c> values.
/// </summary>
public sealed partial record MsDeployChangeSet(
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Added,
    int? ReportedTotal)
{
    /// <summary>Paths whose current server copy must be saved to undo the deploy (updated + deleted).</summary>
    public IReadOnlyList<string> ToSave => [.. Updated, .. Deleted];

    public bool IsEmpty => Updated.Count == 0 && Deleted.Count == 0 && Added.Count == 0;

    /// <summary>
    /// True when msdeploy's own summary reported changes but none of the per-item lines were recognised —
    /// i.e. the output format wasn't understood and the change set can't be trusted.
    /// </summary>
    public bool LooksUnparsed => IsEmpty && ReportedTotal > 0;

    // e.g. "Info: Updating file (site\bin\App.dll)." / "Info: Adding child filePath (site/x.txt)."
    [GeneratedRegex(@"^Info:\s+(Adding|Updating|Deleting)\s+(?:child\s+)?(file|filePath|directory|dirPath)\s+\((.+)\)\.?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ChangeLine();

    // e.g. "Total changes: 5 (2 added, 1 deleted, 2 updated, 0 parameters changed, 1234 bytes copied)"
    [GeneratedRegex(@"Total changes:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryLine();

    public static MsDeployChangeSet Parse(IEnumerable<string> lines)
    {
        var updated = new List<string>();
        var deletedFiles = new List<string>();
        var deletedDirs = new List<string>();
        var addedFiles = new List<string>();
        var addedDirs = new List<string>();
        int? total = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            var summary = SummaryLine().Match(line);
            if (summary.Success)
            {
                total = int.Parse(summary.Groups[1].Value);
                continue;
            }

            var match = ChangeLine().Match(line);
            if (!match.Success)
                continue;

            var action = match.Groups[1].Value.ToLowerInvariant();
            var isDirectory = match.Groups[2].Value.StartsWith("dir", StringComparison.OrdinalIgnoreCase);
            var path = match.Groups[3].Value.Trim();

            switch (action, isDirectory)
            {
                case ("updating", false): updated.Add(path); break;
                case ("deleting", false): deletedFiles.Add(path); break;
                case ("deleting", true): deletedDirs.Add(path); break;
                case ("adding", false): addedFiles.Add(path); break;
                case ("adding", true): addedDirs.Add(path); break;
                // "Updating directory" is an attribute change only — nothing to save.
            }
        }

        // A deleted/added directory covers everything inside it, so drop the children to keep the
        // save/delete lists minimal and free of overlaps.
        var deleted = Collapse(deletedDirs, deletedFiles);
        var added = Collapse(addedDirs, addedFiles);

        return new MsDeployChangeSet(Distinct(updated), deleted, added, total);
    }

    private static List<string> Collapse(List<string> directories, List<string> files)
    {
        var roots = Distinct(directories)
            .Where(d => !directories.Any(other => !Same(other, d) && IsUnder(d, other)))
            .ToList();

        return roots
            .Concat(Distinct(files).Where(f => !roots.Any(r => IsUnder(f, r))))
            .ToList();
    }

    private static List<string> Distinct(IEnumerable<string> paths) =>
        paths.DistinctBy(Normalize, StringComparer.OrdinalIgnoreCase).ToList();

    private static bool Same(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string directory) =>
        Normalize(path).StartsWith(Normalize(directory) + "\\", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');
}
