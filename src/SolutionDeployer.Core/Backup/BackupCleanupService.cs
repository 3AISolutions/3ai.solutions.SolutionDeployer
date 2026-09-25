using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Backup;

/// <summary>Whether the owner of a set of snapshots still exists, and still backs up to where they are.</summary>
public enum BackupOwnerStatus
{
    /// <summary>The owner exists and this is its current backup destination.</summary>
    Active,

    /// <summary>
    /// The owner exists but now backs up to a different destination, so these snapshots no longer show up
    /// (or get pruned) under it.
    /// </summary>
    OldDestination,

    /// <summary>The owner's publish profile file or script no longer exists.</summary>
    Orphaned,

    /// <summary>Taken before snapshots recorded their owner, and not matched to any profile the app knows of.</summary>
    Unknown,
}

/// <summary>Every snapshot one owner has in one destination.</summary>
public sealed class BackupOwnerGroup
{
    public required string StoreTargetId { get; init; }

    public required string StoreDescription { get; init; }

    public required string OwnerKey { get; init; }

    /// <summary>The profile's file path or the script's id, when known.</summary>
    public string? OwnerId { get; init; }

    public required BackupOwnerStatus Status { get; init; }

    /// <summary>Newest first; never empty.</summary>
    public required IReadOnlyList<DeploymentBackup> Snapshots { get; init; }

    public string OwnerName => Snapshots[0].ProfileName;

    public string ProjectName => Snapshots[0].ProjectName;

    public long TotalBytes => Snapshots.Sum(s => s.SizeBytes);

    public DeploymentBackup Newest => Snapshots[0];

    public DeploymentBackup Oldest => Snapshots[^1];
}

/// <summary>A destination that could not be listed (e.g. an unreachable bucket or a missing secret key).</summary>
public sealed record BackupStoreError(string StoreDescription, string Message);

/// <summary>Every snapshot across every configured destination, grouped by owner.</summary>
public sealed class BackupInventory
{
    public required IReadOnlyList<BackupOwnerGroup> Groups { get; init; }

    public IReadOnlyList<BackupStoreError> Errors { get; init; } = [];
}

/// <summary>Snapshots chosen for deletion, reviewed before anything is removed.</summary>
public sealed class BackupCleanupPlan
{
    public BackupCleanupPlan(IEnumerable<BackupDeletion> deletions) => Deletions = deletions.ToList();

    public IReadOnlyList<BackupDeletion> Deletions { get; }

    public bool IsEmpty => Deletions.Count == 0;

    public long BytesReclaimed => Deletions.Sum(d => d.Backup.SizeBytes);

    /// <summary>What the policy expires in every group — orphaned ones included, but each keeps its newest snapshot.</summary>
    public static BackupCleanupPlan ForRetention(BackupInventory inventory, BackupRetentionPolicy policy, DateTimeOffset now) =>
        new(inventory.Groups.SelectMany(g => BackupRetention.SelectExpired(g.Snapshots, policy, now)));

    /// <summary>Every snapshot of the given groups (e.g. orphaned owners the user chose to remove).</summary>
    public static BackupCleanupPlan ForGroups(IEnumerable<BackupOwnerGroup> groups) =>
        new(groups.SelectMany(g => g.Snapshots.Select(s => new BackupDeletion(s, ReasonFor(g.Status)))));

    private static string ReasonFor(BackupOwnerStatus status) => status switch
    {
        BackupOwnerStatus.Orphaned => "owner no longer exists",
        BackupOwnerStatus.OldDestination => "owner now backs up elsewhere",
        BackupOwnerStatus.Unknown => "owner unknown",
        _ => "deleted by user",
    };
}

public sealed record BackupCleanupResult(int Deleted, int Failed, long BytesReclaimed, IReadOnlyList<string> Errors);

/// <summary>Finds every snapshot across all destinations, works out who still needs it, and removes what the user approves.</summary>
public interface IBackupCleanupService
{
    /// <param name="knownOwners">Profiles and scripts currently loaded, used to match snapshots that predate <see cref="DeploymentBackup.OwnerId"/>.</param>
    Task<BackupInventory> GetInventoryAsync(IReadOnlyCollection<BackupOwner> knownOwners, CancellationToken cancellationToken = default);

    /// <summary>Deletes a plan's snapshots, oldest first within each owner; carries on past individual failures.</summary>
    Task<BackupCleanupResult> ApplyAsync(BackupCleanupPlan plan, CancellationToken cancellationToken = default);
}

public sealed class BackupCleanupService(IBackupStoreProvider storeProvider, SettingsStore settingsStore) : IBackupCleanupService
{
    private const string ScriptKeyPrefix = "script_";

