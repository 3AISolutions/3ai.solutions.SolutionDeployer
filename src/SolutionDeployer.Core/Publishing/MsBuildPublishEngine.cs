using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Publishing;

/// <summary>
/// Publishes via full <c>msbuild.exe</c> (Windows-only, located through vswhere). Required for
/// classic Web Deploy / full-framework projects. Equivalent to:
/// <code>msbuild &lt;csproj&gt; /restore /t:Publish /p:Configuration=Release /p:PublishProfile=&lt;profile&gt; /p:Password=&lt;pw&gt; /p:UserName=&lt;user&gt; /p:AllowUntrustedCertificate=true /v:minimal /m</code>
/// </summary>
public sealed class MsBuildPublishEngine(ProcessRunner processRunner, MsBuildLocator locator) : IPublishEngine
{
    public PublishEngineKind Kind => PublishEngineKind.MsBuild;

    public bool IsAvailable(out string? unavailableReason)
    {
        if (!locator.IsSupported)
        {
            unavailableReason = "msbuild.exe is only available on Windows. Use the dotnet engine on this platform.";
            return false;
        }

        if (locator.Locate() is null)
        {
            unavailableReason = "Could not locate msbuild.exe. Install Visual Studio or the Build Tools (with the MSBuild component).";
            return false;
        }

        unavailableReason = null;
        return true;
    }

    public async Task<PublishResult> PublishAsync(
        PublishJob job,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken = default)
    {
        var msbuild = locator.Locate();
        if (msbuild is null)
        {
            IsAvailable(out var reason);
            return new PublishResult
            {
                JobId = job.Id,
                DisplayName = job.DisplayName,
                Status = PublishStatus.Failed,
                ErrorMessage = reason ?? "msbuild.exe not found.",
            };
        }

        var (props, redactedProps) = PublishArguments.BuildProperties(job);

        var args = new List<string>
        {
            job.Project.ProjectPath,
            "/restore",
            "/t:Publish",
            $"/p:Configuration={job.Configuration}",
        };
        args.AddRange(props);
        args.Add("/v:minimal");
        args.Add("/m");

        var redacted = new List<string>
        {
            "msbuild",
            job.Project.ProjectPath,
            "/restore",
            "/t:Publish",
            $"/p:Configuration={job.Configuration}",
        };
        redacted.AddRange(redactedProps);
        redacted.Add("/v:minimal");
        redacted.Add("/m");
        var commandLine = string.Join(' ', redacted);

        onOutput(OutputLine.Info($"$ {commandLine}"));

        try
        {
            var result = await processRunner
                .RunAsync(msbuild, args, job.Project.ProjectDirectory, onOutput, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new PublishResult
            {
                JobId = job.Id,
                DisplayName = job.DisplayName,
                Status = result.ExitCode == 0 ? PublishStatus.Succeeded : PublishStatus.Failed,
                ExitCode = result.ExitCode,
                Duration = result.Duration,
                CommandLine = commandLine,
                ErrorMessage = result.ExitCode == 0 ? null : $"msbuild exited with code {result.ExitCode}.",
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
}
