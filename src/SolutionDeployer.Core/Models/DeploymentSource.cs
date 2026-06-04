using System.Text.Json.Serialization;

namespace SolutionDeployer.Core.Models;

/// <summary>What kind of file a deployment source points at.</summary>
public enum SourceKind
{
    /// <summary>A `.sln` / `.slnx` solution — expands to its publishable projects that have profiles.</summary>
    Solution,

    /// <summary>A single `.csproj` / `.fsproj` / `.vbproj` — always included, even without profiles.</summary>
    Project,
}

/// <summary>
/// A user-added item the app tracks: a solution or a standalone project. Sources persist across
/// sessions and are re-parsed from disk on load (so on-disk profile changes are always reflected).
/// </summary>
public sealed class DeploymentSource
{
    public SourceKind Kind { get; set; }

    /// <summary>Absolute path to the solution or project file.</summary>
    public string Path { get; set; } = string.Empty;

    [JsonIgnore]
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public static DeploymentSource Solution(string path) => new() { Kind = SourceKind.Solution, Path = path };

    public static DeploymentSource Project(string path) => new() { Kind = SourceKind.Project, Path = path };

    public override string ToString() => $"{Kind}:{Path}";
}
