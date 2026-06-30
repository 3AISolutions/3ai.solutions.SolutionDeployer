using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.Core.Tests;

public sealed class RemoteBackupTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public RemoteBackupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sd_remote_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void ProfileBackupTarget_defaults_to_local_and_round_trips()
    {
        var store = new SettingsStore(_settingsPath);
        var settings = store.Load();

        Assert.Equal(S3BackupTarget.LocalId, settings.GetBackupTargetId("c:/p/Prod.pubxml"));

        settings.RemoteBackupTargets.Add(new S3BackupTarget
        {
            Id = "t1", Name = "Wasabi", ServiceUrl = "https://s3.wasabisys.com",
            Bucket = "deploys", AccessKey = "AK", Region = "eu-central-1",
        });
        settings.SetBackupTargetId("c:/p/Prod.pubxml", "t1");
        settings.SetBackupTargetId("c:/p/Other.pubxml", S3BackupTarget.LocalId); // local should not be stored
        store.Save(settings);

        var reloaded = new SettingsStore(_settingsPath).Load();
        var target = Assert.Single(reloaded.RemoteBackupTargets);
        Assert.Equal("Wasabi", target.Name);
        Assert.Equal("deploys", target.Bucket);
        Assert.Equal("t1", reloaded.GetBackupTargetId("c:/p/Prod.pubxml"));
        Assert.Equal(S3BackupTarget.LocalId, reloaded.GetBackupTargetId("c:/p/Other.pubxml"));
        Assert.False(reloaded.ProfileBackupTarget.ContainsKey("c:/p/Other.pubxml"));
    }

    [Fact]
    public void Provider_resolves_local_s3_and_rejects_unknown()
    {
        var store = new SettingsStore(_settingsPath);
        var settings = store.Load();
        settings.RemoteBackupTargets.Add(new S3BackupTarget { Id = "t1", Name = "Minio", Bucket = "b" });
        store.Save(settings);

        var provider = new BackupStoreProvider(store, new NullCredentialStore(), localRootOverride: _tempDir);

        Assert.IsType<LocalBackupStore>(provider.ForTargetId(S3BackupTarget.LocalId));
        Assert.IsType<LocalBackupStore>(provider.ForTargetId(""));

        var s3 = provider.ForTargetId("t1");
        Assert.IsType<S3BackupStore>(s3);
        Assert.Equal("t1", s3.TargetId);

        Assert.Throws<InvalidOperationException>(() => provider.ForTargetId("does-not-exist"));
    }

    [Fact]
    public void S3_key_layout_includes_prefix_profile_and_file()
    {
        var s3 = new S3BackupStore(
            new S3BackupTarget { Id = "t", Bucket = "b", Prefix = "/backups/" }, secretKey: "x");

        Assert.Equal("backups/Prod_abc/file.zip", s3.ResolveKey("Prod_abc", "file.zip"));
    }
}
