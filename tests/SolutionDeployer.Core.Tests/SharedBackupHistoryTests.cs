using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Tests;

/// <summary>
/// A team sharing one backup bucket: each target's folder is the same on every machine, deploys are recorded (when,
/// by whom) beside its snapshots, and one machine never takes a teammate's target for gone.
/// </summary>
public sealed class SharedBackupHistoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"sd_shared_{Guid.NewGuid():N}");

    public SharedBackupHistoryTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static PublishProfile WebDeployProfile(string cloneRoot, string site = "MySite") => new()
    {
        Name = "Prod",
        FilePath = Path.Combine(cloneRoot, "WebApp", "Properties", "PublishProfiles", "Prod.pubxml"),
        Format = PublishProfileFormat.PubXml,
        WebPublishMethod = "MSDeploy",
        ServerUrl = "https://web01:8172/msdeploy.axd",
        SiteName = site,
    };

    private static string Clone(string root) => Path.Combine(root, "WebApp");

    /// <summary>Where someone cloned the repository; differs from machine to machine.</summary>
    private static string Root(params string[] parts) => Path.Combine([Path.GetTempPath(), .. parts]);

    // ---- Folder keys ----------------------------------------------------------

    [Fact]
    public void A_profile_has_the_same_folder_in_every_clone()
    {
        var myRoot = Root("src", "shop");
        var theirRoot = Root("work", "repos", "shop");

        var myKey = BackupService.KeyFor(BackupOwner.ForProfile(WebDeployProfile(myRoot), Clone(myRoot)));

        Assert.Equal(myKey, BackupService.KeyFor(BackupOwner.ForProfile(WebDeployProfile(theirRoot), Clone(theirRoot))));
        Assert.StartsWith("Prod_", myKey);
    }

    [Fact]
    public void A_profile_pointed_at_another_site_gets_its_own_folder()
    {
        var root = Root("src", "shop");
        var prod = BackupService.KeyFor(BackupOwner.ForProfile(WebDeployProfile(root), Clone(root)));
        var other = BackupService.KeyFor(BackupOwner.ForProfile(WebDeployProfile(root, "OtherSite"), Clone(root)));

        Assert.NotEqual(prod, other);
    }

    [Fact]
    public void A_script_has_the_same_folder_for_everyone_who_added_it()
    {
        // Each person's settings give the script its own random id.
        var mine = new ScriptTarget { Name = "Deploy", ScriptPath = @"deploy\ship.ps1", Arguments = "-Env prod" };
        var theirs = new ScriptTarget { Name = "Ship to prod", ScriptPath = "deploy/ship.ps1", Arguments = "-Env prod" };
        Assert.NotEqual(mine.Id, theirs.Id);

        var myKey = BackupService.KeyFor(BackupOwner.ForScript(mine, Clone(Root("src", "shop"))));
        Assert.Equal(myKey, BackupService.KeyFor(BackupOwner.ForScript(theirs, Clone(Root("work", "shop")))));
        Assert.StartsWith("script_ship_", myKey);
    }

    [Fact]
    public void One_script_run_against_two_environments_keeps_two_folders()
    {
        var prod = new ScriptTarget { ScriptPath = "deploy/ship.ps1", Arguments = "-Env prod" };
        var test = new ScriptTarget { ScriptPath = "deploy/ship.ps1", Arguments = "-Env test" };

        Assert.NotEqual(
            BackupService.KeyFor(BackupOwner.ForScript(prod, Clone(Root("src", "shop")))),
            BackupService.KeyFor(BackupOwner.ForScript(test, Clone(Root("src", "shop")))));
    }

    // ---- Last deployment ------------------------------------------------------

    [Fact]
    public async Task The_last_deployment_is_kept_beside_the_snapshots_without_being_one()
    {
        var store = new LocalBackupStore(_tempDir);
        var deployment = new LastDeployment { DeployedUtc = DateTimeOffset.UtcNow, User = "maria", Machine = "LAPTOP-7" };

        await store.SaveLastDeploymentAsync("Prod_abc", deployment);

        var read = await store.GetLastDeploymentAsync("Prod_abc");
        Assert.NotNull(read);
        Assert.Equal("maria on LAPTOP-7", read.By);
        Assert.Equal(deployment.DeployedUtc, read.DeployedUtc);
        Assert.Empty(await store.ListAsync("Prod_abc"));
        Assert.Empty(await store.ListAllAsync());
        Assert.Null(await store.GetLastDeploymentAsync("Other_abc"));
    }

    [Fact]
    public async Task A_successful_deploy_is_recorded_and_a_failed_one_is_not()
    {
        var backups = new RecordingBackupService();
        var runner = new DeploymentRunner(
            new PublishEngineFactory([new FixedEngine(PublishStatus.Succeeded, PublishEngineKind.Dotnet),
                new FixedEngine(PublishStatus.Failed, PublishEngineKind.MsBuild)]),
            backups);
        var project = new DeploymentProject { Name = "WebApp", ProjectPath = Path.Combine(_tempDir, "WebApp", "WebApp.csproj") };
        var ok = new PublishJob { Project = project, Profile = WebDeployProfile(_tempDir), Engine = PublishEngineKind.Dotnet };
        var failed = new PublishJob { Project = project, Profile = WebDeployProfile(_tempDir, "Other"), Engine = PublishEngineKind.MsBuild };
        var output = new List<string>();

        await runner.RunAsync([ok, failed], new DeploymentRunOptions(), o => output.Add(o.Line.Text), _ => { });

        Assert.Equal(["MySite"], backups.Recorded.Select(o => o.Profile!.SiteName));
        Assert.Contains("[deploy] Recorded this deploy in team bucket.", output);
    }

    [Fact]
    public async Task A_destination_that_cannot_record_the_deploy_does_not_fail_it()
    {
        var backups = new RecordingBackupService { Fail = true };
        var runner = new DeploymentRunner(
            new PublishEngineFactory([new FixedEngine(PublishStatus.Succeeded, PublishEngineKind.Dotnet)]), backups);
        var job = new PublishJob
        {
            Project = new DeploymentProject { Name = "WebApp", ProjectPath = Path.Combine(_tempDir, "WebApp", "WebApp.csproj") },
            Profile = WebDeployProfile(_tempDir),
            Engine = PublishEngineKind.Dotnet,
        };
        var output = new List<string>();

        var results = await runner.RunAsync([job], new DeploymentRunOptions(), o => output.Add(o.Line.Text), _ => { });

        Assert.True(results.Single().IsSuccess);
        Assert.Contains(output, l => l.StartsWith("[deploy] Could not record this deploy"));
    }

    // ---- Snapshot ordering and cleanup ------------------------------------------

    private static DeploymentBackup Partial(string id, long sequence, int minute) => new()
    {
        Id = id,
        ProfileKey = "Prod_abc",
        ProfileName = "Prod",
        ProjectName = "WebApp",
        Kind = BackupKind.MsDeployPartial,
        CreatedUtc = new DateTimeOffset(2026, 11, 23, 10, minute, 0, TimeSpan.Zero),
        Sequence = sequence,
        PackagePath = $"{id}.zip",
    };

    [Fact]
    public void Two_teammates_snapshots_with_one_sequence_number_are_ordered_by_time()
    {
        // Both listed #4 before either had saved, so both took #5.
        var first = Partial("a", 5, minute: 1);
        var second = Partial("b", 5, minute: 2);
        var all = new[] { Partial("old", 4, minute: 0), first, second };

        Assert.Equal(["old", "a", "b"], BackupRetention.DeletionSet(second, all).Select(b => b.Id));
        Assert.Equal(["old", "a"], BackupRetention.DeletionSet(first, all).Select(b => b.Id));
    }

    [Fact]
    public async Task In_a_shared_bucket_a_target_not_loaded_here_is_never_taken_for_gone()
    {
        var settingsStore = new SettingsStore(Path.Combine(_tempDir, "settings.json"));
        var shared = new TeamStore(new LocalBackupStore(Path.Combine(_tempDir, "bucket")));
        var local = new LocalBackupStore(Path.Combine(_tempDir, "local"));
        var provider = new FixedProvider(local, shared);

        // A teammate's snapshot: its owner is a profile path on their machine.
        await SaveAsync(shared, "Prod_teammate", Path.Combine(_tempDir, "theirs", "WebApp", "Properties", "PublishProfiles", "Prod.pubxml"));
        await SaveAsync(local, "Prod_gone", Path.Combine(_tempDir, "deleted.pubxml"));

        var inventory = await new BackupCleanupService(provider, settingsStore).GetInventoryAsync([]);

        var teammate = inventory.Groups.Single(g => g.OwnerKey == "Prod_teammate");
        Assert.Equal(BackupOwnerStatus.NotLoadedHere, teammate.Status);
        Assert.Equal("team-bucket", teammate.Snapshots.Single().StorageTargetId);
        Assert.Equal(BackupOwnerStatus.Orphaned, inventory.Groups.Single(g => g.OwnerKey == "Prod_gone").Status);
    }

    private async Task SaveAsync(IBackupStore store, string key, string ownerId)
    {
        var package = Path.Combine(_tempDir, $"pkg_{Guid.NewGuid():N}.zip");
        await File.WriteAllTextAsync(package, "x");
        var backup = new DeploymentBackup
        {
            Id = Guid.NewGuid().ToString("N"),
            ProfileKey = key,
            ProfileName = "Prod",
            ProjectName = "WebApp",
            OwnerId = ownerId,
            CreatedBy = "maria on LAPTOP-7",
            Kind = BackupKind.FileSystem,
            CreatedUtc = DateTimeOffset.UtcNow,
            Sequence = 1,
            StorageTargetId = "someone-elses-id",
            PackagePath = store.ResolveKey(key, "snap.zip"),
        };
        await store.SaveAsync(backup, package);
    }

    // ---- Fakes ----------------------------------------------------------------

    /// <summary>A shared destination: local files standing in for a bucket, under its own id, stamping what it lists.</summary>
    private sealed class TeamStore(LocalBackupStore inner) : IBackupStore
    {
        public string TargetId => "team-bucket";

        public string Description => "team bucket";

        public string ResolveKey(string profileKey, string fileName) => inner.ResolveKey(profileKey, fileName);

        public async Task<IReadOnlyList<DeploymentBackup>> ListAsync(string profileKey, CancellationToken cancellationToken = default) =>
            Stamp(await inner.ListAsync(profileKey, cancellationToken));

        public async Task<IReadOnlyList<DeploymentBackup>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Stamp(await inner.ListAllAsync(cancellationToken));

        private IReadOnlyList<DeploymentBackup> Stamp(IReadOnlyList<DeploymentBackup> backups)
        {
            foreach (var backup in backups)
                backup.StorageTargetId = TargetId;
            return backups;
        }

        public Task SaveAsync(DeploymentBackup backup, string localPackagePath, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(backup, localPackagePath, cancellationToken);

        public Task<DownloadedPackage> DownloadAsync(DeploymentBackup backup, CancellationToken cancellationToken = default) =>
            inner.DownloadAsync(backup, cancellationToken);

        public Task DeleteAsync(DeploymentBackup backup, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(backup, cancellationToken);

        public Task<LastDeployment?> GetLastDeploymentAsync(string profileKey, CancellationToken cancellationToken = default) =>
            inner.GetLastDeploymentAsync(profileKey, cancellationToken);

        public Task SaveLastDeploymentAsync(string profileKey, LastDeployment deployment, CancellationToken cancellationToken = default) =>
            inner.SaveLastDeploymentAsync(profileKey, deployment, cancellationToken);
    }

    private sealed class FixedProvider(IBackupStore local, IBackupStore shared) : IBackupStoreProvider
    {
        public IBackupStore ForOwner(BackupOwner owner) => shared;

        public IBackupStore ForTargetId(string targetId) => targetId == shared.TargetId ? shared : local;

        public IReadOnlyList<IBackupStore> AllStores() => [local, shared];
    }

    private sealed class FixedEngine(PublishStatus status, PublishEngineKind kind) : IPublishEngine
    {
        public PublishEngineKind Kind => kind;

        public bool IsAvailable(out string? unavailableReason)
        {
            unavailableReason = null;
            return true;
        }

        public Task<PublishResult> PublishAsync(PublishJob job, Action<OutputLine> onOutput, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PublishResult { JobId = job.Id, DisplayName = job.DisplayName, Status = status });
    }

    private sealed class RecordingBackupService : IBackupService
    {
        public List<BackupOwner> Recorded { get; } = [];

        public bool Fail { get; init; }

        public Task<string> RecordDeploymentAsync(BackupOwner owner, CancellationToken cancellationToken = default)
        {
            if (Fail)
                throw new InvalidOperationException("bucket unreachable");
            Recorded.Add(owner);
            return Task.FromResult("team bucket");
        }

        public bool CanBackUp(BackupOwner owner, out string? reason)
        {
            reason = "not supported";
            return false;
        }

        public Task<DeploymentBackup?> BackUpAsync(PublishJob job, Action<OutputLine> onOutput, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DeploymentBackup>> ListAsync(BackupOwner owner, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RestoreAsync(
            DeploymentBackup backup, BackupOwner owner, PublishCredentials credentials,
            bool allowUntrustedCertificate, Action<OutputLine> onOutput, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DeploymentBackup>> GetDeletionSetAsync(DeploymentBackup backup, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(DeploymentBackup backup, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
