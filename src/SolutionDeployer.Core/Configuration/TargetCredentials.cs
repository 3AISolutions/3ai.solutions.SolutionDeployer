using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Configuration;

/// <summary>Where a target's credentials come from.</summary>
public enum CredentialSource
{
    /// <summary>The target's own username (settings) and password (OS store, under the target's key).</summary>
    Own,

    /// <summary>A shared entry of the credential library.</summary>
    Library,
}

/// <summary>The credentials a target deploys with, after following any library reference.</summary>
/// <param name="Entry">The library entry, when <paramref name="Source"/> is <see cref="CredentialSource.Library"/>.</param>
/// <param name="Password">The saved password, or null when none is saved.</param>
public sealed record ResolvedCredentials(
    CredentialSource Source,
    SavedCredential? Entry,
    string UserName,
    string? Password)
{
    public bool HasSavedPassword => !string.IsNullOrEmpty(Password);

    public PublishCredentials ToPublishCredentials() => new()
    {
        UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName,
        Password = string.IsNullOrEmpty(Password) ? null : Password,
    };
}

/// <summary>
/// Reads and saves a target's credentials. A target (keyed by profile file path or
/// <see cref="ScriptTarget.CredentialKey"/>) either uses a library entry (<see cref="AppSettings.CredentialAssignments"/>)
/// or its own credentials: a username in <see cref="AppSettings.RememberedUserNames"/> and an opt-in password in the
/// <see cref="ICredentialStore"/> under the same key. Passwords never go into settings.
/// </summary>
public static class TargetCredentials
{
    /// <summary>
    /// The credentials <paramref name="targetKey"/> uses. A reference to a library entry that no longer exists falls
    /// back to the target's own credentials.
    /// </summary>
    /// <param name="defaultUserName">Username to offer when none is remembered (e.g. the one in the profile).</param>
    public static ResolvedCredentials Resolve(
        AppSettings settings, ICredentialStore store, string targetKey, string? defaultUserName = null)
    {
        if (settings.GetAssignedCredential(targetKey) is { } entry)
            return new ResolvedCredentials(CredentialSource.Library, entry, entry.UserName, Read(store, entry.PasswordKey));

        var userName = settings.RememberedUserNames.TryGetValue(targetKey, out var remembered)
            ? remembered
            : defaultUserName ?? string.Empty;
        return new ResolvedCredentials(CredentialSource.Own, null, userName, Read(store, targetKey));
    }

    /// <summary>
    /// Makes <paramref name="targetKey"/> use its own credentials: remembers the username and, when
    /// <paramref name="rememberPassword"/> is set (and a secure store exists), the password; otherwise removes any
    /// saved password. The caller saves the settings.
    /// </summary>
    public static void SaveOwn(
        AppSettings settings, ICredentialStore store, string targetKey,
        string? userName, string? password, bool rememberPassword)
    {
        settings.AssignCredential(targetKey, null);

        if (string.IsNullOrWhiteSpace(userName))
            settings.RememberedUserNames.Remove(targetKey);
        else
            settings.RememberedUserNames[targetKey] = userName.Trim();

        if (!store.IsAvailable)
            return;

        if (rememberPassword && !string.IsNullOrEmpty(password))
            store.Set(targetKey, password);
        else
            store.Delete(targetKey);
    }

    /// <summary>
    /// Makes <paramref name="targetKey"/> use the library entry <paramref name="credentialId"/>. The target's own
    /// username and saved password are kept, so switching back restores them. The caller saves the settings.
    /// </summary>
    /// <exception cref="ArgumentException">No library entry has that id.</exception>
    public static void UseLibraryEntry(AppSettings settings, string targetKey, string credentialId)
    {
        if (settings.FindCredential(credentialId) is null)
            throw new ArgumentException($"No saved credential has the id '{credentialId}'.", nameof(credentialId));
        settings.AssignCredential(targetKey, credentialId);
    }

    /// <summary>Removes everything saved for a target that no longer exists (a removed script).</summary>
    public static void Forget(AppSettings settings, ICredentialStore store, string targetKey)
    {
        settings.AssignCredential(targetKey, null);
        settings.RememberedUserNames.Remove(targetKey);
        if (store.IsAvailable)
            store.Delete(targetKey);
    }

    /// <summary>
    /// Adds or replaces a library entry and saves its password: an empty <paramref name="password"/> removes the
    /// saved one. Without a secure store only the entry is kept. The caller saves the settings.
    /// </summary>
    public static void SaveLibraryEntry(AppSettings settings, ICredentialStore store, SavedCredential entry, string? password)
    {
        var index = settings.Credentials.FindIndex(c => c.Id == entry.Id);
        if (index >= 0)
            settings.Credentials[index] = entry;
        else
            settings.Credentials.Add(entry);

        if (!store.IsAvailable)
            return;

        if (string.IsNullOrEmpty(password))
            store.Delete(entry.PasswordKey);
        else
            store.Set(entry.PasswordKey, password);
    }

    /// <summary>
    /// Deletes a library entry and its password. Targets that used it go back to their own credentials.
    /// The caller saves the settings.
    /// </summary>
    /// <returns>The keys of the targets that used the entry.</returns>
    public static IReadOnlyList<string> DeleteLibraryEntry(AppSettings settings, ICredentialStore store, string credentialId)
    {
        var affected = settings.TargetsUsing(credentialId);
        foreach (var key in affected)
            settings.CredentialAssignments.Remove(key);

        settings.Credentials.RemoveAll(c => c.Id == credentialId);
        if (store.IsAvailable)
            store.Delete(SavedCredential.KeyFor(credentialId));
        return affected;
    }

    private static string? Read(ICredentialStore store, string key) =>
        store.IsAvailable && store.Get(key) is { Length: > 0 } secret ? secret : null;
}
