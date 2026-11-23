using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// A row whose deploy needs a username/password (a credentialed profile or script). The row holds what it will
/// deploy with: its own credentials or a library entry's, resolved by <see cref="TargetCredentials"/>; a password
/// that isn't remembered lives only here, for this session.
/// </summary>
public interface ICredentialTarget
{
    string Name { get; }

    /// <summary>Settings / credential-store key: the profile file path or the script's credential key.</summary>
    string CredentialKey { get; }

    /// <summary>Username to offer when none is remembered (the one in the publish profile).</summary>
    string? DefaultUserName { get; }

    bool RequiresCredentials { get; }

    string UserName { get; }

    string Password { get; }

    bool RememberPassword { get; }

    /// <summary>The library entry the row uses, or null for its own credentials.</summary>
    SavedCredential? CredentialEntry { get; }

    /// <summary>Whether a deploy has what it needs (a password, when one is required).</summary>
    bool CredentialsReady { get; }

    /// <summary>Takes the credentials to deploy with.</summary>
    /// <param name="rememberPassword">Whether the password is saved (false: kept for this session only).</param>
    void ApplyCredentials(ResolvedCredentials credentials, bool rememberPassword);
}

/// <summary>Shared display text for <see cref="ICredentialTarget"/> rows.</summary>
public static class CredentialText
{
    public static string Summarize(string userName, string password, bool rememberPassword, SavedCredential? entry)
    {
        var hasPassword = !string.IsNullOrEmpty(password);
        if (entry is not null)
        {
            var user = string.IsNullOrWhiteSpace(userName) ? "no username" : userName;
            return hasPassword ? $"{entry.Name} ({user})" : $"{entry.Name} ({user}, no password saved)";
        }

        if (string.IsNullOrWhiteSpace(userName))
            return hasPassword ? "No username set" : "No credentials set";
        if (!hasPassword)
            return $"{userName} (no password)";
        return rememberPassword ? $"{userName} (password saved)" : $"{userName} (password for this session)";
    }
}
