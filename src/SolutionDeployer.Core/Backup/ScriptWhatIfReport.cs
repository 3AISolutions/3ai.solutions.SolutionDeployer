using System.Text.RegularExpressions;

namespace SolutionDeployer.Core.Backup;

/// <summary>One Web Deploy endpoint a script deploys to, and what its <c>-WhatIf</c> run says would change there.</summary>
public sealed record ScriptWhatIfTarget(string ComputerName, MsDeployChangeSet Changes);

/// <summary>
/// Reads the output of a script run with <c>-WhatIf</c>. The contract: before each target's changes the
/// script prints <c>SD-WHATIF-TARGET: &lt;msdeploy computerName&gt;</c> (e.g.
/// <c>https://host:8172/msdeploy.axd?site=MySite</c>), followed by msdeploy-style change lines
/// (<c>Info: Updating file (MySite\bin\App.dll).</c>) and a <c>Total changes: N</c> line.
/// </summary>
public static partial class ScriptWhatIfReport
{
    public const string TargetMarker = "SD-WHATIF-TARGET:";

    [GeneratedRegex(@"SD-WHATIF-TARGET:\s*(\S+)\s*$")]
    private static partial Regex MarkerLine();

    /// <summary>
    /// Splits the output into its targets. Throws when the script reported no target, or when a target's
    /// section has no "Total changes" line — its change list can't be trusted, so nothing may be skipped.
    /// </summary>
    public static IReadOnlyList<ScriptWhatIfTarget> Parse(IEnumerable<string> lines)
    {
        var sections = new List<(string ComputerName, List<string> Lines)>();
        foreach (var line in lines)
        {
            var marker = MarkerLine().Match(line);
            if (marker.Success)
                sections.Add((marker.Groups[1].Value, []));
            else if (sections.Count > 0)
                sections[^1].Lines.Add(line);
        }

        if (sections.Count == 0)
        {
            throw new InvalidOperationException(
                $"The script's -WhatIf run didn't report any target (no \"{TargetMarker}\" line).");
        }

        var targets = new List<ScriptWhatIfTarget>();
        foreach (var (computerName, sectionLines) in sections)
        {
            var changes = MsDeployChangeSet.Parse(sectionLines);
            if (changes.ReportedTotal is null)
            {
                throw new InvalidOperationException(
                    $"The script's -WhatIf run didn't finish reporting the changes for {computerName} (no \"Total changes\" line).");
            }

            if (changes.LooksUnparsed)
            {
                throw new InvalidOperationException(
                    $"The script's -WhatIf run reported changes for {computerName} that couldn't be read.");
            }

            targets.Add(new ScriptWhatIfTarget(computerName, changes));
        }

        return targets;
    }
}
