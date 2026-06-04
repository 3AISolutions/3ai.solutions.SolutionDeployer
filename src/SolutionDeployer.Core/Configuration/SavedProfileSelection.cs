using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Configuration;

public enum SelectionKind
{
    Profile,
    Script,
}

/// <summary>
/// A persisted record that one of a project's targets was selected. For a profile target it carries
/// the profile name + engine; for a script target it carries the script id. Matched back to a
/// freshly-loaded source by <see cref="Project"/> name plus the target identity.
/// </summary>
public sealed class SavedProfileSelection
{
    public SelectionKind Kind { get; set; } = SelectionKind.Profile;

    public string Project { get; set; } = string.Empty;

    /// <summary>Profile name (for <see cref="SelectionKind.Profile"/>).</summary>
    public string Profile { get; set; } = string.Empty;

    public PublishEngineKind Engine { get; set; } = PublishEngineKind.Dotnet;

    /// <summary>Script target id (for <see cref="SelectionKind.Script"/>).</summary>
    public string? ScriptId { get; set; }
}
