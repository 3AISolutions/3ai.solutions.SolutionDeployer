using System.Runtime.InteropServices;
using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Tests;

public sealed class CredentialTesterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"sd_credtest_{Guid.NewGuid():N}");

    public CredentialTesterTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static PublishProfile MsDeployProfile(string serverUrl = "https://web01:8172/msdeploy.axd", string site = "Default Web Site/app") => new()
    {
        Name = "Prod",
        FilePath = @"C:\repo\Web\Properties\PublishProfiles\Prod.pubxml",
        Format = PublishProfileFormat.PubXml,
        WebPublishMethod = "MSDeploy",
        ServerUrl = serverUrl,
        SiteName = site,
        RequiresCredentials = true,
    };

    private static readonly PublishCredentials Credentials = new() { UserName = "deploy", Password = "s3cr,et pw" };

    // ---- Web Deploy -------------------------------------------------------------

    [Fact]
    public void Msdeploy_test_is_a_read_only_dump_of_the_site_with_the_password_masked_in_the_log()
    {
        var (args, redacted) = CredentialTester.BuildMsDeployArguments(MsDeployProfile(), Credentials, allowUntrustedCertificate: true);

        Assert.StartsWith("-verb:dump -source:contentPath=\"Default Web Site/app\",", args);
        Assert.Contains("computerName=\"https://web01:8172/msdeploy.axd?site=Default Web Site\"", args);
        Assert.Contains("authType=Basic,userName=deploy,password=\"s3cr,et pw\"", args);
        Assert.EndsWith(" -allowUntrusted", args);
        Assert.DoesNotContain("-verb:sync", args);
        Assert.DoesNotContain("-dest:", args);

        Assert.DoesNotContain("s3cr", redacted);
        Assert.Contains("userName=deploy,password=***", redacted);
        Assert.Equal(args.Replace("\"s3cr,et pw\"", "***"), redacted);
    }

    [Fact]
    public void Msdeploy_test_without_a_username_uses_windows_authentication()
    {
        var (args, redacted) = CredentialTester.BuildMsDeployArguments(MsDeployProfile(), PublishCredentials.None, allowUntrustedCertificate: false);

        Assert.Contains("authType=NTLM", args);
        Assert.DoesNotContain("userName=", args);
        Assert.DoesNotContain("password=", args);
        Assert.DoesNotContain("-allowUntrusted", args);
        Assert.Equal(args, redacted);
    }

    [Theory]
    [InlineData("web01", "Site", "https://web01:8172/msdeploy.axd?site=Site")]
    [InlineData("myapp.scm.azurewebsites.net:443", "myapp", "https://myapp.scm.azurewebsites.net:443/msdeploy.axd?site=myapp")]
    [InlineData("https://web01:8172", "Site/app", "https://web01:8172/msdeploy.axd?site=Site")]
    [InlineData("https://web01:8172/msdeploy.axd?site=Other", "Site", "https://web01:8172/msdeploy.axd?site=Other")]
    [InlineData("https://web01:8172/msdeploy.axd", @"D:\Apps\Service", "https://web01:8172/msdeploy.axd")]
    public void Endpoints_are_completed_like_msbuild_does(string serverUrl, string site, string expected)
    {
        Assert.Equal(expected, CredentialTester.MsDeployEndpoint(serverUrl, site));
    }

    [Fact]
    public void Only_web_deploy_profiles_with_a_server_and_site_can_be_tested()
    {
        Assert.True(CredentialTester.CanTest(MsDeployProfile(), out _));

        var fileSystem = new PublishProfile
        {
            Name = "Folder", FilePath = "f.pubxml", Format = PublishProfileFormat.PubXml, WebPublishMethod = "FileSystem",
        };
        Assert.False(CredentialTester.CanTest(fileSystem, out var reason));
        Assert.Contains("FileSystem", reason);

        Assert.False(CredentialTester.CanTest(MsDeployProfile(site: ""), out reason));
        Assert.Contains("server URL or site", reason);
    }

    [Fact]
    public async Task Testing_a_profile_that_cannot_be_tested_explains_why_without_running_anything()
    {
        var tester = new CredentialTester(new ProcessRunner(), new MsDeployLocator());
        var lines = new List<OutputLine>();

        var result = await tester.TestProfileAsync(MsDeployProfile(site: " "), Credentials, true, lines.Add);

        Assert.False(result.Success);
        Assert.Contains("server URL or site", result.Message);
        Assert.Empty(lines);
    }

    [Fact]
    public void Known_msdeploy_errors_are_explained()
    {
        Assert.Equal("The server rejected the username or password.", CredentialTester.DescribeMsDeployFailure(
            ["Error Code: ERROR_USER_UNAUTHORIZED", "More Information: Connected to the remote computer..."], -1));
        Assert.Equal("Error: something odd happened", CredentialTester.DescribeMsDeployFailure(
            ["  Error: something odd happened  "], 1));
        Assert.Equal("msdeploy exited with code 7.", CredentialTester.DescribeMsDeployFailure([], 7));
    }

    [Fact]
    public void Passwords_are_redacted_from_output()
    {
        Assert.Equal("user=deploy pass=*** again ***", CredentialTester.Redact("user=deploy pass=hunter2 again hunter2", "hunter2"));
        Assert.Equal("nothing to hide", CredentialTester.Redact("nothing to hide", null));
        Assert.Equal("nothing to hide", CredentialTester.Redact("nothing to hide", ""));
    }

    // ---- Scripts ----------------------------------------------------------------

    [Fact]
    public void Test_commands_run_through_the_platform_shell()
    {
        var (cmd, cmdArgs) = CredentialTester.ShellInvocation("check.cmd \"a b\"", windows: true);
        Assert.EndsWith("cmd.exe", cmd, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/d /s /c \"check.cmd \"a b\"\""], cmdArgs);

        var (sh, shArgs) = CredentialTester.ShellInvocation("./check.sh 'a b'", windows: false);
        Assert.Equal("/bin/sh", sh);
        Assert.Equal(["-c", "./check.sh 'a b'"], shArgs);
    }

    [Fact]
    public void Script_test_environment_has_the_credentials_in_the_scripts_variables()
    {
        var script = new ScriptTarget
        {
            UserNameVariable = "APP_USER",
            PasswordVariable = " ",
            Environment = { ["REGION"] = "eu" },
        };

        var env = CredentialTester.BuildScriptEnvironment(script, _tempDir, Credentials);

        Assert.Equal("deploy", env["APP_USER"]);
        Assert.Equal("s3cr,et pw", env[ScriptTarget.DefaultPasswordVariable]);
        Assert.Equal("eu", env["REGION"]);
        Assert.Equal(_tempDir, env["SD_PROJECT_DIR"]);
        Assert.Equal("1", env["SD_CREDENTIAL_TEST"]);
    }

    private static string Command(string windows, string unix) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? windows : unix;

    private async Task<(CredentialTestResult Result, List<OutputLine> Lines)> RunScriptTestAsync(string command)
    {
        var tester = new CredentialTester(new ProcessRunner(), new MsDeployLocator());
        var script = new ScriptTarget { Name = "Deploy", ScriptPath = "deploy.ps1", TestCommand = command };
        var lines = new List<OutputLine>();
        var result = await tester.TestScriptAsync(script, _tempDir, Credentials, line => { lock (lines) lines.Add(line); });
        return (result, lines);
    }

    [Fact]
    public async Task A_script_test_succeeds_when_its_command_sees_the_credentials_and_exits_zero()
    {
        var (result, _) = await RunScriptTestAsync(Command(
            "if \"%DEPLOY_USERNAME%\"==\"deploy\" (exit 0) else (exit 1)",
            "[ \"$DEPLOY_USERNAME\" = deploy ]"));

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task A_script_test_fails_with_the_commands_exit_code()
    {
        var (result, _) = await RunScriptTestAsync("exit 3");

        Assert.False(result.Success);
        Assert.Contains("exit code 3", result.Message);
    }

    [Fact]
    public async Task A_script_test_never_shows_the_password_even_when_the_command_prints_it()
    {
        var (result, lines) = await RunScriptTestAsync(Command("echo pw=%DEPLOY_PASSWORD%", "echo \"pw=$DEPLOY_PASSWORD\""));

        Assert.True(result.Success, result.Message);
        Assert.Contains(lines, l => l.Text.Contains("pw=***"));
        Assert.DoesNotContain(lines, l => l.Text.Contains("s3cr"));
    }

    [Fact]
    public async Task A_script_without_a_test_command_cannot_be_tested()
    {
        var (result, lines) = await RunScriptTestAsync("  ");

        Assert.False(result.Success);
        Assert.Contains("no test command", result.Message);
        Assert.Empty(lines);
        Assert.False(CredentialTester.CanTest(new ScriptTarget()));
    }
}
