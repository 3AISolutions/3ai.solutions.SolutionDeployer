using SolutionDeployer.App.Services;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Profiles;
using SolutionDeployer.Core.Projects;
using SolutionDeployer.Core.Publishing;
using SolutionDeployer.Core.Solutions;

namespace SolutionDeployer.App.Tests;

/// <summary>A row's Snapshots window: listing, restoring through the main window, and deleting.</summary>
public sealed class SnapshotsWindowTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectPath;
    private readonly string _sitePath;
    private readonly string _backupRoot;
    private BackupService? _backupService;

    public SnapshotsWindowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sd_snapshots_{Guid.NewGuid():N}");
        var webApp = Path.Combine(_tempDir, "WebApp");
        var profiles = Path.Combine(webApp, "Properties", "PublishProfiles");
        Directory.CreateDirectory(profiles);
        _sitePath = Path.Combine(_tempDir, "site");
        Directory.CreateDirectory(_sitePath);
        _backupRoot = Path.Combine(_tempDir, "Backups");

        _projectPath = Path.Combine(webApp, "WebApp.csproj");
        File.WriteAllText(_projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(profiles, "Prod.pubxml"),
            "<Project>\n  <PropertyGroup>\n    <WebPublishMethod>FileSystem</WebPublishMethod>\n" +
            $"    <PublishUrl>{_sitePath}</PublishUrl>\n  </PropertyGroup>\n</Project>\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private (MainWindowViewModel Vm, FakeSnapshots Snapshots) CreateViewModel()
    {
        var discovery = new ProfileDiscovery();
        var sourceLoader = new SourceLoader(new SolutionParser(discovery), new ProjectLoader(discovery));
        var processRunner = new ProcessRunner();
        var engineFactory = new PublishEngineFactory(new IPublishEngine[]
        {
            new DotnetPublishEngine(processRunner),
            new MsBuildPublishEngine(processRunner, new MsBuildLocator()),
            new ScriptPublishEngine(processRunner),
        });
        var settings = new SettingsStore(Path.Combine(_tempDir, "settings.json"));
        var storeProvider = new BackupStoreProvider(settings, new NullCredentialStore(), localRootOverride: _backupRoot);
        _backupService = new BackupService(processRunner, new MsDeployLocator(), storeProvider);
        var snapshots = new FakeSnapshots();

        var vm = new MainWindowViewModel(
            sourceLoader, new DeploymentRunner(engineFactory, _backupService), engineFactory, settings,
            new FakeFilePicker(), new UpdateService(), new NullCredentialStore(), new FakeScriptEditor(), _backupService,
            new FakeDeployConfirmation(), new FakeGitHistory(), new FakeReleaseSummary(), new FakeRemoteTargets(),
            new FakeUpdatePrompt(), new FakeWhatsNew(), new FakeDeploySummary(), new FakeBackupManager(),
            new FakeAutoStart(), new FakeMcpServer(), new FakeSettingsBackup(), snapshots);
        return (vm, snapshots);
    }

    private static async Task<ProfileViewModel> LoadProfileAsync(MainWindowViewModel vm, string projectPath)
    {
        await vm.OpenRecentCommand.ExecuteAsync(projectPath);
        var profile = vm.Sources.Single().Projects.Single().Profiles.Single();
        Assert.True(profile.SupportsBackup);
        return profile;
    }

    /// <summary>Saves partial snapshots #1..#count for the profile straight into its local store.</summary>
    private async Task SavePartialSnapshotsAsync(ProfileViewModel profile, int count)
    {
        var store = new LocalBackupStore(_backupRoot);
        var key = BackupService.KeyFor(profile.BackupOwner);
        for (var sequence = 1; sequence <= count; sequence++)
        {
            var package = Path.Combine(_tempDir, $"pkg_{Guid.NewGuid():N}.zip");
            await File.WriteAllTextAsync(package, "x");
            var id = Guid.NewGuid().ToString("N");
            await store.SaveAsync(new DeploymentBackup
            {
                Id = id,
                ProfileKey = key,
                ProfileName = profile.Name,
                ProjectName = "WebApp",
                OwnerId = profile.Profile.FilePath,
                Kind = BackupKind.MsDeployPartial,
                CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(sequence),
                Sequence = sequence,
                PackagePath = store.ResolveKey(key, $"{sequence:D6}_{id[..8]}.zip"),
                SizeBytes = 1,
                SavedPaths = ["index.html"],
            }, package);
        }
    }

    [Fact]
    public async Task Restoring_from_the_window_closes_it_and_runs_the_restore_in_the_main_window()
    {
        var (vm, snapshots) = CreateViewModel();
        var profile = await LoadProfileAsync(vm, _projectPath);

        var index = Path.Combine(_sitePath, "index.html");
        await File.WriteAllTextAsync(index, "v1");
        var job = new PublishJob { Project = profile.Parent.Project, Profile = profile.Profile, Engine = PublishEngineKind.Dotnet };
        Assert.NotNull(await _backupService!.BackUpAsync(job, _ => { }));
        await File.WriteAllTextAsync(index, "v2");

        snapshots.OnShow = async window =>
        {
            await window.RefreshAsync();
            Assert.Same(profile, window.Host);
            Assert.False(window.IsEmpty);
            var entry = Assert.Single(window.Host.Backups);
            Assert.Equal("Full", entry.KindText);
            Assert.Same(entry, window.Host.SelectedBackup);
            Assert.True(window.RestoreCommand.CanExecute(null));
            window.RestoreCommand.Execute(null);
        };

        await vm.ShowSnapshotsCommand.ExecuteAsync(profile);

        Assert.True(snapshots.Closed);
        Assert.NotNull(snapshots.Last!.RestoreRequested);
        Assert.Equal("v1", await File.ReadAllTextAsync(index));
        Assert.False(vm.IsRunning);
        Assert.Contains(vm.Log, l => l.Text.Contains("Restored Prod"));
    }

    [Fact]
    public async Task Closing_without_restoring_changes_nothing()
    {
        var (vm, snapshots) = CreateViewModel();
        var profile = await LoadProfileAsync(vm, _projectPath);
        snapshots.OnShow = window =>
        {
            window.CloseCommand.Execute(null);
            return Task.CompletedTask;
        };

        await vm.ShowSnapshotsCommand.ExecuteAsync(profile);

        Assert.True(snapshots.Closed);
        Assert.Null(snapshots.Last!.RestoreRequested);
        Assert.DoesNotContain(vm.Log, l => l.Text.Contains("Restoring"));
    }

    [Fact]
    public async Task A_target_without_snapshots_shows_the_empty_state()
    {
        var (vm, snapshots) = CreateViewModel();
        var profile = await LoadProfileAsync(vm, _projectPath);

        snapshots.OnShow = async window =>
        {
            await window.RefreshAsync();
            Assert.True(window.IsEmpty);
            Assert.False(window.Host.IsLoadingBackups);
            Assert.Null(window.RestoreNote);
            Assert.False(window.RestoreCommand.CanExecute(null));
            Assert.False(window.DeleteCommand.CanExecute(null));
        };

        await vm.ShowSnapshotsCommand.ExecuteAsync(profile);
        Assert.NotNull(snapshots.Last);
    }

    [Fact]
    public async Task Restore_and_delete_are_disabled_while_a_deploy_runs()
    {
        var (vm, snapshots) = CreateViewModel();
        var profile = await LoadProfileAsync(vm, _projectPath);
        await SavePartialSnapshotsAsync(profile, count: 1);

        snapshots.OnShow = async window =>
        {
            await window.RefreshAsync();
            Assert.True(window.RestoreCommand.CanExecute(null));

            vm.IsRunning = true; // e.g. a deploy started over MCP while the window is open
            Assert.False(window.RestoreCommand.CanExecute(null));
            Assert.False(window.DeleteCommand.CanExecute(null));

            vm.IsRunning = false;
            Assert.True(window.RestoreCommand.CanExecute(null));
        };

        await vm.ShowSnapshotsCommand.ExecuteAsync(profile);
    }

    [Fact]
    public async Task Selecting_an_older_partial_snapshot_warns_that_newer_deploys_are_undone()
    {
        var (vm, snapshots) = CreateViewModel();
        var profile = await LoadProfileAsync(vm, _projectPath);
        await SavePartialSnapshotsAsync(profile, count: 3);

        snapshots.OnShow = async window =>
        {
            await window.RefreshAsync();
            Assert.Equal(3, window.Host.Backups.Count);

            window.Host.SelectedBackup = window.Host.Backups.Single(b => b.Backup.Sequence == 3);
            Assert.False(window.RestoreUndoesNewer);

            window.Host.SelectedBackup = window.Host.Backups.Single(b => b.Backup.Sequence == 1);
            Assert.True(window.RestoreUndoesNewer);
            Assert.Contains("#2, #3", window.RestoreNote);
        };

        await vm.ShowSnapshotsCommand.ExecuteAsync(profile);
    }

    [Fact]
    public async Task Deleting_a_partial_snapshot_asks_first_and_takes_the_older_ones_with_it()
    {
        var (vm, snapshots) = CreateViewModel();
        var profile = await LoadProfileAsync(vm, _projectPath);
        await SavePartialSnapshotsAsync(profile, count: 3);
        snapshots.ConfirmAnswers.Enqueue(false);
        snapshots.ConfirmAnswers.Enqueue(true);

        snapshots.OnShow = async window =>
        {
            await window.RefreshAsync();
            window.Host.SelectedBackup = window.Host.Backups.Single(b => b.Backup.Sequence == 2);

            await window.DeleteCommand.ExecuteAsync(null);
            Assert.Equal("Nothing deleted.", window.Status);
            Assert.Equal(3, window.Host.Backups.Count);

            await window.DeleteCommand.ExecuteAsync(null);
            Assert.Equal("Deleted 2 snapshots for Prod.", window.Status);
            var left = Assert.Single(window.Host.Backups);
            Assert.Equal(3, left.Backup.Sequence);
            Assert.Same(left, window.Host.SelectedBackup);
            Assert.False(window.IsEmpty);
        };

        await vm.ShowSnapshotsCommand.ExecuteAsync(profile);

        Assert.Equal(2, snapshots.Asked.Count);
        Assert.Contains("#1", snapshots.Asked[0]);
        Assert.False(snapshots.Closed);
        Assert.Equal("📸 1", profile.BackupBadge);
        Assert.Null(snapshots.Last!.RestoreRequested);
    }
}
