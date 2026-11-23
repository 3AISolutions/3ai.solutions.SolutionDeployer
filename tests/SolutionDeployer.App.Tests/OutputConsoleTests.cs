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

/// <summary>The output console: per-target lines and filter, clearing on a new deploy, and the summary option.</summary>
public sealed class OutputConsoleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectPath;
    private readonly string _settingsPath;

    public OutputConsoleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sd_output_{Guid.NewGuid():N}");
        var webApp = Path.Combine(_tempDir, "WebApp");
        var profiles = Path.Combine(webApp, "Properties", "PublishProfiles");
        Directory.CreateDirectory(profiles);

        _projectPath = Path.Combine(webApp, "WebApp.csproj");
        File.WriteAllText(_projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(profiles, "Prod.pubxml"),
            "<Project>\n  <PropertyGroup>\n    <WebPublishMethod>FileSystem</WebPublishMethod>\n" +
            $"    <PublishUrl>{Path.Combine(_tempDir, "site")}</PublishUrl>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(webApp, "ok.cmd"), "@echo deployed\r\n@exit /b 0\r\n");
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private (MainWindowViewModel Vm, FakeScriptEditor Editor, FakeDeploySummary Summary) CreateViewModel()
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
        var editor = new FakeScriptEditor();
        var summary = new FakeDeploySummary();

        var vm = new MainWindowViewModel(
            sourceLoader, new DeploymentRunner(engineFactory, backupService), engineFactory, settings,
            new FakeFilePicker(), new UpdateService(), new NullCredentialStore(), editor, backupService,
            new FakeDeployConfirmation(), new FakeGitHistory(), new FakeReleaseSummary(), new FakeRemoteTargets(),
            new FakeUpdatePrompt(), new FakeWhatsNew(), summary, new FakeBackupManager(),
            new FakeAutoStart(), new FakeMcpServer(), new FakeSettingsBackup(), new FakeSnapshots());
        return (vm, editor, summary);
    }

    private async Task<(MainWindowViewModel Vm, ProjectViewModel Project, FakeScriptEditor Editor, FakeDeploySummary Summary)> LoadAsync()
    {
        var (vm, editor, summary) = CreateViewModel();
        await vm.OpenRecentCommand.ExecuteAsync(_projectPath);
        return (vm, vm.Sources.Single().Projects.Single(), editor, summary);
    }

    private static async Task<ScriptTargetViewModel> AddScriptAsync(MainWindowViewModel vm, ProjectViewModel project,
        FakeScriptEditor editor, string name)
    {
        editor.Next = new ScriptTarget { Name = name, ScriptPath = "ok.cmd" };
        await vm.AddScriptCommand.ExecuteAsync(project);
        return project.ScriptTargets.Single(s => s.Name == name);
    }

    [Fact]
    public async Task Target_lines_carry_their_target_name_and_a_colour_of_their_own()
    {
        var (_, project, _, _) = await LoadAsync();
        var prod = project.Profiles.Single();
        var ship = new ScriptTargetViewModel(project, new ScriptTarget { Name = "Ship", ScriptPath = "ok.cmd" });
        var output = new OutputLog(100);

        var a = output.Line(prod, "building");
        var b = output.Line(ship, "copying");
        output.Append(LogLine.System("── Deploying 2 target(s) ──"));

        Assert.Equal("[WebApp → Prod] building", a.ToString());
        Assert.Equal("[WebApp → Ship] copying", b.ToString());
        Assert.NotNull(a.TargetBrush);
        Assert.NotEqual(a.TargetBrush, b.TargetBrush);
        Assert.Same(a.TargetBrush, output.Line(prod, "again").TargetBrush);
        Assert.Equal(string.Empty, output.All.Single().Prefix);
    }

    [Fact]
    public async Task Choosing_a_target_shows_only_its_lines_including_ones_written_afterwards()
    {
        var (_, project, _, _) = await LoadAsync();
        var prod = project.Profiles.Single();
        var ship = new ScriptTargetViewModel(project, new ScriptTarget { Name = "Ship", ScriptPath = "ok.cmd" });
        var output = new OutputLog(100);
        output.Append(LogLine.System("heading"));
        output.Append(output.Line(prod, "p1"));
        output.Append(output.Line(ship, "s1"));

        output.Target = ship;
        Assert.Equal(["s1"], output.Visible.Select(l => l.Text));

        output.Append(output.Line(prod, "p2"));
        output.Append(output.Line(ship, "s2"));
        Assert.Equal(["s1", "s2"], output.Visible.Select(l => l.Text));

        output.Target = null;
        Assert.Equal(["heading", "p1", "s1", "p2", "s2"], output.Visible.Select(l => l.Text));
    }

    [Fact]
    public async Task Capped_output_drops_the_oldest_line_from_the_shown_lines_too()
    {
        var (_, project, _, _) = await LoadAsync();
        var prod = project.Profiles.Single();
        var output = new OutputLog(3) { Target = prod };

        for (var i = 1; i <= 5; i++)
            output.Append(output.Line(prod, $"line {i}"));

        Assert.Equal(["line 3", "line 4", "line 5"], output.All.Select(l => l.Text));
        Assert.Equal(["line 3", "line 4", "line 5"], output.Visible.Select(l => l.Text));
    }

    [Fact]
    public async Task Clicking_a_row_filters_the_output_and_clicking_it_again_shows_everything()
    {
        var (vm, project, _, _) = await LoadAsync();
        var prod = project.Profiles.Single();

        vm.ToggleOutputTarget(prod);
        Assert.Same(prod, vm.OutputTarget);
        Assert.True(prod.IsOutputFocused);
        Assert.True(vm.IsOutputFiltered);
        Assert.Equal("Output: WebApp → Prod", vm.OutputTitle);

        vm.ToggleOutputTarget(prod);
        Assert.Null(vm.OutputTarget);
        Assert.False(prod.IsOutputFocused);
        Assert.Equal("Output", vm.OutputTitle);
    }

    [Fact]
    public void Summary_after_deploy_is_on_by_default_and_saved_when_changed()
    {
        var (vm, _, _) = CreateViewModel();
        Assert.True(vm.ShowSummaryAfterDeploy);

        vm.ShowSummaryAfterDeploy = false;

        Assert.False(new SettingsStore(_settingsPath).Load().ShowSummaryAfterDeploy);
        Assert.False(CreateViewModel().Vm.ShowSummaryAfterDeploy);
    }

    [Fact]
    public async Task A_new_deploy_clears_the_output_and_shows_the_summary_only_when_enabled()
    {
        if (!OperatingSystem.IsWindows())
            return; // Uses a .cmd for a dependency-free, deterministic script.

        var (vm, project, editor, summary) = await LoadAsync();
        var ship = await AddScriptAsync(vm, project, editor, "Ship");
        ship.IsSelected = true;

        vm.ShowSummaryAfterDeploy = false;
        await vm.DeployCommand.ExecuteAsync(null);
        Assert.Null(summary.Last);
        Assert.Contains(vm.Log, l => l.Text.Contains("All 1 target(s) succeeded"));

        vm.ShowSummaryAfterDeploy = true;
        await vm.DeployCommand.ExecuteAsync(null);
        Assert.NotNull(summary.Last);

        // Only the second run's lines are left.
        Assert.Single(vm.Log, l => l.Text.StartsWith("── Deploying"));
        Assert.Single(vm.Log, l => l.Text.Contains("succeeded"));
    }

    [Fact]
    public async Task A_new_deploy_keeps_the_filter_only_when_that_target_deploys_again()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (vm, project, editor, _) = await LoadAsync();
        var ship = await AddScriptAsync(vm, project, editor, "Ship");
        var other = await AddScriptAsync(vm, project, editor, "Other");
        vm.ShowSummaryAfterDeploy = false;
        ship.IsSelected = true;

        vm.ToggleOutputTarget(ship);
        await vm.DeployCommand.ExecuteAsync(null);
        Assert.Same(ship, vm.OutputTarget);

        vm.ToggleOutputTarget(other);
        await vm.DeployCommand.ExecuteAsync(null);
        Assert.Null(vm.OutputTarget);
        Assert.False(other.IsOutputFocused);
    }
}
