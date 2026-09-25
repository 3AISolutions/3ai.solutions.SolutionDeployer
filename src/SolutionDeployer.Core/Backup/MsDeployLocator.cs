using System.Runtime.InteropServices;

namespace SolutionDeployer.Core.Backup;

/// <summary>
/// Locates <c>msdeploy.exe</c> (Web Deploy) on Windows. Web Deploy is bidirectional, so the same
/// executable used implicitly by an MSDeploy publish can also pull the currently-deployed content
/// back down for a backup. Returns null on non-Windows platforms or when Web Deploy is not installed.
/// </summary>
public sealed class MsDeployLocator
{
    // Thread-safe: parallel jobs call Locate() at the same moment (see MsBuildLocator).
    private readonly Lazy<string?> _path;

    public MsDeployLocator() =>
        _path = new Lazy<string?>(() => IsSupported ? LocateOnWindows() : null, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public string? Locate() => _path.Value;

    private static string? LocateOnWindows()
    {
        // Standard install layout: %ProgramFiles%\IIS\Microsoft Web Deploy V3\msdeploy.exe (also V2).
        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(programFiles))
                continue;

            foreach (var version in new[] { "V3", "V2" })
            {
                var candidate = Path.Combine(programFiles, "IIS", $"Microsoft Web Deploy {version}", "msdeploy.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
