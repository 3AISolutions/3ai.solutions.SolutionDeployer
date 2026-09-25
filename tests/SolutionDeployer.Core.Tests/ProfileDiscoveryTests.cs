using SolutionDeployer.Core.Profiles;

namespace SolutionDeployer.Core.Tests;

public sealed class ProfileDiscoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-profiles-" + Guid.NewGuid().ToString("N"));

    public ProfileDiscoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Discovers_vb_profiles_under_My_Project()
    {
        var projectPath = Path.Combine(_dir, "LegacyWeb.vbproj");
        File.WriteAllText(projectPath, "<Project />");

        var profilesDir = Path.Combine(_dir, "My Project", "PublishProfiles");
        Directory.CreateDirectory(profilesDir);
        File.WriteAllText(Path.Combine(profilesDir, "Production.pubxml"),
            """
            <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <WebPublishMethod>MSDeploy</WebPublishMethod>
              </PropertyGroup>
            </Project>
            """);

        var profile = Assert.Single(new ProfileDiscovery().DiscoverProfiles(projectPath));
        Assert.Equal("Production", profile.Name);
        Assert.Equal("MSDeploy", profile.WebPublishMethod);
    }
}
