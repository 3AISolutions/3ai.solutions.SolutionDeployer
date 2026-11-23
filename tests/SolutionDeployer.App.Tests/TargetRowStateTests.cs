using SolutionDeployer.App.ViewModels;
using SolutionDeployer.Core.Configuration;
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
            RequiresCredentials = true,
        };
        return new ProfileViewModel(project, profile, PublishEngineKind.Dotnet, "deploy", rememberedPassword,
            credentialStoreAvailable: true);
    }

    [Fact]
    public void Saved_password_makes_the_row_ready()
    {
        var profile = Profile(rememberedPassword: "secret");

        Assert.True(profile.CredentialsReady);
        Assert.False(profile.NeedsPassword);
        Assert.Equal("deploy (password saved)", profile.CredentialsSummary);
    }

    [Fact]
    public void Missing_password_asks_for_one()
    {
        var profile = Profile(rememberedPassword: null);

        Assert.False(profile.CredentialsReady);
        Assert.True(profile.NeedsPassword);
        Assert.Equal("deploy (no password)", profile.CredentialsSummary);
    }

    [Fact]
    public void Library_credentials_are_shown_by_entry_name()
    {
        var profile = Profile(rememberedPassword: null);
        var entry = new SavedCredential { Name = "Prod deploy", UserName = "svc-deploy" };

        profile.ApplyCredentials(new ResolvedCredentials(CredentialSource.Library, entry, "svc-deploy", "pw"), rememberPassword: true);

        Assert.Same(entry, profile.CredentialEntry);
        Assert.True(profile.CredentialsReady);
        Assert.Equal("Prod deploy (svc-deploy)", profile.CredentialsSummary);
        Assert.Equal("svc-deploy", profile.BuildCredentials().UserName);
        Assert.Equal("pw", profile.BuildCredentials().Password);
    }

    [Fact]
    public void A_password_typed_for_the_session_is_not_called_saved()
    {
        var profile = Profile(rememberedPassword: null);

        profile.ApplyCredentials(new ResolvedCredentials(CredentialSource.Own, null, "deploy", "typed"), rememberPassword: false);

        Assert.True(profile.CredentialsReady);
        Assert.Equal("deploy (password for this session)", profile.CredentialsSummary);
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
