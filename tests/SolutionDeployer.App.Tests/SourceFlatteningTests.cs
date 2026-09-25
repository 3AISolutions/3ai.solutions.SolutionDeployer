using SolutionDeployer.App.ViewModels;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.Tests;

public sealed class SourceFlatteningTests
{
    private static ProjectViewModel Project(string name) =>
        new(new DeploymentProject { Name = name, ProjectPath = Path.Combine(Path.GetTempPath(), $"{name}.csproj") });

    [Fact]
    public void Single_project_is_flattened_into_its_source_header()
    {
        var source = new SourceViewModel(DeploymentSource.Solution(Path.Combine(Path.GetTempPath(), "Shop.slnx")));
        var project = Project("Shop.Web");
        project.IsExpanded = false;

        source.Projects.Add(project);

        Assert.True(source.IsSingleProject);
        Assert.Same(project, source.SingleProject);
        Assert.True(project.IsFlattened);
        Assert.True(project.IsExpanded); // no header left to re-open it
        Assert.Equal("Shop.Web · 0 profiles", source.HeaderDetail);

        project.IsExpanded = false;
        Assert.True(project.IsExpanded);
    }

    [Fact]
    public void Second_project_restores_the_project_headers()
    {
        var source = new SourceViewModel(DeploymentSource.Solution(Path.Combine(Path.GetTempPath(), "Shop.slnx")));
        var first = Project("Shop.Web");
        source.Projects.Add(first);
        source.Projects.Add(Project("Shop.Api"));

        Assert.False(source.IsSingleProject);
        Assert.Null(source.SingleProject);
        Assert.False(first.IsFlattened);
        Assert.Equal("(2)", source.HeaderDetail);
    }

    [Fact]
    public void Project_named_like_its_source_is_not_repeated()
    {
        var source = new SourceViewModel(DeploymentSource.Project(Path.Combine(Path.GetTempPath(), "Shop.Web.csproj")));
        source.Projects.Add(Project(source.Name));

        Assert.Equal("0 profiles", source.HeaderDetail);
    }
}
