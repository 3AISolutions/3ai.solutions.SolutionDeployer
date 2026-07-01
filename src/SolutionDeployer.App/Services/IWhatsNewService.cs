namespace SolutionDeployer.App.Services;

/// <summary>
/// Resolves the running app's version and fetches its release notes, and surfaces the "what's new"
/// dialog either on startup (first launch of a new version) or on demand from the toolbar.
/// </summary>
public interface IWhatsNewService
{
    /// <summary>Fetches the markdown notes for the currently running version, or null when unavailable.</summary>
    Task<string?> GetNotesForCurrentVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Shows the "what's new" dialog for the current version. Returns true if it was displayed.</summary>
    Task<bool> ShowAsync(string? notes = null);
}