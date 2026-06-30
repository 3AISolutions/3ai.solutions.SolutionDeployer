using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Git;

/// <summary>
/// Drives the <c>git</c> CLI (via <see cref="ProcessRunner"/>) to summarise commit history. Each
/// project is resolved against its own repository, so cross-repo dependencies are handled.
/// </summary>
public sealed class GitHistoryService(ProcessRunner processRunner) : IGitHistoryService
{
    private const char FieldSeparator = (char)0x1f; // ASCII unit separator, safe inside commit subjects.
    private const int MaxCommitsWithoutBaseline = 20;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var (exit, _) = await RunGitAsync(null, ["--version"], cancellationToken).ConfigureAwait(false);
        return exit == 0;
    }

    public async Task<ReleaseSummary> BuildSummaryAsync(
        string deployedProjectPath,
        string deployedProjectName,
        IReadOnlyDictionary<string, string> previousShas,
        CancellationToken cancellationToken = default)
    {
        var closure = ProjectReferenceResolver.ResolveClosure(deployedProjectPath);
        var gitAvailable = await IsAvailableAsync(cancellationToken).ConfigureAwait(false);

        var histories = new List<ProjectHistory>();
        var first = true;
        foreach (var projectPath in closure)
        {
            histories.Add(await BuildProjectHistoryAsync(
                projectPath, isRoot: first, gitAvailable, previousShas, cancellationToken).ConfigureAwait(false));
            first = false;
        }

        return new ReleaseSummary { DeployedProjectName = deployedProjectName, Projects = histories };
    }

    public async Task<IReadOnlyDictionary<string, string>> CaptureShasAsync(
        string deployedProjectPath,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            return result;

        foreach (var projectPath in ProjectReferenceResolver.ResolveClosure(deployedProjectPath))
        {
            var dir = SafeDirectory(projectPath);
            if (dir is null)
                continue;

            var sha = await HeadShaAsync(dir, cancellationToken).ConfigureAwait(false);
            if (sha is not null)
                result[projectPath] = sha;
        }

        return result;
    }

    private async Task<ProjectHistory> BuildProjectHistoryAsync(
        string projectPath,
        bool isRoot,
        bool gitAvailable,
        IReadOnlyDictionary<string, string> previousShas,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileNameWithoutExtension(projectPath);
        var dir = SafeDirectory(projectPath);

        if (!gitAvailable)
            return new ProjectHistory { ProjectName = name, ProjectPath = projectPath, IsRoot = isRoot, Note = "git was not found on PATH." };

        if (dir is null || await RepoRootAsync(dir, cancellationToken).ConfigureAwait(false) is not { } repoRoot)
            return new ProjectHistory { ProjectName = name, ProjectPath = projectPath, IsRoot = isRoot, Note = "Not under a git repository." };

        var head = await HeadShaAsync(dir, cancellationToken).ConfigureAwait(false);
        var branch = await RunFirstLineAsync(dir, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);
        var dirty = await IsDirtyAsync(dir, cancellationToken).ConfigureAwait(false);
        previousShas.TryGetValue(projectPath, out var previous);

        var (commits, note) = await LogAsync(dir, previous, cancellationToken).ConfigureAwait(false);

        return new ProjectHistory
        {
            ProjectName = name,
            ProjectPath = projectPath,
            IsRoot = isRoot,
            RepoRoot = repoRoot,
            Branch = branch,
            IsDirty = dirty,
            PreviousSha = previous,
            CurrentSha = head,
            Commits = commits,
            Note = note,
        };
    }

    private async Task<(IReadOnlyList<CommitInfo> Commits, string? Note)> LogAsync(
        string dir, string? previousSha, CancellationToken cancellationToken)
    {
        var format = $"--pretty=format:%h{FieldSeparator}%s{FieldSeparator}%an{FieldSeparator}%ad";
        string[] baseArgs = ["log", "--no-merges", "--date=short", format];

        // With a baseline, list commits since it; otherwise show the most recent commits.
        if (!string.IsNullOrWhiteSpace(previousSha))
        {
            var (exit, lines) = await RunGitAsync(dir,
                [.. baseArgs, $"{previousSha}..HEAD", "--", "."], cancellationToken).ConfigureAwait(false);

            if (exit == 0)
            {
                var commits = ParseCommits(lines);
                return (commits, commits.Count == 0 ? "No changes since the last deployment." : null);
            }

            // Baseline not in history (e.g. force-push/rebase) — fall back to recent commits.
            var (fbExit, fbLines) = await RunGitAsync(dir,
                [.. baseArgs, $"-n{MaxCommitsWithoutBaseline}", "--", "."], cancellationToken).ConfigureAwait(false);
            return (ParseCommits(fbLines),
                fbExit == 0 ? "Previous deploy commit not found in history; showing recent commits." : "Could not read git history.");
        }

        var (firstExit, firstLines) = await RunGitAsync(dir,
            [.. baseArgs, $"-n{MaxCommitsWithoutBaseline}", "--", "."], cancellationToken).ConfigureAwait(false);
        return (ParseCommits(firstLines),
            firstExit == 0 ? "First recorded deployment; showing recent commits." : "Could not read git history.");
    }

    private static IReadOnlyList<CommitInfo> ParseCommits(IReadOnlyList<string> lines)
    {
        var commits = new List<CommitInfo>();
        foreach (var line in lines)
        {
            var parts = line.Split(FieldSeparator);
            if (parts.Length == 4)
                commits.Add(new CommitInfo(parts[0], parts[1], parts[2], parts[3]));
        }

        return commits;
    }

    private async Task<string?> RepoRootAsync(string dir, CancellationToken ct) =>
        await RunFirstLineAsync(dir, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false);

    private async Task<string?> HeadShaAsync(string dir, CancellationToken ct) =>
        await RunFirstLineAsync(dir, ["rev-parse", "HEAD"], ct).ConfigureAwait(false);

    private async Task<bool> IsDirtyAsync(string dir, CancellationToken ct)
    {
        var (exit, lines) = await RunGitAsync(dir, ["status", "--porcelain"], ct).ConfigureAwait(false);
        return exit == 0 && lines.Any(l => l.Trim().Length > 0);
    }

    private async Task<string?> RunFirstLineAsync(string dir, string[] args, CancellationToken ct)
    {
        var (exit, lines) = await RunGitAsync(dir, args, ct).ConfigureAwait(false);
        return exit == 0 ? lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() : null;
    }

    private async Task<(int Exit, List<string> Lines)> RunGitAsync(
        string? workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        try
        {
            var result = await processRunner.RunAsync(
                "git", args, workingDirectory,
                line => { if (line.Severity == OutputSeverity.Info) lines.Add(line.Text); },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return (result.ExitCode, lines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // git not found, or failed to start.
            return (-1, lines);
        }
    }

    private static string? SafeDirectory(string projectPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            return Directory.Exists(dir) ? dir : null;
        }
        catch
        {
            return null;
        }
    }
}
