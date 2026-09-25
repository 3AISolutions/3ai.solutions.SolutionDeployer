using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Projects;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Tests;

public sealed class BuildOverlapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-overlap-" + Guid.NewGuid().ToString("N"));

    public BuildOverlapTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteProject(string name, params string[] references)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".vbproj");
        var items = string.Concat(references.Select(r => $"""<ProjectReference Include="..\{r}\{r}.vbproj" />"""));
        File.WriteAllText(path, $"""<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><ItemGroup>{items}</ItemGroup></Project>""");
        return path;
    }

    private static PublishJob Job(string projectPath, string profile) => new()
    {
        Project = new DeploymentProject { Name = Path.GetFileNameWithoutExtension(projectPath), ProjectPath = projectPath },
        Profile = new PublishProfile { Name = profile, FilePath = profile + ".pubxml", Format = PublishProfileFormat.PubXml },
        Engine = PublishEngineKind.MsBuild,
    };

    [Fact]
    public void Build_closure_follows_references_transitively()
    {
        WriteProject("Core");
        WriteProject("Common", "Core");
        var admin = WriteProject("Admin", "Common");

        var closure = ProjectGraph.BuildClosure(admin);

        Assert.Equal(3, closure.Count);
        Assert.Contains(closure, p => p.EndsWith("Core.vbproj"));
    }

    [Fact]
    public void Jobs_sharing_a_referenced_project_are_grouped_and_others_stay_separate()
    {
        WriteProject("Common");
        var admin = WriteProject("Admin", "Common");
        var b2b = WriteProject("B2B", "Common");
        var tool = WriteProject("Tool");

        var groups = DeploymentRunner.GroupByBuildOverlap(
            [Job(admin, "Admin"), Job(tool, "Tool"), Job(b2b, "ATP"), Job(admin, "Reservations")]);

        Assert.Equal(2, groups.Count);
        Assert.Equal(["Admin", "ATP", "Reservations"], groups[0].Select(j => j.Profile!.Name));
        Assert.Equal(["Tool"], groups[1].Select(j => j.Profile!.Name));
    }

    [Fact]
    public async Task Locators_give_every_parallel_caller_the_same_answer()
    {
        var msbuild = new MsBuildLocator();
        var msdeploy = new MsDeployLocator();

        // Before the fix the first caller flagged "resolved" before the lookup finished, so the others got null.
        var answers = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => (msbuild.Locate(), msdeploy.Locate()))));

        Assert.Single(answers.Distinct());
    }
}
