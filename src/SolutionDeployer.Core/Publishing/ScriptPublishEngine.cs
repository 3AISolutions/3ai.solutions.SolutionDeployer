using System.Reflection;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Publishing;

/// <summary>
/// Runs a user-supplied script (<c>.ps1</c>/<c>.sh</c>/<c>.bash</c>/<c>.cmd</c>/<c>.bat</c>) as the
/// deployment. The interpreter is inferred from the extension; the script receives its configured
/// arguments plus <c>SD_*</c> context environment variables. A non-zero exit code means failure.
/// </summary>
public sealed class ScriptPublishEngine(ProcessRunner processRunner) : IPublishEngine
{
    public PublishEngineKind Kind => PublishEngineKind.Script;

    // Availability is per-script (depends on the file extension), checked in PublishAsync.
    public bool IsAvailable(out string? unavailableReason)
    {
        unavailableReason = null;
        return true;
    }

    public async Task<PublishResult> PublishAsync(
        PublishJob job,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken = default)
    {
        var script = job.Script
            ?? throw new InvalidOperationException("ScriptPublishEngine requires a job with a Script.");

        var projectDir = job.Project.ProjectDirectory;
        var scriptPath = script.ResolveScriptPath(projectDir);

        PublishResult Fail(string message)
        {
            onOutput(OutputLine.Error(message));
            return new PublishResult
            {
                JobId = job.Id,
                DisplayName = job.DisplayName,
                Status = PublishStatus.Failed,
                ErrorMessage = message,
            };
        }

        if (!File.Exists(scriptPath))
            return Fail($"Script not found: {scriptPath}");

        if (!ScriptInterpreters.TryResolve(scriptPath, out var interpreter, out var reason))
            return Fail(reason ?? "No interpreter available for this script.");

        var args = new List<string>(interpreter.LeadingArgs) { scriptPath };
        args.AddRange(CommandLine.Tokenize(script.Arguments));

        var workingDirectory = script.ResolveWorkingDirectory(projectDir);
        var environment = BuildEnvironment(job, script, projectDir);

        var commandLine = $"{interpreter.FileName} {string.Join(' ', interpreter.LeadingArgs)} \"{scriptPath}\" {script.Arguments}".Trim();
        onOutput(OutputLine.Info($"$ {commandLine}"));

        try
        {
            var result = await processRunner
                .RunAsync(interpreter.FileName, args, workingDirectory, onOutput, environment, cancellationToken)
                .ConfigureAwait(false);

            return new PublishResult
            {
                JobId = job.Id,
                DisplayName = job.DisplayName,
                Status = result.ExitCode == 0 ? PublishStatus.Succeeded : PublishStatus.Failed,
                ExitCode = result.ExitCode,
                Duration = result.Duration,
                CommandLine = commandLine,
                ErrorMessage = result.ExitCode == 0 ? null : $"Script exited with code {result.ExitCode}.",
            };
        }
        catch (OperationCanceledException)
        {
            return new PublishResult
            {
                JobId = job.Id,
                DisplayName = job.DisplayName,
                Status = PublishStatus.Cancelled,
                CommandLine = commandLine,
                ErrorMessage = "Cancelled.",
            };
        }
    }

    private static Dictionary<string, string> BuildEnvironment(PublishJob job, ScriptTarget script, string projectDir)
    {
        // Start with the user's own variables, then layer the authoritative SD_* context on top.
        var env = new Dictionary<string, string>(script.Environment)
        {
            ["SD_PROJECT_PATH"] = job.Project.ProjectPath,
            ["SD_PROJECT_DIR"] = projectDir,
            ["SD_PROJECT_NAME"] = job.Project.Name,
            ["SD_CONFIGURATION"] = job.Configuration,
            ["SD_DEPLOYER_VERSION"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
        };
        return env;
    }
}
