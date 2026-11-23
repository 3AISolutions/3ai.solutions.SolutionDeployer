using SolutionDeployer.App.ViewModels;
using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.Services;

/// <summary>One line of a target's connection details, e.g. ("Server", "https://host:8172/msdeploy.axd").</summary>
public sealed record ConnectionDetail(string Label, string Value);

/// <summary>What the per-target Credentials dialog edits.</summary>
/// <param name="Kind">"Publish profile" or "Script", for the heading.</param>
/// <param name="Profile">The profile, for a Web Deploy test; null for a script.</param>
/// <param name="Script">The script, for its test command; null for a profile.</param>
/// <param name="ProjectDirectory">Where a script's test command runs from.</param>
/// <param name="Prompt">Why the dialog was opened, when a deploy needs a password (null otherwise).</param>
public sealed record CredentialsDialogRequest(
    ICredentialTarget Target,
    string Kind,
    IReadOnlyList<ConnectionDetail> Connection,
    PublishProfile? Profile,
    ScriptTarget? Script,
    string? ProjectDirectory,
    string? Prompt = null);

/// <param name="Saved">The user saved the target's credentials (already applied to the row and persisted).</param>
/// <param name="LibraryChanged">The credential library was edited from the dialog, so other rows may need refreshing.</param>
public sealed record CredentialsDialogResult(bool Saved, bool LibraryChanged);

/// <summary>Shows the per-target Credentials dialog and the credential library manager.</summary>
public interface ICredentialsDialogService
{
    /// <param name="describeTarget">Names a target by its credential key (for "used by" lists in the library).</param>
    Task<CredentialsDialogResult> EditAsync(
        AppSettings settings, CredentialsDialogRequest request, Func<string, string> describeTarget);

    /// <returns>True when the library was changed and saved.</returns>
    Task<bool> ShowLibraryAsync(AppSettings settings, Func<string, string> describeTarget);
}
