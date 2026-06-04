namespace SolutionDeployer.Core.Publishing;

/// <summary>A resolved interpreter: the executable to launch plus the args that precede the script path.</summary>
public sealed record ResolvedInterpreter(string FileName, IReadOnlyList<string> LeadingArgs);

/// <summary>
/// Maps a script's file extension to the interpreter that runs it, and checks the interpreter is
/// available on this machine. Supports PowerShell (.ps1), shell (.sh/.bash) and Windows batch (.cmd/.bat).
/// </summary>
public static class ScriptInterpreters
{
    public static readonly string[] SupportedExtensions = [".ps1", ".sh", ".bash", ".cmd", ".bat"];

    public static bool IsSupported(string scriptPath) =>
        SupportedExtensions.Contains(Path.GetExtension(scriptPath), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the interpreter for <paramref name="scriptPath"/>. Returns false with a reason when the
    /// extension is unsupported or the interpreter is not installed.
    /// </summary>
    public static bool TryResolve(string scriptPath, out ResolvedInterpreter interpreter, out string? error)
    {
        interpreter = null!;
        error = null;
        var ext = Path.GetExtension(scriptPath).ToLowerInvariant();

        switch (ext)
        {
            case ".ps1":
                if (OnPath("pwsh"))
                    interpreter = new ResolvedInterpreter("pwsh", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"]);
                else if (OperatingSystem.IsWindows() && OnPath("powershell"))
                    interpreter = new ResolvedInterpreter("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"]);
                else
                {
                    error = "PowerShell ('pwsh') was not found on PATH. Install PowerShell to run .ps1 scripts.";
                    return false;
                }
                return true;

            case ".sh":
            case ".bash":
                if (OnPath("bash"))
                    interpreter = new ResolvedInterpreter("bash", []);
                else
                {
                    error = "'bash' was not found on PATH.";
                    return false;
                }
                return true;

            case ".cmd":
            case ".bat":
                if (!OperatingSystem.IsWindows())
                {
                    error = ".cmd/.bat scripts can only run on Windows.";
                    return false;
                }
                interpreter = new ResolvedInterpreter("cmd", ["/c"]);
                return true;

            default:
                error = $"Unsupported script type '{ext}'. Supported: {string.Join(", ", SupportedExtensions)}.";
                return false;
        }
    }

    /// <summary>Whether the interpreter for this script exists on the current machine.</summary>
    public static bool IsAvailable(string scriptPath, out string? error) => TryResolve(scriptPath, out _, out error);

    private static bool OnPath(string executable)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
            return false;

        var candidates = OperatingSystem.IsWindows()
            ? new[] { executable + ".exe", executable + ".cmd", executable + ".bat", executable }
            : [executable];

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in candidates)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir.Trim('"'), name)))
                        return true;
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return false;
    }
}
