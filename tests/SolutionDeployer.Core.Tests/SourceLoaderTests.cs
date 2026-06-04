using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Profiles;
using SolutionDeployer.Core.Projects;
using SolutionDeployer.Core.Solutions;

namespace SolutionDeployer.Core.Tests;

public sealed class SourceLoaderTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "SampleSolution");

    private static SourceLoader CreateLoader()
    {
        var discovery = new ProfileDiscovery();
        return new SourceLoader(new SolutionParser(discovery), new ProjectLoader(discovery));
    }

    [Fact]
    public async Task Solution_source_omits_projects_without_profiles()
    {
        var source = DeploymentSource.Solution(Path.Combine(FixtureDir, "Sample.slnx"));

        var loaded = await CreateLoader().LoadAsync(source);

        // ClassLib has no profiles → filtered out (#8); WebApp has two → kept.
        var project = Assert.Single(loaded.Projects);
        Assert.Equal("WebApp", project.Name);
        Assert.False(loaded.IsMissing);
    }

    [Fact]
    public async Task Project_source_is_included_even_with_no_profiles()
    {
        var source = DeploymentSource.Project(Path.Combine(FixtureDir, "ClassLib", "ClassLib.csproj"));

        var loaded = await CreateLoader().LoadAsync(source);

        var project = Assert.Single(loaded.Projects);
        Assert.Equal("ClassLib", project.Name);
        Assert.Empty(project.Profiles);
    }

    [Fact]
    public async Task Project_source_discovers_its_profiles()
    {
        var source = DeploymentSource.Project(Path.Combine(FixtureDir, "WebApp", "WebApp.csproj"));

        var loaded = await CreateLoader().LoadAsync(source);

        var project = Assert.Single(loaded.Projects);
        Assert.Equal(2, project.Profiles.Count);
    }

    [Fact]
    public async Task Missing_source_is_flagged_not_thrown()
    {
        var source = DeploymentSource.Solution(Path.Combine(FixtureDir, "DoesNotExist.slnx"));

        var loaded = await CreateLoader().LoadAsync(source);

        Assert.True(loaded.IsMissing);
        Assert.Empty(loaded.Projects);
    }
}
