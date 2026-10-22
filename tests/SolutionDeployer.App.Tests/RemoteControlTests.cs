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

/// <summary>Deploys started over MCP, and the tray / sign-in options.</summary>
public sealed class RemoteControlTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectPath;
    private readonly string _settingsPath;

    public RemoteControlTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sd_remote_{Guid.NewGuid():N}");
        var webApp = Path.Combine(_tempDir, "WebApp");
        var profiles = Path.Combine(webApp, "Properties", "PublishProfiles");
        Directory.CreateDirectory(profiles);

        _projectPath = Path.Combine(webApp, "WebApp.csproj");
        File.WriteAllText(_projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(profiles, "Prod.pubxml"),
            "<Project>\n  <PropertyGroup>\n    <WebPublishMethod>MSDeploy</WebPublishMethod>\n    <MSDeployServiceURL>https://example:8172/msdeploy.axd</MSDeployServiceURL>\n" +
            "    <UserName>deployer</UserName>\n  </PropertyGroup>\n</Project>\n");
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private (MainWindowViewModel Vm, FakeAutoStart AutoStart, FakeMcpServer Mcp) CreateViewModel()
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
        var settings = new SettingsStore(_settingsPath);
        var storeProvider = new BackupStoreProvider(settings, new NullCredentialStore(),
            localRootOverride: Path.Combine(_tempDir, "Backups"));
        var backupService = new BackupService(processRunner, new MsDeployLocator(), storeProvider);
        var autoStart = new FakeAutoStart();
        var mcp = new FakeMcpServer();

        var vm = new MainWindowViewModel(
            sourceLoader, new DeploymentRunner(engineFactory, backupService), engineFactory, settings,
            new FakeFilePicker(), new UpdateService(), new NullCredentialStore(), new FakeScriptEditor(), backupService,
            new FakeDeployConfirmation(), new FakeGitHistory(), new FakeReleaseSummary(), new FakeRemoteTargets(),
            new FakeUpdatePrompt(), new FakeWhatsNew(), new FakeDeploySummary(), new FakeBackupManager(),
            autoStart, mcp);
        return (vm, autoStart, mcp);
    }

    private sealed class RecordingObserver : IRunObserver
    {
        public void Output(string line) { }
        public void TargetChanged(BackupHostViewModel target, PublishStatus status, string? result) { }
        public void Completed(PublishStatus status, string message) { }
    }

    [Fact]
    public async Task Remote_deploy_refuses_a_target_without_a_saved_password()
    {
        var (vm, _, _) = CreateViewModel();
        await vm.OpenRecentCommand.ExecuteAsync(_projectPath);
        var profile = vm.Sources.Single().Projects.Single().Profiles.Single();
        Assert.True(profile.RequiresCredentials);

        var error = vm.StartRemoteDeploy([profile], null, null, new RecordingObserver());

        Assert.NotNull(error);
        Assert.Contains("No saved password for Prod", error);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Remote_deploy_refuses_a_script_that_cannot_run()
    {
        var (vm, _, _) = CreateViewModel();
        await vm.OpenRecentCommand.ExecuteAsync(_projectPath);
        var project = vm.Sources.Single().Projects.Single();
        var script = new ScriptTargetViewModel(project, new ScriptTarget { Name = "Ship", ScriptPath = "missing.ps1" });

        var error = vm.StartRemoteDeploy([script], null, null, new RecordingObserver());

        Assert.Equal("Script 'Ship' can't run: Script file not found.", error);
    }

    [Fact]
    public async Task Profile_ids_are_stable_across_reloads()
    {
        var (vm, _, _) = CreateViewModel();
        await vm.OpenRecentCommand.ExecuteAsync(_projectPath);
        var first = McpDeployerBridge.IdOf(vm.Sources.Single().Projects.Single().Profiles.Single());

        await vm.RefreshAllCommand.ExecuteAsync(null);
        var second = McpDeployerBridge.IdOf(vm.Sources.Single().Projects.Single().Profiles.Single());

        Assert.Equal(first, second);
        Assert.StartsWith("profile-", first);
    }

    [Fact]
    public void Background_options_are_saved_and_applied()
    {
        var (vm, autoStart, mcp) = CreateViewModel();
        Assert.True(vm.CloseToTray); // default: closing keeps the app (and its MCP server) running
        Assert.False(vm.StartWithOperatingSystem);

        vm.CloseToTray = false;
        vm.StartWithOperatingSystem = true;
        vm.McpServerEnabled = true;

        Assert.True(autoStart.Enabled);
        Assert.True(mcp.IsRunning);

        var saved = new SettingsStore(_settingsPath).Load();
        Assert.False(saved.CloseToTray);
        Assert.True(saved.StartWithOperatingSystem);
        Assert.True(saved.McpServerEnabled);
    }

    [Fact]
    public void Sign_in_entries_launch_the_app_in_the_background()
    {
        var desktop = AutoStartService.LinuxDesktopEntry("/opt/Solution Deployer/SolutionDeployer.App");
        Assert.Contains("Exec=\"/opt/Solution Deployer/SolutionDeployer.App\" --background", desktop);

        var plist = AutoStartService.MacLaunchAgent("/Applications/A&B.app/Contents/MacOS/SolutionDeployer.App");
        Assert.Contains("<string>/Applications/A&amp;B.app/Contents/MacOS/SolutionDeployer.App</string>", plist);
        Assert.Contains("<string>--background</string>", plist);
    }
}
