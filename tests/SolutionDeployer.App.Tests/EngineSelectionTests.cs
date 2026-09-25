using SolutionDeployer.App.ViewModels;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.Tests;

public sealed class EngineSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-engine-" + Guid.NewGuid().ToString("N"));

    public EngineSelectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ProfileViewModel ProfileFor(string projectXml, PublishEngineKind defaultEngine)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.vbproj");
        File.WriteAllText(path, projectXml);
        var project = new ProjectViewModel(new DeploymentProject { Name = "App", ProjectPath = path });
        var profile = new PublishProfile { Name = "Prod", FilePath = Path.Combine(_dir, "Prod.pubxml"), Format = PublishProfileFormat.PubXml };
        return new ProfileViewModel(project, profile, defaultEngine, null, null, credentialStoreAvailable: false);
    }

    private const string ClassicWebProject =
        """
        <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <ProjectTypeGuids>{349c5851-65df-11da-9384-00065b846f21};{F184B08F-C81C-45F6-A57F-5ABD9991F28F}</ProjectTypeGuids>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public void Classic_web_project_only_offers_msbuild_and_ignores_a_saved_dotnet_choice()
    {
        var profile = ProfileFor(ClassicWebProject, PublishEngineKind.Dotnet);

        Assert.Equal(PublishEngineKind.MsBuild, profile.Engine);
        Assert.Equal([PublishEngineKind.MsBuild], profile.Engines);

        profile.Engine = PublishEngineKind.Dotnet; // e.g. restoring an older saved selection
        Assert.Equal(PublishEngineKind.MsBuild, profile.Engine);
    }

    [Fact]
    public void Sdk_project_keeps_both_engines_and_the_default()
    {
        var profile = ProfileFor("""<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""", PublishEngineKind.Dotnet);

        Assert.Equal(PublishEngineKind.Dotnet, profile.Engine);
        Assert.Equal(2, profile.Engines.Count);
    }
}
