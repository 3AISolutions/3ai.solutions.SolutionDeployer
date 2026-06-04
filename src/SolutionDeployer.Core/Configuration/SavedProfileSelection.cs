using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Configuration;

/// <summary>
/// A persisted record that a particular project's profile was selected, with the engine chosen for
/// it. Matched back to a freshly-parsed solution by <see cref="Project"/> + <see cref="Profile"/> name.
/// </summary>
public sealed class SavedProfileSelection
{
    public string Project { get; set; } = string.Empty;

    public string Profile { get; set; } = string.Empty;

    public PublishEngineKind Engine { get; set; } = PublishEngineKind.Dotnet;
}
