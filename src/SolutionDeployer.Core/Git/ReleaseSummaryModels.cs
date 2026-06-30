namespace SolutionDeployer.Core.Git;

/// <summary>A single commit in a release summary.</summary>
public sealed record CommitInfo(string ShortSha, string Subject, string Author, string Date);

/// <summary>
/// The history of one project (the deployed project or one of its dependencies) since the last
/// recorded deployment. Each project resolves its own git repository, so a cross-repo dependency
/// is summarised against its own repo.
/// </summary>
public sealed class ProjectHistory
{
    public required string ProjectName { get; init; }
    public required string ProjectPath { get; init; }

    /// <summary>True for the project being deployed; false for a dependency.</summary>
    public bool IsRoot { get; init; }

    public string? RepoRoot { get; init; }
    public string? Branch { get; init; }
    public bool IsDirty { get; init; }

    /// <summary>SHA recorded at the previous deployment, if any.</summary>
    public string? PreviousSha { get; init; }

    /// <summary>Current HEAD SHA of the project's repo.</summary>
    public string? CurrentSha { get; init; }

    public IReadOnlyList<CommitInfo> Commits { get; init; } = [];

    /// <summary>Set when commits couldn't be produced (no repo, first deploy, git missing, etc.).</summary>
    public string? Note { get; init; }
}

/// <summary>A release summary across the deployed project and its transitive project dependencies.</summary>
public sealed class ReleaseSummary
{
    public required string DeployedProjectName { get; init; }
    public required IReadOnlyList<ProjectHistory> Projects { get; init; }

    /// <summary>Renders the summary as plain text (used for the log and copy-to-clipboard).</summary>
    public string ToPlainText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Release summary — {DeployedProjectName}");
        foreach (var p in Projects)
        {
            sb.AppendLine();
            var header = p.IsRoot ? $"■ {p.ProjectName} (deployed)" : $"  └ {p.ProjectName}";
            if (p.Branch is not null)
                header += $"  [{p.Branch}{(p.IsDirty ? " *dirty" : "")}]";
            sb.AppendLine(header);

            if (p.Note is not null)
                sb.AppendLine($"      {p.Note}");

            foreach (var c in p.Commits)
                sb.AppendLine($"      {c.ShortSha}  {c.Subject}  ({c.Author}, {c.Date})");
        }

        return sb.ToString();
    }
}
