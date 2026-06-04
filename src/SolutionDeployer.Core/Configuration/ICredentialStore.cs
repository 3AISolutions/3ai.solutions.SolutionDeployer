namespace SolutionDeployer.Core.Configuration;

/// <summary>
/// Securely stores publish-profile passwords using the platform's native secret store. Secrets are
/// never written to <c>settings.json</c>. Implementations are best-effort: <see cref="IsAvailable"/>
/// is false when no secure backend exists, in which case the UI should not offer to remember secrets.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Whether a secure backend is available on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>Returns the stored secret for <paramref name="key"/>, or null if none/unavailable.</summary>
    string? Get(string key);

    /// <summary>Stores (or replaces) the secret for <paramref name="key"/>.</summary>
    void Set(string key, string secret);

    /// <summary>Removes any stored secret for <paramref name="key"/>.</summary>
    void Delete(string key);
}
