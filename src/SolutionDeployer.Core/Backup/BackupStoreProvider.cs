using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Backup;

/// <summary>Resolves the configured <see cref="IBackupStore"/> for a profile or a stored target id.</summary>
public interface IBackupStoreProvider
{
    /// <summary>The store a profile's new snapshots should go to (its configured destination).</summary>
    IBackupStore ForProfile(PublishProfile profile);

    /// <summary>The store a given destination id refers to (used to read/restore existing snapshots).</summary>
    IBackupStore ForTargetId(string targetId);
}

public sealed class BackupStoreProvider(
    SettingsStore settingsStore,
    ICredentialStore credentialStore,
    string? localRootOverride = null) : IBackupStoreProvider
{
    public IBackupStore ForProfile(PublishProfile profile) =>
        ForTargetId(settingsStore.Load().GetBackupTargetId(profile.FilePath));

    public IBackupStore ForTargetId(string targetId)
    {
        if (string.IsNullOrEmpty(targetId) || targetId == S3BackupTarget.LocalId)
            return new LocalBackupStore(localRootOverride);

        var target = settingsStore.Load().RemoteBackupTargets.FirstOrDefault(t => t.Id == targetId)
            ?? throw new InvalidOperationException(
                $"Backup destination '{targetId}' is not configured. Re-select a destination for this profile.");

        var secret = credentialStore.IsAvailable ? credentialStore.Get(target.SecretCredentialKey) : null;
        return new S3BackupStore(target, secret ?? string.Empty);
    }
}
