using System.Runtime.InteropServices;
using SolutionDeployer.Core.Backup;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Publishing;

/// <summary>The outcome of a credential test.</summary>
public sealed record CredentialTestResult(bool Success, string Message);

/// <summary>
/// Checks a target's credentials without deploying. A Web Deploy (MSDeploy) profile is tested with a read-only
/// <c>msdeploy -verb:dump</c> of its site's content; a script with its own <see cref="ScriptTarget.TestCommand"/>,
/// run with the script's environment and credential variables and judged by its exit code. The password never
/// appears in what is logged.
/// </summary>
public sealed class CredentialTester(ProcessRunner processRunner, MsDeployLocator msDeployLocator)
{
    /// <summary>How long a test may take before it is stopped.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How many lines of a successful dump are shown before the rest are only counted.</summary>
    private const int ShownDumpLines = 10;

    /// <summary>Whether <paramref name="profile"/> can be tested, and why not.</summary>
    public static bool CanTest(PublishProfile profile, out string? reason)
    {
        if (!string.Equals(profile.WebPublishMethod, "MSDeploy", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Only Web Deploy (MSDeploy) profiles can be tested; this one is '{profile.WebPublishMethod ?? "unknown"}'.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(profile.ServerUrl) || string.IsNullOrWhiteSpace(profile.SiteName))
        {
            reason = "The profile has no server URL or site name to test against.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>Whether <paramref name="script"/> has a test command.</summary>
    public static bool CanTest(ScriptTarget script) => !string.IsNullOrWhiteSpace(script.TestCommand);

    /// <summary>Lists the profile's site content over Web Deploy with <paramref name="credentials"/>; nothing is changed.</summary>
    public async Task<CredentialTestResult> TestProfileAsync(
        PublishProfile profile,
        PublishCredentials credentials,
        bool allowUntrustedCertificate,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken = default)
    {
        if (!CanTest(profile, out var reason))
            return new CredentialTestResult(false, reason!);
        if (!msDeployLocator.IsSupported)
            return new CredentialTestResult(false, "Testing Web Deploy credentials needs msdeploy.exe, which is only available on Windows.");
        if (msDeployLocator.Locate() is not { } msdeploy)
            return new CredentialTestResult(false, "Could not locate msdeploy.exe. Install Web Deploy (the IIS \"Microsoft Web Deploy\" component).");

        var (arguments, redacted) = BuildMsDeployArguments(profile, credentials, allowUntrustedCertificate);
        onOutput(OutputLine.Info($"$ \"{msdeploy}\" {redacted}"));

        var errors = new List<string>();
        var entries = 0;
        void Collect(OutputLine line)
        {
            line = line with { Text = Redact(line.Text, credentials.Password) };
            if (line.Severity == OutputSeverity.Error)
            {
                errors.Add(line.Text);
                onOutput(line);
            }
            else if (++entries <= ShownDumpLines)
            {
                onOutput(line);
            }
        }

        var (exit, timedOut) = await RunWithTimeoutAsync(
            ct => processRunner.RunRawAsync(msdeploy, arguments, workingDirectory: null, Collect, cancellationToken: ct),
            cancellationToken).ConfigureAwait(false);

        if (timedOut)
            return new CredentialTestResult(false, $"No answer from the server within {DefaultTimeout.TotalSeconds:0} seconds.");
        if (entries > ShownDumpLines)
            onOutput(OutputLine.Info($"… and {entries - ShownDumpLines} more entries."));

        return exit == 0
            ? new CredentialTestResult(true, $"Signed in to {profile.SiteName} and read its content ({entries} entries).")
            : new CredentialTestResult(false, DescribeMsDeployFailure(errors, exit));
    }

    /// <summary>Runs the script's <see cref="ScriptTarget.TestCommand"/> with <paramref name="credentials"/> in its variables.</summary>
    public async Task<CredentialTestResult> TestScriptAsync(
        ScriptTarget script,
        string projectDirectory,
        PublishCredentials credentials,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken = default)
    {
        if (!CanTest(script))
            return new CredentialTestResult(false, "This script has no test command. Add one in the script's settings.");

        var command = script.TestCommand!.Trim();
        var environment = BuildScriptEnvironment(script, projectDirectory, credentials);
        var workingDirectory = script.ResolveWorkingDirectory(projectDirectory);
        if (!Directory.Exists(workingDirectory))
            return new CredentialTestResult(false, $"Working directory not found: {workingDirectory}");

        onOutput(OutputLine.Info($"$ {Redact(command, credentials.Password)}"));
        void Forward(OutputLine line) => onOutput(line with { Text = Redact(line.Text, credentials.Password) });

        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var (shell, shellArguments) = ShellInvocation(command, windows);
        var (exit, timedOut) = await RunWithTimeoutAsync(ct => windows
                // cmd parses its own command line, so the command goes through verbatim.
                ? processRunner.RunRawAsync(shell, shellArguments[0], workingDirectory, Forward, environment, ct)
                : processRunner.RunAsync(shell, shellArguments, workingDirectory, Forward, environment, ct),
            cancellationToken).ConfigureAwait(false);

        if (timedOut)
            return new CredentialTestResult(false, $"The test command didn't finish within {DefaultTimeout.TotalSeconds:0} seconds.");
        return exit == 0
            ? new CredentialTestResult(true, "The test command succeeded.")
            : new CredentialTestResult(false, $"The test command failed (exit code {exit}).");
    }

    /// <summary>
    /// The msdeploy arguments for a read-only dump of the profile's site, and a copy with the password masked for
    /// logging. Values are quoted inside each argument, as msdeploy expects (see <see cref="ProcessRunner.RunRawAsync"/>).
    /// </summary>
    internal static (string Arguments, string Redacted) BuildMsDeployArguments(
        PublishProfile profile, PublishCredentials credentials, bool allowUntrustedCertificate)
    {
        var site = profile.SiteName!.Trim();
        var provider = new List<string>
        {
            $"contentPath={Q(site)}",
            $"computerName={Q(MsDeployEndpoint(profile.ServerUrl!.Trim(), site))}",
        };

        var hasUser = !string.IsNullOrWhiteSpace(credentials.UserName);
        provider.Add($"authType={(hasUser ? "Basic" : "NTLM")}");
        if (hasUser)
            provider.Add($"userName={Q(credentials.UserName!.Trim())}");

        var real = new List<string>(provider);
        var safe = new List<string>(provider);
        if (!string.IsNullOrEmpty(credentials.Password))
        {
            real.Add($"password={Q(credentials.Password)}");
            safe.Add("password=***");
        }

        var tail = allowUntrustedCertificate ? " -allowUntrusted" : string.Empty;
        return ($"-verb:dump -source:{string.Join(',', real)}{tail}", $"-verb:dump -source:{string.Join(',', safe)}{tail}");
    }

    /// <summary>
    /// The Web Deploy endpoint for a profile's <c>MSDeployServiceURL</c>: a bare host (as Azure and many IIS profiles
    /// give it) becomes <c>https://host:8172/msdeploy.axd</c> like MSBuild does, and the site is added as
    /// <c>?site=</c> for WMSvc endpoints.
    /// </summary>
    internal static string MsDeployEndpoint(string serverUrl, string site)
    {
        var url = serverUrl;
        if (!url.Contains("://", StringComparison.Ordinal))
        {
            var slash = url.IndexOf('/');
            var host = slash < 0 ? url : url[..slash];
            var path = slash < 0 ? string.Empty : url[slash..];
            if (!host.Contains(':'))
                host += ":8172";
            url = $"https://{host}{(path.Length <= 1 ? "/msdeploy.axd" : path)}";
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath is "" or "/" && uri.Query.Length == 0)
        {
            url = url.TrimEnd('/') + "/msdeploy.axd";
        }

        return BackupService.NormalizeComputerName(url, site);
    }

    /// <summary>The shell that runs a test command: <c>cmd /d /s /c "…"</c> (one raw argument) or <c>/bin/sh -c …</c>.</summary>
    internal static (string FileName, IReadOnlyList<string> Arguments) ShellInvocation(string command, bool windows) =>
        windows
            ? (Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe",
                [$"/d /s /c \"{command}\""])
            : ("/bin/sh", ["-c", command]);

    /// <summary>The script's own variables, the project directory, and the credentials in the script's variables.</summary>
    internal static Dictionary<string, string> BuildScriptEnvironment(
        ScriptTarget script, string projectDirectory, PublishCredentials credentials)
    {
        var env = new Dictionary<string, string>(script.Environment)
        {
            ["SD_PROJECT_DIR"] = projectDirectory,
            ["SD_CREDENTIAL_TEST"] = "1",
        };
        if (!string.IsNullOrEmpty(credentials.UserName))
            env[VariableOrDefault(script.UserNameVariable, ScriptTarget.DefaultUserNameVariable)] = credentials.UserName;
        if (!string.IsNullOrEmpty(credentials.Password))
            env[VariableOrDefault(script.PasswordVariable, ScriptTarget.DefaultPasswordVariable)] = credentials.Password;
        return env;
    }

    /// <summary>Masks every occurrence of <paramref name="password"/> in <paramref name="text"/>.</summary>
    public static string Redact(string text, string? password) =>
        string.IsNullOrEmpty(password) || string.IsNullOrEmpty(text)
            ? text
            : text.Replace(password, "***", StringComparison.Ordinal);

    /// <summary>A short explanation of a failed msdeploy call, from its error code when it gave a known one.</summary>
    internal static string DescribeMsDeployFailure(IReadOnlyList<string> errors, int exitCode)
    {
        var all = string.Join('\n', errors);
        string? Known(string code, string message) =>
            all.Contains(code, StringComparison.OrdinalIgnoreCase) ? message : null;

        return Known("ERROR_USER_UNAUTHORIZED", "The server rejected the username or password.")
               ?? Known("ERROR_USER_NOT_ADMIN", "The user is not allowed to use Web Deploy on this server.")
               ?? Known("ERROR_INSUFFICIENT_ACCESS_TO_SITE_FOLDER", "Signed in, but the user can't read the site's folder.")
               ?? Known("ERROR_SITE_DOES_NOT_EXIST", "Signed in, but the site doesn't exist on the server.")
               ?? Known("ERROR_CERTIFICATE_VALIDATION_FAILED", "The server's certificate isn't trusted.")
               ?? Known("ERROR_DESTINATION_NOT_REACHABLE", "The server can't be reached (check the URL, port 8172 and firewalls).")
               ?? Known("ERROR_COULD_NOT_CONNECT_TO_REMOTESVC", "The server can't be reached (check the URL and that the Web Management Service runs).")
               ?? errors.FirstOrDefault(e => e.Contains("Error", StringComparison.OrdinalIgnoreCase))?.Trim()
               ?? $"msdeploy exited with code {exitCode}.";
    }

    private static async Task<(int ExitCode, bool TimedOut)> RunWithTimeoutAsync(
        Func<CancellationToken, Task<ProcessRunResult>> run, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);
        try
        {
            var result = await run(timeout.Token).ConfigureAwait(false);
            return (result.ExitCode, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (-1, true);
        }
    }

    private static string Q(string value) =>
        value.IndexOfAny([' ', ',']) >= 0 ? $"\"{value}\"" : value;

    private static string VariableOrDefault(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
}
