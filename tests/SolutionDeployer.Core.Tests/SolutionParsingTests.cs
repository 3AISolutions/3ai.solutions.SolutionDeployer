using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Profiles;
using SolutionDeployer.Core.Solutions;

namespace SolutionDeployer.Core.Tests;

public sealed class SolutionParsingTests
{
    private static string FixtureSolution =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "SampleSolution", "Sample.slnx");

    private static SolutionParser CreateParser() => new(new ProfileDiscovery());

    [Fact]
    public async Task Parses_slnx_and_finds_all_projects()
    {
        var solution = await CreateParser().ParseAsync(FixtureSolution);

        Assert.Equal(2, solution.Projects.Count);
        Assert.Contains(solution.Projects, p => p.Name == "WebApp");
        Assert.Contains(solution.Projects, p => p.Name == "ClassLib");
    }

    [Fact]
    public async Task Discovers_both_publish_profiles_for_web_project()
    {
        var solution = await CreateParser().ParseAsync(FixtureSolution);
        var web = solution.Projects.Single(p => p.Name == "WebApp");

        Assert.Equal(2, web.Profiles.Count);
        Assert.Contains(web.Profiles, p => p.Name == "Production");
        Assert.Contains(web.Profiles, p => p.Name == "Staging");
    }

    [Fact]
    public async Task Reads_msdeploy_metadata_and_flags_credentials()
    {
        var solution = await CreateParser().ParseAsync(FixtureSolution);
        var prod = solution.Projects.Single(p => p.Name == "WebApp")
            .Profiles.Single(p => p.Name == "Production");

        Assert.Equal(PublishProfileFormat.PubXml, prod.Format);
        Assert.Equal("MSDeploy", prod.WebPublishMethod);
        Assert.Equal("deployuser", prod.UserName);
        Assert.True(prod.RequiresCredentials);
        Assert.Contains("8172", prod.ServerUrl);
    }

    [Fact]
    public async Task FileSystem_profile_does_not_require_credentials()
    {
        var solution = await CreateParser().ParseAsync(FixtureSolution);
        var staging = solution.Projects.Single(p => p.Name == "WebApp")
            .Profiles.Single(p => p.Name == "Staging");

        Assert.Equal("FileSystem", staging.WebPublishMethod);
        Assert.False(staging.RequiresCredentials);
    }
}
