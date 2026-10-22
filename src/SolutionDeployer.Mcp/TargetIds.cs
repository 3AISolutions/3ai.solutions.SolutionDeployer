using System.Security.Cryptography;
using System.Text;

namespace SolutionDeployer.Mcp;

/// <summary>
/// Short ids for deploy targets that stay the same across restarts and refreshes, so a caller can reuse
/// one it saw earlier: a profile is keyed by its file path, a script by its own id.
/// </summary>
public static class TargetIds
{
    public static string ForProfile(string profileFilePath)
    {
        var normalized = Path.GetFullPath(profileFilePath);
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "profile-" + Convert.ToHexStringLower(hash)[..10];
    }

    public static string ForScript(string scriptId) => "script-" + scriptId;
}
