using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SolutionDeployer.Core.Publishing;

/// <summary>
/// Locates a full <c>msbuild.exe</c> on Windows via vswhere (required for Web Deploy / MSDeploy
/// publishing). Returns null on non-Windows platforms or when no Visual Studio / Build Tools install
/// is found.
/// </summary>
public sealed class MsBuildLocator
{
    // Thread-safe: parallel jobs call Locate() at the same moment, and a plain "resolved" flag set before
    // the lookup finished made the others see null ("Could not locate msbuild.exe").
    private readonly Lazy<string?> _path;

    public MsBuildLocator() =>
        _path = new Lazy<string?>(() => IsSupported ? LocateViaVsWhere() : null, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public string? Locate() => _path.Value;

    private static string? LocateViaVsWhere()
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var vsWhere = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vsWhere))
            return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = vsWhere,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
                     {
                         "-latest",
                         "-prerelease",
                         "-products", "*",
                         "-requires", "Microsoft.Component.MSBuild",
                         "-find", "MSBuild\\**\\Bin\\MSBuild.exe",
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            var path = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(File.Exists);

            return path;
        }
        catch
        {
            return null;
        }
    }
}
