using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Projects;
using SolutionDeployer.Core.Publishing;
using SolutionDeployer.Core.Solutions;
using SolutionDeployer.Core.Profiles;

namespace SolutionDeployer.Core.Tests;

public sealed class MsBuildArgumentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-msbuild-" + Guid.NewGuid().ToString("N"));

    public MsBuildArgumentsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteClassicWebProject()
    {
        var path = Path.Combine(_dir, "Legacy.Web.vbproj");
        File.WriteAllText(path,
            """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <ProjectTypeGuids>{349c5851-65df-11da-9384-00065b846f21};{F184B08F-C81C-45F6-A57F-5ABD9991F28F}</ProjectTypeGuids>
              </PropertyGroup>
            </Project>
            """);
        return path;
    }

    private static PublishJob Job(DeploymentProject project) => new()
    {
        Project = project,
        Engine = PublishEngineKind.MsBuild,
        Configuration = "Release-Admin",
        Profile = new PublishProfile { Name = "Prod", FilePath = "Prod.pubxml", Format = PublishProfileFormat.PubXml },
    };

    [Fact]
    public void Detects_classic_web_projects_but_not_sdk_projects()
    {
        Assert.True(ProjectFormat.IsClassicWebProject(WriteClassicWebProject()));

        var sdk = Path.Combine(_dir, "Sdk.csproj");
        File.WriteAllText(sdk, """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Assert.False(ProjectFormat.IsClassicWebProject(sdk));
    }

    [Fact]
    public void Sdk_project_referencing_a_classic_project_requires_msbuild()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Common"));
        File.WriteAllText(Path.Combine(_dir, "Common", "Common.vbproj"),
            """<Project ToolsVersion="12.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"></Project>""");

        var api = Path.Combine(_dir, "Api.csproj");
        File.WriteAllText(api,
            """<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><ProjectReference Include="Common\Common.vbproj" /></ItemGroup></Project>""");
        var standalone = Path.Combine(_dir, "Standalone.csproj");
        File.WriteAllText(standalone, """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");

        Assert.True(ProjectFormat.RequiresMsBuild(api, out var classic));
        Assert.EndsWith("Common.vbproj", classic);
        Assert.False(ProjectFormat.RequiresMsBuild(standalone, out _));
    }

    [Fact]
    public void Classic_web_project_publishes_through_its_solution_with_DeployOnBuild()
    {
        var project = new DeploymentProject
        {
            Name = "Legacy.Web",
            ProjectPath = WriteClassicWebProject(),
            SolutionPath = Path.Combine(_dir, "App.slnx"),
            SolutionTargetName = "Legacy_Web",
        };

        var args = MsBuildPublishEngine.BuildTargetArguments(Job(project));

        Assert.Equal(project.SolutionPath, args[0]);
        Assert.Contains("/t:Legacy_Web", args);
        Assert.Contains("/p:DeployOnBuild=true", args);
        Assert.Contains("/p:Platform=Any CPU", args);
        Assert.DoesNotContain("/t:Publish", args);
    }

    [Fact]
    public async Task Dotnet_engine_refuses_a_classic_web_project_with_a_clear_message()
    {
        var project = new DeploymentProject { Name = "Legacy.Web", ProjectPath = WriteClassicWebProject() };
        var output = new List<OutputLine>();

        var result = await new DotnetPublishEngine(new ProcessRunner()).PublishAsync(Job(project), output.Add);

        Assert.Equal(PublishStatus.Failed, result.Status);
        Assert.Contains("msbuild", result.ErrorMessage);
        Assert.Contains(output, o => o.Severity == OutputSeverity.Error);
    }

    [Fact]
    public void Sdk_project_keeps_the_publish_target()
    {
        var sdk = Path.Combine(_dir, "Api.csproj");
        File.WriteAllText(sdk, """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");

        var args = MsBuildPublishEngine.BuildTargetArguments(Job(new DeploymentProject { Name = "Api", ProjectPath = sdk }));

        Assert.Equal(sdk, args[0]);
        Assert.Contains("/t:Publish", args);
    }

    [Fact]
    public async Task Solution_target_names_include_folders_and_cleanse_dots()
    {
        var slnx = Path.Combine(_dir, "App.slnx");
        Directory.CreateDirectory(Path.Combine(_dir, "Web"));
        File.WriteAllText(Path.Combine(_dir, "Web", "My.Web.App.csproj"), """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        File.WriteAllText(slnx,
            """
            <Solution>
              <Folder Name="/Front End/">
                <Project Path="Web/My.Web.App.csproj" />
              </Folder>
            </Solution>
            """);

        var solution = await new SolutionParser(new ProfileDiscovery()).ParseAsync(slnx);

        var project = Assert.Single(solution.Projects);
        Assert.Equal(@"Front End\My_Web_App", project.SolutionTargetName);
        Assert.Equal(slnx, project.SolutionPath);
    }
}
