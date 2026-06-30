using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Configuration;

/// <summary>
/// Persisted user settings. Passwords are intentionally NOT stored here.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Most-recently-opened solution paths (newest first).</summary>
    public List<string> RecentSolutions { get; set; } = [];

    /// <summary>Default engine to preselect for new profiles.</summary>
    public PublishEngineKind DefaultEngine { get; set; } = PublishEngineKind.Dotnet;

    /// <summary>Run selected jobs in parallel by default.</summary>
    public bool RunInParallel { get; set; }

    /// <summary>Snapshot the current deployment before publishing (MSDeploy / FileSystem profiles).</summary>
    public bool BackupBeforePublish { get; set; }

    /// <summary>How many snapshots to keep per profile before the oldest are pruned.</summary>
    public int BackupRetention { get; set; } = 10;

    /// <summary>Named S3-compatible storage destinations available for backups (secret keys excluded).</summary>
    public List<S3BackupTarget> RemoteBackupTargets { get; set; } = [];

    /// <summary>
    /// Per-profile backup destination, keyed by profile file path. Value is
    /// <see cref="S3BackupTarget.LocalId"/> (or absent) for local disk, otherwise an
    /// <see cref="S3BackupTarget.Id"/>.
    /// </summary>
    public Dictionary<string, string> ProfileBackupTarget { get; set; } = new();

    /// <summary>The destination id configured for a profile, defaulting to local disk.</summary>
    public string GetBackupTargetId(string profileFilePath) =>
        ProfileBackupTarget.TryGetValue(profileFilePath, out var id) && !string.IsNullOrEmpty(id)
            ? id
            : S3BackupTarget.LocalId;

    public void SetBackupTargetId(string profileFilePath, string targetId)
    {
        if (string.IsNullOrEmpty(targetId) || targetId == S3BackupTarget.LocalId)
            ProfileBackupTarget.Remove(profileFilePath);
        else
            ProfileBackupTarget[profileFilePath] = targetId;
    }

    /// <summary>
    /// Per-profile record of the git commit SHAs deployed last time, keyed by profile file path. Used
    /// to summarise what changed since the previous deployment.
    /// </summary>
    public Dictionary<string, DeployRecord> DeployHistory { get; set; } = new();

    /// <summary>Remembered usernames keyed by profile file path (no passwords).</summary>
    public Dictionary<string, string> RememberedUserNames { get; set; } = new();

    /// <summary>GitHub "owner/repo" used by the updater to find releases.</summary>
    public string UpdateRepository { get; set; } = "3AISolutions/3ai.solutions.SolutionDeployer";

    /// <summary>Automatically check for updates on startup.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>The user-curated list of added items (solutions and standalone projects).</summary>
    public List<DeploymentSource> Sources { get; set; } = [];

    /// <summary>Re-load <see cref="Sources"/> (and their selections) on startup.</summary>
    public bool RestoreSourcesOnStartup { get; set; } = true;

    /// <summary>Ask for confirmation (listing the targets) before a deploy runs.</summary>
    public bool ConfirmBeforeDeploy { get; set; } = true;

    // --- Last window placement (restored on startup). Null until first saved. ---
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>
    /// Remembered target selections, keyed by source path (solution or project). Lets the same
    /// targets be re-checked when a source is reloaded.
    /// </summary>
    public Dictionary<string, List<SavedProfileSelection>> SavedSelections { get; set; } = new();

    /// <summary>
    /// User-defined script deployment targets, keyed by project file path (so they appear whether the
    /// project is added directly or via a solution).
    /// </summary>
    public Dictionary<string, List<ScriptTarget>> ScriptTargets { get; set; } = new();

    public IReadOnlyList<ScriptTarget> GetScriptTargets(string projectPath) =>
        ScriptTargets.TryGetValue(projectPath, out var list) ? list : [];

    public void SetScriptTargets(string projectPath, IEnumerable<ScriptTarget> targets)
    {
        var list = targets.ToList();
        if (list.Count == 0)
            ScriptTargets.Remove(projectPath);
        else
            ScriptTargets[projectPath] = list;
    }

    // --- Legacy (pre-source-list) fields, kept for one-time migration only. ---

    /// <summary>Legacy single last-opened solution. Superseded by <see cref="Sources"/>.</summary>
    public string? LastSolutionPath { get; set; }

    /// <summary>Legacy auto-load toggle. Superseded by <see cref="RestoreSourcesOnStartup"/>.</summary>
    public bool AutoLoadLastSolution { get; set; } = true;

    public void AddRecentSolution(string path)
    {
        RecentSolutions.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentSolutions.Insert(0, path);
        if (RecentSolutions.Count > 10)
            RecentSolutions.RemoveRange(10, RecentSolutions.Count - 10);
    }

    public bool AddSource(DeploymentSource source)
    {
        if (Sources.Any(s => s.Kind == source.Kind && PathEquals(s.Path, source.Path)))
            return false;
        Sources.Add(source);
        return true;
    }

    public void RemoveSource(DeploymentSource source)
    {
        Sources.RemoveAll(s => s.Kind == source.Kind && PathEquals(s.Path, source.Path));
        SavedSelections.Remove(source.Path);
    }

    /// <summary>One-time migration from the pre-source-list shape: seed Sources from the old last solution.</summary>
    public void MigrateLegacy()
    {
        if (Sources.Count == 0 && !string.IsNullOrEmpty(LastSolutionPath))
        {
            Sources.Add(DeploymentSource.Solution(LastSolutionPath));
            RestoreSourcesOnStartup = AutoLoadLastSolution;
        }
        LastSolutionPath = null;
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
