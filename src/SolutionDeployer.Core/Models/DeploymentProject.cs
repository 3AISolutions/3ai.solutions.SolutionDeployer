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

    /// <summary>The solution the project was loaded from, or null when it was added on its own.</summary>
    public string? SolutionPath { get; init; }

    /// <summary>
    /// The project's MSBuild target inside <see cref="SolutionPath"/> (e.g. <c>Web\My_App</c>), used to
    /// build just this project through the solution so its configuration mapping applies.
    /// </summary>
    public string? SolutionTargetName { get; init; }

    /// <summary>Publish profiles discovered for this project.</summary>
    public IReadOnlyList<PublishProfile> Profiles { get; init; } = [];

    public override string ToString() => Name;
}
