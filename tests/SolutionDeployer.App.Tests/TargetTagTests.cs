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

/// <summary>Tags on deploy targets: stored in settings, shown on rows, and filtering the list.</summary>
public sealed class TargetTagTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectPath;
    private readonly string _settingsPath;

    public TargetTagTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sd_tags_{Guid.NewGuid():N}");
        var webApp = Path.Combine(_tempDir, "WebApp");
        var profiles = Path.Combine(webApp, "Properties", "PublishProfiles");
        Directory.CreateDirectory(profiles);

        _projectPath = Path.Combine(webApp, "WebApp.csproj");
        File.WriteAllText(_projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        foreach (var name in new[] { "Prod", "Staging" })
            File.WriteAllText(Path.Combine(profiles, $"{name}.pubxml"),
                "<Project>\n  <PropertyGroup>\n    <WebPublishMethod>FileSystem</WebPublishMethod>\n  </PropertyGroup>\n</Project>\n");
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
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
        var settings = new SettingsStore(_settingsPath);
        var storeProvider = new BackupStoreProvider(settings, new NullCredentialStore(),
            localRootOverride: Path.Combine(_tempDir, "Backups"));
        var backupService = new BackupService(processRunner, new MsDeployLocator(), storeProvider);
        var editor = new FakeScriptEditor();

        var vm = new MainWindowViewModel(
            sourceLoader, new DeploymentRunner(engineFactory, backupService), engineFactory, settings,
            new FakeFilePicker(), new UpdateService(), new NullCredentialStore(), editor, backupService,
            new FakeDeployConfirmation(), new FakeGitHistory(), new FakeReleaseSummary(), new FakeRemoteTargets(),
            new FakeUpdatePrompt(), new FakeWhatsNew(), new FakeDeploySummary(), new FakeBackupManager(),
            new FakeAutoStart(), new FakeMcpServer(), new FakeSettingsBackup(), new FakeSnapshots());
        return (vm, editor);
    }

    private static ProfileViewModel Profile(MainWindowViewModel vm, string name) =>
        vm.Sources.Single().Projects.Single().Profiles.Single(p => p.Name == name);

    private static void Tick(MainWindowViewModel vm, string tag) =>
        vm.TagFilterOptions.Single(o => o.Tag == tag).IsSelected = true;

    private async Task<MainWindowViewModel> LoadTaggedAsync()
    {
        var (vm, _) = CreateViewModel();
        await vm.OpenRecentCommand.ExecuteAsync(_projectPath);
        vm.SetTargetTags(Profile(vm, "Prod"), [" prod ", "EU", "eu"]);
        vm.SetTargetTags(Profile(vm, "Staging"), ["staging", "eu"]);
        return vm;
    }

    [Fact]
    public async Task Tags_are_normalised_saved_and_listed_in_the_filter()
    {
        var vm = await LoadTaggedAsync();

        Assert.Equal(["EU", "prod"], Profile(vm, "Prod").Tags); // rows sort; settings keep the order given
        Assert.Equal(["EU", "prod", "staging"], vm.TagFilterOptions.Select(o => o.Tag));
        Assert.Equal(["prod", "EU"], new SettingsStore(_settingsPath).Load()
            .GetTargetTags(Profile(vm, "Prod").Profile.FilePath));
        Assert.False(vm.IsTagFilterActive);
        Assert.Equal("Tags", vm.TagFilterLabel);
    }

    [Fact]
    public async Task A_target_must_have_every_ticked_tag()
    {
        var vm = await LoadTaggedAsync();

        Tick(vm, "EU");
        Assert.True(Profile(vm, "Prod").IsVisible);
        Assert.True(Profile(vm, "Staging").IsVisible);

        Tick(vm, "prod");
        Assert.True(Profile(vm, "Prod").IsVisible);
        Assert.False(Profile(vm, "Staging").IsVisible);
        Assert.True(vm.IsTagFilterActive);
        Assert.Equal("Tags: EU, prod", vm.TagFilterLabel);

        vm.ClearTagFilterCommand.Execute(null);
        Assert.True(Profile(vm, "Staging").IsVisible);
        Assert.All(vm.TagFilterOptions, o => Assert.False(o.IsSelected));
    }

    [Fact]
    public async Task Tag_filter_and_text_filter_must_both_match()
    {
        var vm = await LoadTaggedAsync();

        Tick(vm, "EU");
        vm.FilterText = "Staging";

        Assert.False(Profile(vm, "Prod").IsVisible);
        Assert.True(Profile(vm, "Staging").IsVisible);

        // A project-name match no longer shows targets the tags rule out.
        vm.FilterText = "WebApp";
        Tick(vm, "staging");
        Assert.False(Profile(vm, "Prod").IsVisible);
        Assert.True(Profile(vm, "Staging").IsVisible);
        Assert.True(vm.Sources.Single().IsVisible);
    }

    [Fact]
    public async Task A_tag_nothing_has_hides_the_source()
    {
        var vm = await LoadTaggedAsync();
        Tick(vm, "prod");
        vm.SetTargetTags(Profile(vm, "Prod"), ["eu"]);

        // The ticked tag stays in the list so it can be unticked, but nothing matches it any more.
        Assert.Contains(vm.TagFilterOptions, o => o.Tag == "prod" && o.IsSelected);
        Assert.False(Profile(vm, "Prod").IsVisible);
        Assert.False(vm.Sources.Single().IsVisible);
    }

    [Fact]
    public async Task Select_all_ticks_only_the_visible_targets()
    {
        var vm = await LoadTaggedAsync();
        Tick(vm, "staging");

        vm.SelectAllCommand.Execute(null);

        Assert.True(Profile(vm, "Staging").IsSelected);
        Assert.False(Profile(vm, "Prod").IsSelected);
        Assert.Equal(1, vm.SelectedCount);

        vm.ClearTagFilterCommand.Execute(null);
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(2, vm.SelectedCount);
    }

    [Fact]
    public async Task Tags_and_the_tag_filter_survive_a_refresh()
    {
        var vm = await LoadTaggedAsync();
        Tick(vm, "staging");

        await vm.RefreshAllCommand.ExecuteAsync(null);

        Assert.Equal(["eu", "staging"], Profile(vm, "Staging").Tags);
        Assert.True(vm.IsTagFilterActive);
        Assert.False(Profile(vm, "Prod").IsVisible);
        Assert.True(Profile(vm, "Staging").IsVisible);
    }

    [Fact]
    public async Task Removing_a_script_forgets_its_tags()
    {
        var (vm, editor) = CreateViewModel();
        await vm.OpenRecentCommand.ExecuteAsync(_projectPath);
        var project = vm.Sources.Single().Projects.Single();
        editor.Next = new ScriptTarget { Name = "Ship", ScriptPath = "ship.ps1" };
        await vm.AddScriptCommand.ExecuteAsync(project);
        var script = project.ScriptTargets.Single();

        vm.SetTargetTags(script, ["linux"]);
        Assert.Equal(["linux"], script.Tags);
        Assert.Contains(vm.TagFilterOptions, o => o.Tag == "linux");

        await vm.RemoveScriptCommand.ExecuteAsync(script);

        Assert.False(new SettingsStore(_settingsPath).Load().TargetTags.ContainsKey(script.TagKey));
        Assert.DoesNotContain(vm.TagFilterOptions, o => o.Tag == "linux");
    }

    [Fact]
    public void Tag_editor_adds_normalised_tags_and_suggests_unused_ones()
    {
        var editor = new TagEditorViewModel("Prod", ["prod"], ["EU", "prod", "staging"]);
        Assert.Equal(["EU", "staging"], editor.Suggestions);

        editor.NewTag = " eu , Prod, us-east ";
        editor.AddCommand.Execute(null);
        Assert.Equal(["eu", "prod", "us-east"], editor.Tags);
        Assert.Equal(["staging"], editor.Suggestions);
        Assert.Equal(string.Empty, editor.NewTag);

        editor.RemoveCommand.Execute("prod");
        editor.NewTag = "linux"; // typed but not added: Save keeps it
        editor.SaveCommand.Execute(null);

        Assert.Equal(["eu", "linux", "us-east"], editor.Result);
    }

    [Fact]
    public void Cancelling_the_tag_editor_returns_nothing()
    {
        var editor = new TagEditorViewModel("Prod", ["prod"], []);
        editor.RemoveCommand.Execute("prod");

        editor.CancelCommand.Execute(null);

        Assert.Null(editor.Result);
    }

    [Fact]
    public void Clicking_an_existing_tag_adds_it_in_sorted_place()
    {
        var editor = new TagEditorViewModel("Prod", ["web", "Linux"], ["staging", "EU", "web"]);
        Assert.Equal(["Linux", "web"], editor.Tags);
        Assert.Equal(["EU", "staging"], editor.Suggestions);
        Assert.True(editor.HasSuggestions);

        editor.AddExistingCommand.Execute("EU");
        editor.AddExistingCommand.Execute("staging");

        Assert.Equal(["EU", "Linux", "staging", "web"], editor.Tags);
        Assert.Empty(editor.Suggestions);
        Assert.False(editor.HasSuggestions);
    }

    [Fact]
    public async Task Row_tags_are_sorted_whatever_order_they_were_added_in()
    {
        var (vm, _) = CreateViewModel();
        await vm.OpenRecentCommand.ExecuteAsync(_projectPath);

        vm.SetTargetTags(Profile(vm, "Prod"), ["web", "EU", "prod"]);
        vm.SetTargetTags(Profile(vm, "Staging"), ["prod", "web", "eu"]);

        Assert.Equal(["EU", "prod", "web"], Profile(vm, "Prod").Tags);
        Assert.Equal(["eu", "prod", "web"], Profile(vm, "Staging").Tags);
    }

    [Fact]
    public async Task Expand_collapse_toggle_closes_everything_then_opens_everything()
    {
        var (vm, _) = CreateViewModel();
        await vm.OpenRecentCommand.ExecuteAsync(_projectPath);
        var source = vm.Sources.Single();
        Assert.True(vm.IsAnyExpanded);

        vm.ToggleExpandAllCommand.Execute(null);
        Assert.False(source.IsExpanded);
        Assert.False(vm.IsAnyExpanded);

        vm.ToggleExpandAllCommand.Execute(null);
        Assert.True(source.IsExpanded);
        Assert.All(source.Projects, p => Assert.True(p.IsExpanded));
        Assert.True(vm.IsAnyExpanded);
    }
}
