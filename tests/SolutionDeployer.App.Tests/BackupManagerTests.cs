using SolutionDeployer.App.ViewModels;
using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.Tests;

public sealed class BackupManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SettingsStore _settingsStore;
    private readonly LocalBackupStore _store;
    private readonly BackupCleanupService _cleanup;

    public BackupManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sd_mgr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var backupRoot = Path.Combine(_tempDir, "Backups");
        _settingsStore = new SettingsStore(Path.Combine(_tempDir, "settings.json"));
        _store = new LocalBackupStore(backupRoot);
        _cleanup = new BackupCleanupService(
            new BackupStoreProvider(_settingsStore, new NullCredentialStore(), backupRoot), _settingsStore);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private async Task SaveAsync(string ownerKey, string ownerId, int count)
    {
        for (var sequence = 1; sequence <= count; sequence++)
        {
            var package = Path.Combine(_tempDir, $"pkg_{Guid.NewGuid():N}.zip");
            await File.WriteAllTextAsync(package, "x");
            var id = Guid.NewGuid().ToString("N");
            await _store.SaveAsync(new DeploymentBackup
            {
                Id = id,
                ProfileKey = ownerKey,
                ProfileName = ownerKey,
                ProjectName = "App",
                OwnerId = ownerId,
                Kind = BackupKind.FileSystem,
                CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(sequence),
                Sequence = sequence,
                PackagePath = _store.ResolveKey(ownerKey, $"{sequence:D6}_{id[..8]}.zip"),
                SizeBytes = 1,
            }, package);
        }
    }

    private string ExistingProfile(string name)
    {
        var path = Path.Combine(_tempDir, name + ".pubxml");
        File.WriteAllText(path, "<Project />");
        return path;
    }

    private (BackupManagerViewModel Vm, List<string> Asked) CreateViewModel(AppSettings settings, bool confirm)
    {
        var asked = new List<string>();
        var vm = new BackupManagerViewModel(_cleanup, _settingsStore, settings, [], (_, message, _) =>
        {
            asked.Add(message);
            return Task.FromResult(confirm);
        });
        return (vm, asked);
    }

    [Fact]
    public async Task Clean_up_by_policy_deletes_only_after_confirmation()
    {
        await SaveAsync("Prod_1", ExistingProfile("Prod"), count: 5);
        var settings = new AppSettings { BackupRetention = 2 };

        var (declined, _) = CreateViewModel(settings, confirm: false);
        await declined.RefreshAsync();
        await declined.CleanUpByPolicyCommand.ExecuteAsync(null);
        Assert.Equal(5, (await _store.ListAsync("Prod_1")).Count);
        Assert.False(declined.DeletedAny);

        var (vm, asked) = CreateViewModel(settings, confirm: true);
        await vm.RefreshAsync();
        await vm.CleanUpByPolicyCommand.ExecuteAsync(null);

        Assert.Contains("Delete 3 snapshot(s)", Assert.Single(asked));
        Assert.Equal(2, (await _store.ListAsync("Prod_1")).Count);
        Assert.True(vm.DeletedAny);
    }

    [Fact]
    public async Task Tick_unused_selects_targets_no_longer_in_use_and_deletes_them()
    {
        await SaveAsync("Prod_1", ExistingProfile("Prod"), count: 1);
        await SaveAsync("Gone_2", Path.Combine(_tempDir, "Gone.pubxml"), count: 2);

        var (vm, _) = CreateViewModel(new AppSettings(), confirm: true);
        await vm.RefreshAsync();
        vm.SelectUnneededCommand.Execute(null);

        var ticked = Assert.Single(vm.Groups, g => g.IsChecked);
        Assert.Equal("Orphaned", ticked.StatusText);

        await vm.DeleteCheckedCommand.ExecuteAsync(null);

        Assert.Empty(await _store.ListAsync("Gone_2"));
        Assert.Single(await _store.ListAsync("Prod_1"));
        Assert.Single(vm.Groups);
    }

    [Fact]
    public void Editing_the_policy_saves_it()
    {
        var settings = new AppSettings();
        var (vm, _) = CreateViewModel(settings, confirm: true);

        Assert.False(vm.MaxAgeEnabled);
        vm.KeepLast = 4;
        vm.MaxAgeEnabled = true;
        vm.MaxAgeDays = 30;

        var saved = _settingsStore.Load();
        Assert.Equal(4, saved.BackupRetention);
        Assert.Equal(30, saved.BackupMaxAgeDays);

        vm.MaxAgeEnabled = false;
        Assert.Null(_settingsStore.Load().BackupMaxAgeDays);
    }
}
