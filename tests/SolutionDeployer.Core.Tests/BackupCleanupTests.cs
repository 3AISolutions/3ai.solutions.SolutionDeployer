using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Tests;

public sealed class BackupCleanupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _backupRoot;
    private readonly SettingsStore _settingsStore;
    private readonly BackupStoreProvider _provider;
    private readonly LocalBackupStore _store;

    public BackupCleanupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sd_cleanup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _backupRoot = Path.Combine(_tempDir, "Backups");
        _settingsStore = new SettingsStore(Path.Combine(_tempDir, "settings.json"));
        _provider = new BackupStoreProvider(_settingsStore, new NullCredentialStore(), localRootOverride: _backupRoot);
        _store = new LocalBackupStore(_backupRoot);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // ---- Retention selection -------------------------------------------------

    private static DeploymentBackup Snapshot(long sequence, int daysOld, BackupKind kind = BackupKind.FileSystem) => new()
    {
        Id = $"id{sequence}",
        ProfileKey = "Prod_12345678",
        ProfileName = "Prod",
        ProjectName = "App",
        Kind = kind,
        CreatedUtc = Now.AddDays(-daysOld),
        Sequence = sequence,
        PackagePath = $"p{sequence}.zip",
    };

    private static List<DeploymentBackup> NewestFirst(params DeploymentBackup[] snapshots) =>
        snapshots.OrderByDescending(s => s.Sequence).ToList();

    [Fact]
    public void Retention_keeps_the_most_recent_count()
    {
        var all = NewestFirst(Snapshot(1, 5), Snapshot(2, 4), Snapshot(3, 3), Snapshot(4, 2), Snapshot(5, 1));

        var expired = BackupRetention.SelectExpired(all, new BackupRetentionPolicy(KeepLast: 3), Now);

        Assert.Equal([2L, 1L], expired.Select(d => d.Backup.Sequence));
    }

    [Fact]
    public void Retention_has_no_age_limit_by_default()
    {
        var all = NewestFirst(Snapshot(1, 900), Snapshot(2, 800));

        Assert.Null(BackupRetentionPolicy.From(new AppSettings()).MaxAgeDays);
        Assert.Empty(BackupRetention.SelectExpired(all, BackupRetentionPolicy.From(new AppSettings()), Now));
    }

    [Fact]
    public void Retention_age_limit_never_expires_the_newest_snapshot()
    {
        var all = NewestFirst(Snapshot(1, 90), Snapshot(2, 80), Snapshot(3, 70));

        var expired = BackupRetention.SelectExpired(all, new BackupRetentionPolicy(KeepLast: 10, MaxAgeDays: 30), Now);

        Assert.Equal([2L, 1L], expired.Select(d => d.Backup.Sequence));
        Assert.All(expired, d => Assert.Contains("30 days", d.Reason));
    }

    [Fact]
    public void Retention_only_ever_removes_the_oldest_end()
    {
        // #2's clock was off: it looks old, but #1 behind it does not. Keeping #1 alone would leave a gap.
        var all = NewestFirst(Snapshot(1, 1), Snapshot(2, 60), Snapshot(3, 0));

        var expired = BackupRetention.SelectExpired(all, new BackupRetentionPolicy(KeepLast: 10, MaxAgeDays: 30), Now);

        Assert.Equal([2L, 1L], expired.Select(d => d.Backup.Sequence));
    }

    [Fact]
    public void Deleting_a_partial_snapshot_also_deletes_the_older_partial_ones()
    {
        var all = NewestFirst(
            Snapshot(1, 5, BackupKind.MsDeployPartial),
            Snapshot(2, 4, BackupKind.FileSystem),
            Snapshot(3, 3, BackupKind.MsDeployPartial),
            Snapshot(4, 2, BackupKind.MsDeployPartial),
            Snapshot(5, 1, BackupKind.MsDeployPartial));

        var set = BackupRetention.DeletionSet(all.Single(s => s.Sequence == 4), all);

        // #5 is newer (still restorable); #2 is a full snapshot, which needs nothing else.
        Assert.Equal([1L, 3L, 4L], set.Select(s => s.Sequence));
    }

    [Fact]
    public void Deleting_a_full_snapshot_deletes_only_that_snapshot()
    {
        var all = NewestFirst(Snapshot(1, 3), Snapshot(2, 2), Snapshot(3, 1));

        var set = BackupRetention.DeletionSet(all.Single(s => s.Sequence == 2), all);

        Assert.Equal([2L], set.Select(s => s.Sequence));
    }

    // ---- Inventory -----------------------------------------------------------

    private string ExistingFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "<Project />");
        return path;
    }

    private static PublishProfile Profile(string filePath) => new()
    {
        Name = Path.GetFileNameWithoutExtension(filePath),
        FilePath = filePath,
        Format = PublishProfileFormat.PubXml,
    };

    private async Task<DeploymentBackup> SaveAsync(
        string ownerKey, string? ownerId, long sequence, int daysOld = 0,
        BackupKind kind = BackupKind.FileSystem, string name = "Prod")
    {
        var package = Path.Combine(_tempDir, $"pkg_{Guid.NewGuid():N}.zip");
        await File.WriteAllTextAsync(package, new string('x', 100));
        var id = Guid.NewGuid().ToString("N");
        var backup = new DeploymentBackup
        {
            Id = id,
            ProfileKey = ownerKey,
            ProfileName = name,
            ProjectName = "App",
            OwnerId = ownerId,
            Kind = kind,
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-daysOld),
            Sequence = sequence,
            PackagePath = _store.ResolveKey(ownerKey, $"{sequence:D6}_{id[..8]}.zip"),
            SizeBytes = 100,
        };
        await _store.SaveAsync(backup, package);
        return backup;
    }

    private BackupCleanupService NewCleanup() => new(_provider, _settingsStore);

    private static BackupOwnerGroup Group(BackupInventory inventory, string ownerKey) =>
        inventory.Groups.Single(g => g.OwnerKey == ownerKey);

    [Fact]
    public async Task Inventory_marks_owners_that_still_exist_as_active()
    {
        var profile = Profile(ExistingFile("Prod.pubxml"));
        var key = BackupService.KeyFor(BackupOwner.ForProfile(profile, _tempDir));
        await SaveAsync(key, profile.FilePath, 1);

        var inventory = await NewCleanup().GetInventoryAsync([]);

        Assert.Equal(BackupOwnerStatus.Active, Group(inventory, key).Status);
        Assert.Empty(inventory.Errors);
    }

    [Fact]
    public async Task Inventory_marks_deleted_profiles_and_scripts_as_orphaned()
    {
        var goneProfile = Profile(Path.Combine(_tempDir, "Gone.pubxml"));
        var profileKey = BackupService.KeyFor(BackupOwner.ForProfile(goneProfile, _tempDir));
        await SaveAsync(profileKey, goneProfile.FilePath, 1, name: "Gone");

        var goneScript = new ScriptTarget { Name = "Deploy" };
        var scriptKey = BackupService.KeyFor(BackupOwner.ForScript(goneScript, _tempDir));
        await SaveAsync(scriptKey, goneScript.Id, 1, name: "Deploy");

        var inventory = await NewCleanup().GetInventoryAsync([]);

        Assert.Equal(BackupOwnerStatus.Orphaned, Group(inventory, profileKey).Status);
        Assert.Equal(BackupOwnerStatus.Orphaned, Group(inventory, scriptKey).Status);
    }

    [Fact]
    public async Task Inventory_finds_scripts_in_the_settings()
    {
        var script = new ScriptTarget { Name = "Deploy" };
        var settings = _settingsStore.Load();
        settings.SetScriptTargets(Path.Combine(_tempDir, "App.csproj"), [script]);
        _settingsStore.Save(settings);

        // No OwnerId: taken before owners were recorded.
        var key = BackupService.KeyFor(BackupOwner.ForScript(script, _tempDir));
        await SaveAsync(key, ownerId: null, 1, name: "Deploy");

        var inventory = await NewCleanup().GetInventoryAsync([]);

        Assert.Equal(BackupOwnerStatus.Active, Group(inventory, key).Status);
    }

    [Fact]
    public async Task Inventory_matches_older_snapshots_to_profiles_the_settings_remember()
    {
        var remembered = Profile(ExistingFile("Staging.pubxml"));
        var settings = _settingsStore.Load();
        settings.RememberedUserNames[remembered.FilePath] = "deploy";
        _settingsStore.Save(settings);

        var rememberedKey = BackupService.KeyFor(BackupOwner.ForProfile(remembered, _tempDir));
        await SaveAsync(rememberedKey, ownerId: null, 1, name: "Staging");
        await SaveAsync("Mystery_abcdef12", ownerId: null, 1, name: "Mystery");

        var inventory = await NewCleanup().GetInventoryAsync([]);

        Assert.Equal(BackupOwnerStatus.Active, Group(inventory, rememberedKey).Status);
        Assert.Equal(remembered.FilePath, Group(inventory, rememberedKey).OwnerId);
        Assert.Equal(BackupOwnerStatus.Unknown, Group(inventory, "Mystery_abcdef12").Status);
    }

    [Fact]
    public async Task Inventory_matches_older_snapshots_to_loaded_owners()
    {
        var profile = Profile(ExistingFile("Prod.pubxml"));
        var owner = BackupOwner.ForProfile(profile, _tempDir);
        var key = BackupService.KeyFor(owner);
        await SaveAsync(key, ownerId: null, 1);

        var inventory = await NewCleanup().GetInventoryAsync([owner]);

        Assert.Equal(BackupOwnerStatus.Active, Group(inventory, key).Status);
    }

    [Fact]
    public async Task Inventory_flags_snapshots_left_in_a_previous_destination()
    {
        var profile = Profile(ExistingFile("Prod.pubxml"));
        var settings = _settingsStore.Load();
        settings.SetBackupTargetId(profile.FilePath, "remote1");
        _settingsStore.Save(settings);

        var key = BackupService.KeyFor(BackupOwner.ForProfile(profile, _tempDir));
        await SaveAsync(key, profile.FilePath, 1);

        var inventory = await NewCleanup().GetInventoryAsync([]);

        Assert.Equal(BackupOwnerStatus.OldDestination, Group(inventory, key).Status);
    }

    // ---- Apply ---------------------------------------------------------------

    [Fact]
    public async Task Retention_cleanup_removes_expired_snapshots_in_every_group()
    {
        var profile = Profile(ExistingFile("Prod.pubxml"));
        var key = BackupService.KeyFor(BackupOwner.ForProfile(profile, _tempDir));
        for (var i = 1; i <= 5; i++)
            await SaveAsync(key, profile.FilePath, i);

        var cleanup = NewCleanup();
        var plan = BackupCleanupPlan.ForRetention(
            await cleanup.GetInventoryAsync([]), new BackupRetentionPolicy(KeepLast: 2), DateTimeOffset.UtcNow);

        Assert.Equal(3, plan.Deletions.Count);
        Assert.Equal(300, plan.BytesReclaimed);

        var result = await cleanup.ApplyAsync(plan);

        Assert.Equal(3, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.Equal([5L, 4L], (await _store.ListAsync(key)).Select(b => b.Sequence));
    }

    [Fact]
    public async Task Deleting_an_orphaned_group_removes_its_folder()
    {
        var key = BackupService.KeyFor(BackupOwner.ForProfile(Profile(Path.Combine(_tempDir, "Gone.pubxml")), _tempDir));
        await SaveAsync(key, Path.Combine(_tempDir, "Gone.pubxml"), 1);
        await SaveAsync(key, Path.Combine(_tempDir, "Gone.pubxml"), 2);

        var cleanup = NewCleanup();
        var orphaned = (await cleanup.GetInventoryAsync([])).Groups.Where(g => g.Status == BackupOwnerStatus.Orphaned);
        var result = await cleanup.ApplyAsync(BackupCleanupPlan.ForGroups(orphaned));

        Assert.Equal(2, result.Deleted);
        Assert.False(Directory.Exists(Path.Combine(_backupRoot, key)));
        Assert.Empty((await cleanup.GetInventoryAsync([])).Groups);
    }

    // ---- BackupService integration --------------------------------------------

    [Fact]
    public async Task Deleting_a_partial_snapshot_through_the_service_removes_the_older_chain()
    {
        var service = new BackupService(new ProcessRunner(), new MsDeployLocator(), _provider);
        const string key = "Prod_12345678";
        var b1 = await SaveAsync(key, null, 1, kind: BackupKind.MsDeployPartial);
        var b2 = await SaveAsync(key, null, 2, kind: BackupKind.MsDeployPartial);
        var b3 = await SaveAsync(key, null, 3, kind: BackupKind.MsDeployPartial);

        Assert.Equal([b1.Id, b2.Id], (await service.GetDeletionSetAsync(b2)).Select(b => b.Id));
        Assert.True(await service.DeleteAsync(b2));

        Assert.Equal([b3.Id], (await _store.ListAsync(key)).Select(b => b.Id));
    }

    [Fact]
    public async Task Backing_up_prunes_by_age_when_a_limit_is_set()
    {
        var dest = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(dest);
        await File.WriteAllTextAsync(Path.Combine(dest, "app.dll"), "v1");
        var profile = new PublishProfile
        {
            Name = "Prod",
            FilePath = ExistingFile("Prod.pubxml"),
            Format = PublishProfileFormat.PubXml,
            WebPublishMethod = "FileSystem",
            Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["publishUrl"] = dest },
        };
        var key = BackupService.KeyFor(BackupOwner.ForProfile(profile, _tempDir));
        await SaveAsync(key, profile.FilePath, 1, daysOld: 60);
        await SaveAsync(key, profile.FilePath, 2, daysOld: 45);
        await SaveAsync(key, profile.FilePath, 3, daysOld: 5);

        var service = new BackupService(new ProcessRunner(), new MsDeployLocator(), _provider,
            () => new BackupRetentionPolicy(KeepLast: 10, MaxAgeDays: 30));
        var job = new PublishJob
        {
            Project = new DeploymentProject { Name = "App", ProjectPath = Path.Combine(_tempDir, "App.csproj") },
            Profile = profile,
            Engine = PublishEngineKind.Dotnet,
        };

        var created = await service.BackUpAsync(job, _ => { });

        Assert.NotNull(created);
        Assert.Equal(profile.FilePath, created!.OwnerId);
        Assert.Equal([4L, 3L], (await _store.ListAsync(key)).Select(b => b.Sequence));
    }
}
