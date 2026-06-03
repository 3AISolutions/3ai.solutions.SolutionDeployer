namespace SolutionDeployer.Core.Models;

/// <summary>
/// A publishable project discovered inside a solution, together with its publish profiles.
/// </summary>
public sealed class DeploymentProject
{
    /// <summary>Display name of the project (file name without extension).</summary>
    public required string Name { get; init; }

    /// <summary>Absolute path to the project file (.csproj / .fsproj / .vbproj).</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Absolute path to the directory containing the project file.</summary>
    public string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;

    /// <summary>Publish profiles discovered for this project.</summary>
    public IReadOnlyList<PublishProfile> Profiles { get; init; } = [];

    public override string ToString() => Name;
}
