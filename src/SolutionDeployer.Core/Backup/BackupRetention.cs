using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.Core.Backup;

/// <summary>How many snapshots each owner keeps, and optionally for how long.</summary>
public sealed record BackupRetentionPolicy(int KeepLast = 10, int? MaxAgeDays = null)
{
    public static BackupRetentionPolicy From(AppSettings settings) =>
        new(settings.BackupRetention, settings.BackupMaxAgeDays);
}

/// <summary>A snapshot selected for deletion, and why.</summary>
public sealed record BackupDeletion(DeploymentBackup Backup, string Reason);

/// <summary>
/// Which snapshots may be deleted without breaking the ones that remain. Partial snapshots form a chain —
/// restoring one first rolls back every newer one — so snapshots are only ever removed from the old end.
/// </summary>
public static class BackupRetention
{
    /// <summary>
    /// The snapshots of one owner (<paramref name="newestFirst"/>) that <paramref name="policy"/> expires: everything
    /// from the first snapshot that is beyond <see cref="BackupRetentionPolicy.KeepLast"/> or older than
    /// <see cref="BackupRetentionPolicy.MaxAgeDays"/> onwards, so what is kept is always an unbroken run of the
    /// newest. The newest snapshot is never expired, however old it is.
    /// </summary>
    public static IReadOnlyList<BackupDeletion> SelectExpired(
        IReadOnlyList<DeploymentBackup> newestFirst, BackupRetentionPolicy policy, DateTimeOffset now)
    {
        var keep = Math.Max(1, policy.KeepLast);
        var maxAgeDays = policy.MaxAgeDays is int days && days > 0 ? days : (int?)null;
        var cutoff = maxAgeDays is { } d ? now.AddDays(-d) : (DateTimeOffset?)null;

        bool TooOld(DeploymentBackup b) => cutoff is { } c && b.CreatedUtc < c;

        for (var i = 1; i < newestFirst.Count; i++)
        {
            if (i < keep && !TooOld(newestFirst[i]))
                continue;

            return newestFirst
                .Skip(i)
                .Select((b, offset) => new BackupDeletion(b, (i + offset) >= keep
                    ? $"beyond the {keep} most recent"
                    : TooOld(b) ? $"older than {maxAgeDays} days" : "older than an expired snapshot"))
                .ToList();
        }

        return [];
    }

    /// <summary>
    /// What deleting <paramref name="backup"/> has to remove: the snapshot itself plus every older partial snapshot
    /// of the same owner, which could no longer be restored without it. Oldest first.
    /// </summary>
    public static IReadOnlyList<DeploymentBackup> DeletionSet(
        DeploymentBackup backup, IEnumerable<DeploymentBackup> ownerSnapshots) =>
        OldestFirst(ownerSnapshots
            .Where(b => b.Id != backup.Id && b.IsPartial && b.Sequence < backup.Sequence)
            .Append(backup));

    /// <summary>
    /// The order to delete in: oldest first, so a cleanup that stops part-way still leaves each owner with an
    /// unbroken run of its newest snapshots.
    /// </summary>
    public static IReadOnlyList<DeploymentBackup> OldestFirst(IEnumerable<DeploymentBackup> backups) =>
        backups
            .OrderBy(b => b.Sequence)
            .ThenBy(b => b.CreatedUtc)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToList();
}
