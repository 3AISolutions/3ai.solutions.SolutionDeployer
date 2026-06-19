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

public sealed class ScriptRefreshTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _solutionPath;
    private readonly string _settingsPath;

    public ScriptRefreshTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sd_app_{Guid.NewGuid():N}");
        var webApp = Path.Combine(_tempDir, "WebApp");
        var profiles = Path.Combine(webApp, "Properties", "PublishProfiles");
        Directory.CreateDirectory(profiles);

        File.WriteAllText(Path.Combine(_tempDir, "Sample.slnx"),
            "<Solution>\n  <Project Path=\"WebApp/WebApp.csproj\" />\n</Solution>\n");
        File.WriteAllText(Path.Combine(webApp, "WebApp.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(profiles, "Prod.pubxml"),
            "<Project>\n  <PropertyGroup>\n    <WebPublishMethod>FileSystem</WebPublishMethod>\n  </PropertyGroup>\n</Project>\n");

        _solutionPath = Path.Combine(_tempDir, "Sample.slnx");
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    private (MainWindowViewModel Vm, FakeScriptEditor Editor) CreateViewModel()
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
        var backupService = new BackupService(
            processRunner, new MsDeployLocator(), rootOverride: Path.Combine(_tempDir, "Backups"));
        var runner = new DeploymentRunner(engineFactory, backupService);
        var settings = new SettingsStore(_settingsPath);
        var editor = new FakeScriptEditor();

        var vm = new MainWindowViewModel(
            sourceLoader, runner, engineFactory, settings,
            new FakeFilePicker(), new UpdateService(), new NullCredentialStore(), editor, backupService);

        return (vm, editor);
    }

    [Fact]
    public async Task Manually_added_script_survives_a_refresh()
    {
        var (vm, editor) = CreateViewModel();
        await vm.OpenRecentCommand.ExecuteAsync(_solutionPath);

        var project = vm.Sources.Single().Projects.Single();
        editor.Next = new ScriptTarget { Name = "Deploy", ScriptPath = "deploy.ps1" };
        await vm.AddScriptCommand.ExecuteAsync(project);

        Assert.Single(project.ScriptTargets);

        await vm.RefreshAllCommand.ExecuteAsync(null);

        var refreshedProject = vm.Sources.Single().Projects.Single();
        var script = Assert.Single(refreshedProject.ScriptTargets);
        Assert.Equal("Deploy", script.Name);
    }

    [Fact]
    public async Task Manually_added_script_survives_an_app_restart()
    {
        var (vm, editor) = CreateViewModel();
        await vm.OpenRecentCommand.ExecuteAsync(_solutionPath);
        editor.Next = new ScriptTarget { Name = "Deploy", ScriptPath = "deploy.ps1" };
        await vm.AddScriptCommand.ExecuteAsync(vm.Sources.Single().Projects.Single());

        // Simulate a restart: a fresh view model loading the same settings file.
        var (vm2, _) = CreateViewModel();
        await vm2.RunStartupLoadAsync();

        var project = vm2.Sources.Single().Projects.Single();
        Assert.Single(project.ScriptTargets);
    }

    [Fact]
    public async Task Script_on_directly_added_project_survives_refresh()
    {
        var (vm, editor) = CreateViewModel();
        var projectPath = Path.Combine(_tempDir, "WebApp", "WebApp.csproj");
        await vm.OpenRecentCommand.ExecuteAsync(projectPath); // .csproj => project source

        editor.Next = new ScriptTarget { Name = "Deploy", ScriptPath = "deploy.ps1" };
        await vm.AddScriptCommand.ExecuteAsync(vm.Sources.Single().Projects.Single());

        await vm.RefreshAllCommand.ExecuteAsync(null);

        Assert.Single(vm.Sources.Single().Projects.Single().ScriptTargets);
    }

    [Fact]
    public async Task Script_on_profileless_project_survives_refresh()
    {
        // A project with no publish profiles, added directly.
        var dir = Path.Combine(_tempDir, "Lib");
        Directory.CreateDirectory(dir);
        var projectPath = Path.Combine(dir, "Lib.csproj");
        File.WriteAllText(projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");

        var (vm, editor) = CreateViewModel();
        await vm.OpenRecentCommand.ExecuteAsync(projectPath);

        editor.Next = new ScriptTarget { Name = "Deploy", ScriptPath = "deploy.ps1" };
        await vm.AddScriptCommand.ExecuteAsync(vm.Sources.Single().Projects.Single());
        Assert.Single(vm.Sources.Single().Projects.Single().ScriptTargets);

        await vm.RefreshAllCommand.ExecuteAsync(null);

        Assert.Single(vm.Sources.Single().Projects.Single().ScriptTargets);
    }

    [Fact]
    public async Task Script_survives_refresh_when_project_is_in_two_sources()
    {
        var (vm, editor) = CreateViewModel();
        var projectPath = Path.Combine(_tempDir, "WebApp", "WebApp.csproj");

        // Same project added both via the solution and directly.
        await vm.OpenRecentCommand.ExecuteAsync(_solutionPath);
        await vm.OpenRecentCommand.ExecuteAsync(projectPath);
        Assert.Equal(2, vm.Sources.Count);

        editor.Next = new ScriptTarget { Name = "Deploy", ScriptPath = "deploy.ps1" };
        await vm.AddScriptCommand.ExecuteAsync(vm.Sources[0].Projects.Single());

        await vm.RefreshAllCommand.ExecuteAsync(null);

        // The script should reappear under the same project in both sources.
        Assert.All(vm.Sources, s => Assert.Single(s.Projects.Single().ScriptTargets));
    }

    [Fact]
    public async Task Editing_a_script_persists_across_refresh()
    {
        var (vm, editor) = CreateViewModel();
        await vm.OpenRecentCommand.ExecuteAsync(_solutionPath);
        var project = vm.Sources.Single().Projects.Single();

        editor.Next = new ScriptTarget { Name = "Deploy", ScriptPath = "deploy.ps1" };
        await vm.AddScriptCommand.ExecuteAsync(project);

        editor.Next = new ScriptTarget { Name = "Deploy v2", ScriptPath = "deploy2.ps1", Arguments = "-x 1" };
        await vm.EditScriptCommand.ExecuteAsync(project.ScriptTargets.Single());

        await vm.RefreshAllCommand.ExecuteAsync(null);

        var script = Assert.Single(vm.Sources.Single().Projects.Single().ScriptTargets);
        Assert.Equal("Deploy v2", script.Name);
        Assert.Equal("-x 1", script.Target.Arguments);
    }

    [Fact]
    public async Task Script_survives_refresh_with_classic_sln()
    {
        // Classic .sln (the most common real-world case), not .slnx.
        var slnPath = Path.Combine(_tempDir, "Classic.sln");
        File.WriteAllText(slnPath,
            "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
            "# Visual Studio Version 17\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"WebApp\", \"WebApp\\WebApp.csproj\", \"{2D3A8E1B-1111-2222-3333-444455556666}\"\n" +
            "EndProject\n" +
            "Global\nEndGlobal\n");

        var (vm, editor) = CreateViewModel();
        await vm.OpenRecentCommand.ExecuteAsync(slnPath);

        var project = vm.Sources.Single().Projects.Single();
        editor.Next = new ScriptTarget { Name = "Deploy", ScriptPath = "deploy.ps1" };
        await vm.AddScriptCommand.ExecuteAsync(project);
        Assert.Single(project.ScriptTargets);

        await vm.RefreshAllCommand.ExecuteAsync(null);

        Assert.Single(vm.Sources.Single().Projects.Single().ScriptTargets);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
