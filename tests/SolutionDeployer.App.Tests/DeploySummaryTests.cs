using SolutionDeployer.App.ViewModels;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.Tests;

public sealed class DeploySummaryTests
{
    private static PublishJob ProfileJob(DeploymentProject project, string name) => new()
    {
        Project = project,
        Engine = PublishEngineKind.MsBuild,
        Profile = new PublishProfile { Name = name, FilePath = $"{name}.pubxml", Format = PublishProfileFormat.PubXml },
    };

    private static PublishResult Result(PublishJob job, PublishStatus status, string? error = null) => new()
    {
        JobId = job.Id,
        DisplayName = job.DisplayName,
        Status = status,
        ErrorMessage = error,
        Duration = TimeSpan.FromSeconds(2),
    };

    [Fact]
    public void Groups_results_by_project_and_flags_failures()
    {
        var admin = new DeploymentProject { Name = "Admin", ProjectPath = "Admin.vbproj" };
        var jobManager = new DeploymentProject { Name = "JobManager", ProjectPath = "JobManager.vbproj" };

        var adminProd = ProfileJob(admin, "Prod");
        var adminStaging = ProfileJob(admin, "Staging");
        var script = new PublishJob
        {
            Project = jobManager,
            Engine = PublishEngineKind.Script,
            Script = new ScriptTarget { Name = "deploy" },
        };

        var summary = DeploySummaryViewModel.From(
            [adminProd, adminStaging, script],
            [
                Result(script, PublishStatus.Succeeded),
                Result(adminProd, PublishStatus.Succeeded),
                Result(adminStaging, PublishStatus.Failed, "Build failed."),
            ]);

        Assert.Equal(["Admin", "JobManager"], summary.Projects.Select(p => p.Name));

        var adminSummary = summary.Projects[0];
        Assert.False(adminSummary.IsSuccess);
        Assert.True(adminSummary.Targets[0].IsSuccess);
        Assert.Equal("Build failed.", adminSummary.Targets[1].Detail);

        Assert.True(summary.Projects[1].IsSuccess);
        Assert.Equal("2 of 3 target(s) succeeded", summary.Heading);
    }

    [Fact]
    public void Job_without_a_result_is_reported_as_failed()
    {
        var project = new DeploymentProject { Name = "App", ProjectPath = "App.csproj" };
        var job = ProfileJob(project, "Prod");

        var summary = DeploySummaryViewModel.From([job], []);

        var target = Assert.Single(Assert.Single(summary.Projects).Targets);
        Assert.False(target.IsSuccess);
        Assert.Equal("Not run", target.Detail);
    }
}