    public async Task<BackupInventory> GetInventoryAsync(
        IReadOnlyCollection<BackupOwner> knownOwners, CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Load();
        var owners = new OwnerDirectory(settings, knownOwners);
        var groups = new List<BackupOwnerGroup>();
        var errors = new List<BackupStoreError>();

        foreach (var store in storeProvider.AllStores())
        {
            IReadOnlyList<DeploymentBackup> all;
            try
            {
                all = await store.ListAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(new BackupStoreError(store.Description, ex.Message));
                continue;
            }

            foreach (var owned in all.GroupBy(b => b.ProfileKey, StringComparer.OrdinalIgnoreCase))
            {
                var snapshots = BackupManifest.SortNewestFirst(owned);
                var (status, ownerId) = owners.Resolve(owned.Key, snapshots, store.TargetId);
                groups.Add(new BackupOwnerGroup
                {
                    StoreTargetId = store.TargetId,
                    StoreDescription = store.Description,
                    OwnerKey = owned.Key,
                    OwnerId = ownerId,
                    Status = status,
                    Snapshots = snapshots,
                });
            }
        }

        return new BackupInventory
        {
            Groups = groups
                .OrderBy(g => g.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.OwnerName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Errors = errors,
        };
    }

    public async Task<BackupCleanupResult> ApplyAsync(BackupCleanupPlan plan, CancellationToken cancellationToken = default)
    {
        int deleted = 0, failed = 0;
        long bytes = 0;
        var errors = new List<string>();

        foreach (var inStore in plan.Deletions.Select(d => d.Backup).GroupBy(b => b.StorageTargetId))
        {
            IBackupStore store;
            try
            {
                store = storeProvider.ForTargetId(inStore.Key);
            }
            catch (Exception ex)
            {
                failed += inStore.Count();
                errors.Add(ex.Message);
                continue;
            }

            foreach (var backup in inStore.GroupBy(b => b.ProfileKey).SelectMany(BackupRetention.OldestFirst))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await store.DeleteAsync(backup, cancellationToken).ConfigureAwait(false);
                    deleted++;
                    bytes += backup.SizeBytes;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    errors.Add($"{backup.ProfileName} #{backup.Sequence}: {ex.Message}");
                }
            }
        }

        return new BackupCleanupResult(deleted, failed, bytes, errors);
    }

    /// <summary>Resolves a snapshot folder to its owner from what the app knows about profiles and scripts.</summary>
    private sealed class OwnerDirectory
    {
        private readonly AppSettings _settings;
        private readonly Dictionary<string, BackupOwner> _known;
        private readonly Dictionary<string, ScriptTarget> _scriptsByKey;
        private readonly HashSet<string> _scriptIds;
        private readonly Dictionary<string, string> _profilePathsByHash;

        public OwnerDirectory(AppSettings settings, IEnumerable<BackupOwner> knownOwners)
        {
            _settings = settings;
            _known = knownOwners
                .GroupBy(BackupService.KeyFor, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var scripts = settings.ScriptTargets.Values.SelectMany(list => list).ToList();
            _scriptsByKey = scripts
                .GroupBy(s => BackupService.KeyFor(BackupOwner.ForScript(s, string.Empty)), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            _scriptIds = scripts.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Profile paths the settings remember, to identify snapshots taken before OwnerId was recorded.
            _profilePathsByHash = settings.DeployHistory.Keys
                .Concat(settings.ProfileBackupTarget.Keys)
                .Concat(settings.RememberedUserNames.Keys)
                .Where(Path.IsPathRooted)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .GroupBy(BackupService.PathHash)
                .ToDictionary(g => g.Key, g => g.First());
        }

        public (BackupOwnerStatus Status, string? OwnerId) Resolve(
            string ownerKey, IReadOnlyList<DeploymentBackup> snapshots, string storeTargetId)
        {
            var ownerId = snapshots.Select(s => s.OwnerId).FirstOrDefault(id => !string.IsNullOrEmpty(id));

            if (_known.TryGetValue(ownerKey, out var owner))
                return (StatusFor(owner.SettingsKey, storeTargetId), owner.Profile?.FilePath ?? owner.Script!.Id);

            // Script ids are ids, profile owners are rooted file paths.
            var isScript = ownerId is not null
                ? _scriptIds.Contains(ownerId) || !Path.IsPathRooted(ownerId)
                : ownerKey.StartsWith(ScriptKeyPrefix, StringComparison.OrdinalIgnoreCase);

            if (isScript)
            {
                // Settings hold every script there is, so one missing from them is gone.
                return _scriptsByKey.TryGetValue(ownerKey, out var script)
                    ? (StatusFor(script.CredentialKey, storeTargetId), script.Id)
                    : (BackupOwnerStatus.Orphaned, ownerId);
            }

            if (ownerId is null)
            {
                var hash = ownerKey[(ownerKey.LastIndexOf('_') + 1)..];
                if (!_profilePathsByHash.TryGetValue(hash, out ownerId))
                    return (BackupOwnerStatus.Unknown, null);
            }

            return File.Exists(ownerId)
                ? (StatusFor(ownerId, storeTargetId), ownerId)
                : (BackupOwnerStatus.Orphaned, ownerId);
        }

        private BackupOwnerStatus StatusFor(string settingsKey, string storeTargetId) =>
            string.Equals(_settings.GetBackupTargetId(settingsKey), storeTargetId, StringComparison.OrdinalIgnoreCase)
                ? BackupOwnerStatus.Active
                : BackupOwnerStatus.OldDestination;
    }
}
