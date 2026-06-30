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
                string[] psArgs = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"];
                // Prefer PowerShell 7 (pwsh), then fall back to Windows PowerShell. Probe well-known
                // install locations as well as PATH, since the app's PATH may not include either.
                var pwsh = Resolve("pwsh", PwshFallbackPaths());
                if (pwsh is not null)
                    interpreter = new ResolvedInterpreter(pwsh, psArgs);
                else if (Resolve("powershell", WindowsPowerShellFallbackPaths()) is { } winPs)
                    interpreter = new ResolvedInterpreter(winPs, psArgs);
                else
                {
                    error = OperatingSystem.IsWindows()
                        ? "Neither PowerShell 7 ('pwsh') nor Windows PowerShell ('powershell.exe') could be found. Install PowerShell to run .ps1 scripts."
                        : "PowerShell ('pwsh') was not found. Install PowerShell 7 (https://aka.ms/powershell) to run .ps1 scripts on this platform.";
                    return false;
                }
                return true;

            case ".sh":
            case ".bash":
                if (Resolve("bash", []) is { } bash)
                    interpreter = new ResolvedInterpreter(bash, []);
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

    /// <summary>
    /// Resolves <paramref name="executable"/> to a full path: first by scanning <c>PATH</c>, then by
    /// checking the supplied well-known fallback locations. Returns null when it cannot be found.
    /// </summary>
    private static string? Resolve(string executable, IReadOnlyList<string> fallbackPaths)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            var candidates = OperatingSystem.IsWindows()
                ? new[] { executable + ".exe", executable + ".cmd", executable + ".bat", executable }
                : [executable];

            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var name in candidates)
                {
                    try
                    {
                        var full = Path.Combine(dir.Trim('"'), name);
                        if (File.Exists(full))
                            return full;
                    }
                    catch
                    {
                        // Ignore malformed PATH entries.
                    }
                }
            }
        }

        return fallbackPaths.FirstOrDefault(File.Exists);
    }

    private static string[] PwshFallbackPaths()
    {
        if (!OperatingSystem.IsWindows())
            return ["/usr/bin/pwsh", "/usr/local/bin/pwsh", "/opt/microsoft/powershell/7/pwsh"];

        var paths = new List<string>();
        foreach (var pf in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (!string.IsNullOrEmpty(pf))
                paths.Add(Path.Combine(pf, "PowerShell", "7", "pwsh.exe"));
        }

        return [.. paths];
    }

    private static string[] WindowsPowerShellFallbackPaths()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return string.IsNullOrEmpty(system)
            ? []
            : [Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe")];
    }
}
