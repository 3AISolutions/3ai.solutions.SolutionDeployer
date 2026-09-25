using SolutionDeployer.App.ViewModels;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.Tests;

public sealed class TargetRowStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-row-" + Guid.NewGuid().ToString("N"));

    public TargetRowStateTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ProfileViewModel Profile(string? rememberedPassword)
    {
        var path = Path.Combine(_dir, "App.csproj");
        File.WriteAllText(path, """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        var project = new ProjectViewModel(new DeploymentProject { Name = "App", ProjectPath = path });
        var profile = new PublishProfile
        {
            Name = "Prod",
            FilePath = Path.Combine(_dir, "Prod.pubxml"),
            Format = PublishProfileFormat.PubXml,
            WebPublishMethod = "MSDeploy",
        };
        return new ProfileViewModel(project, profile, PublishEngineKind.Dotnet, "deploy", rememberedPassword,
            credentialStoreAvailable: true);
    }

    [Fact]
    public void Saved_password_collapses_the_credential_fields()
    {
        var profile = Profile(rememberedPassword: "secret");

        Assert.False(profile.ShowCredentials);
        Assert.Equal("deploy (password saved)", profile.CredentialsSummary);

        profile.ToggleCredentialsCommand.Execute(null);
        Assert.True(profile.ShowCredentials);
    }

    [Fact]
    public void Missing_password_shows_the_credential_fields()
    {
        Assert.True(Profile(rememberedPassword: null).ShowCredentials);
    }

    [Fact]
    public void Engine_can_be_switched_from_the_menu_and_is_reflected_in_the_summary()
    {
        var profile = Profile(rememberedPassword: null);
        Assert.Equal("MSDeploy · dotnet", profile.MethodSummary);

        profile.SetEngineCommand.Execute(PublishEngineKind.MsBuild);

        Assert.Equal(PublishEngineKind.MsBuild, profile.Engine);
        Assert.True(profile.IsMsBuildEngine);
        Assert.Equal("MSDeploy · MSBuild", profile.MethodSummary);
    }

    [Fact]
    public void Status_text_is_empty_for_idle_rows_and_describes_queued_and_finished_ones()
    {
        var profile = Profile(rememberedPassword: null);
        Assert.Equal(string.Empty, profile.StatusText);

        profile.IsQueued = true;
        Assert.Equal("Waiting to deploy", profile.StatusText);

        profile.Status = PublishStatus.Running;
        Assert.Equal("Deploying…", profile.StatusText);

        profile.Status = PublishStatus.Failed;
        Assert.Equal("Deploy failed", profile.StatusText);
    }
}
