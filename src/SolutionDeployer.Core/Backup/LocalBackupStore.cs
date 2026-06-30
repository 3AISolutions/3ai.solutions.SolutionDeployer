using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.Core.Backup;

/// <summary>Stores snapshot packages and manifests under a per-user local folder.</summary>
public sealed class LocalBackupStore : IBackupStore
{
    private readonly string _root;

    public LocalBackupStore(string? rootOverride = null)
    {
        _root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "3ai.SolutionDeployer",
            "Backups");
    }

    public string TargetId => S3BackupTarget.LocalId;

    public string Description => "local disk";

    public string ResolveKey(string profileKey, string fileName)
    {
        var folder = Path.Combine(_root, profileKey);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, fileName);
    }

    public Task<IReadOnlyList<DeploymentBackup>> ListAsync(string profileKey, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(_root, profileKey);
        if (!Directory.Exists(folder))
            return Task.FromResult<IReadOnlyList<DeploymentBackup>>([]);

        var backups = Directory.EnumerateFiles(folder, "*.json")
            .Select(f => BackupManifest.Deserialize(File.ReadAllText(f)))
            .Where(b => b is not null && File.Exists(b.PackagePath))
            .Select(b => b!);

        return Task.FromResult(BackupManifest.SortNewestFirst(backups));
    }

    public Task SaveAsync(DeploymentBackup backup, string localPackagePath, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(localPackagePath, backup.PackagePath, StringComparison.OrdinalIgnoreCase))
            File.Copy(localPackagePath, backup.PackagePath, overwrite: true);

        File.WriteAllText(BackupManifest.ManifestKey(backup.PackagePath), BackupManifest.Serialize(backup));
        return Task.CompletedTask;
    }

    public Task<DownloadedPackage> DownloadAsync(DeploymentBackup backup, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backup.PackagePath))
            throw new FileNotFoundException($"Backup package not found: {backup.PackagePath}");

        return Task.FromResult(new DownloadedPackage(backup.PackagePath, IsTemporary: false));
    }

    public Task DeleteAsync(DeploymentBackup backup, CancellationToken cancellationToken = default)
    {
        TryDelete(backup.PackagePath);
        TryDelete(BackupManifest.ManifestKey(backup.PackagePath));
        return Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort.
        }
    }
}
