using System.Text.Json;

namespace SolutionDeployer.Core.Backup;

/// <summary>Shared (de)serialization and ordering for snapshot manifests, used by every store.</summary>
internal static class BackupManifest
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Serialize(DeploymentBackup backup) => JsonSerializer.Serialize(backup, Options);

    public static DeploymentBackup? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DeploymentBackup>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Total, stable newest-first ordering so same-second snapshots never sort arbitrarily.</summary>
    public static IReadOnlyList<DeploymentBackup> SortNewestFirst(IEnumerable<DeploymentBackup> backups) =>
        backups
            .OrderByDescending(b => b.Sequence)
            .ThenByDescending(b => b.CreatedUtc)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>The manifest key for a package key (…\x.zip → …\x.json), for local paths and S3 keys alike.</summary>
    public static string ManifestKey(string packageKey) =>
        packageKey.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? packageKey[..^4] + ".json"
            : packageKey + ".json";
}
