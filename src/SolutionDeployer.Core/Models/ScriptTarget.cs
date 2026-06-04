namespace SolutionDeployer.Core.Models;

/// <summary>
/// A user-defined script deployment attached to a project. The script (e.g. a <c>deploy_linux.ps1</c>)
/// performs the whole deployment; the app just runs it with the configured arguments, working
/// directory and environment. Persisted in settings; passwords are never stored here.
/// </summary>
public sealed class ScriptTarget
{
    /// <summary>Stable id used to key selections (and any future per-target state).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name, e.g. "Deploy to prod".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Path to the script. Stored relative to the project directory when the script lives under it
    /// (portable across clones), otherwise an absolute path.
    /// </summary>
    public string ScriptPath { get; set; } = string.Empty;

    /// <summary>Free-form argument string passed to the script (tokenised quote-aware at run time).</summary>
    public string? Arguments { get; set; }

    /// <summary>Working directory; relative paths resolve against the project dir. Null = project dir.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Extra environment variables exposed to the script (in addition to the SD_* context).</summary>
    public Dictionary<string, string> Environment { get; set; } = new();

    /// <summary>Resolves <see cref="ScriptPath"/> to an absolute path using the owning project's directory.</summary>
    public string ResolveScriptPath(string projectDirectory) => Resolve(ScriptPath, projectDirectory);

    /// <summary>Resolves the working directory (defaults to the project directory).</summary>
    public string ResolveWorkingDirectory(string projectDirectory) =>
        string.IsNullOrWhiteSpace(WorkingDirectory) ? projectDirectory : Resolve(WorkingDirectory, projectDirectory);

    private static string Resolve(string path, string projectDirectory) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(projectDirectory, path));

    /// <summary>
    /// Stores <paramref name="absolutePath"/> relative to <paramref name="projectDirectory"/> when it
    /// lives under it, else as an absolute path.
    /// </summary>
    public static string MakeStorablePath(string absolutePath, string projectDirectory)
    {
        var full = Path.GetFullPath(absolutePath);
        var root = Path.GetFullPath(projectDirectory);
        var relative = Path.GetRelativePath(root, full);
        // Use the relative form only when it actually stays under the project dir.
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? full
            : relative;
    }
}
