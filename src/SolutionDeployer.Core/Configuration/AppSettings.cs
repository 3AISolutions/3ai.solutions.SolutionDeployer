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

    /// <summary>Remembered usernames keyed by profile file path (no passwords).</summary>
    public Dictionary<string, string> RememberedUserNames { get; set; } = new();

    /// <summary>GitHub "owner/repo" used by the updater to find releases.</summary>
    public string UpdateRepository { get; set; } = "3AISolutions/3ai.solutions.SolutionDeployer";

    /// <summary>Automatically check for updates on startup.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    public void AddRecentSolution(string path)
    {
        RecentSolutions.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentSolutions.Insert(0, path);
        if (RecentSolutions.Count > 10)
            RecentSolutions.RemoveRange(10, RecentSolutions.Count - 10);
    }
}
