using System.Diagnostics;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Publishing;

/// <summary>
/// Publishes via <c>dotnet publish</c>. Cross-platform; requires the .NET SDK on PATH.
/// Equivalent to:
/// <code>dotnet publish &lt;csproj&gt; -c Release /p:PublishProfile=&lt;profile&gt; /p:Password=&lt;pw&gt; /p:UserName=&lt;user&gt; /p:AllowUntrustedCertificate=true</code>
/// </summary>
public sealed class DotnetPublishEngine(ProcessRunner processRunner) : IPublishEngine
{
    public PublishEngineKind Kind => PublishEngineKind.Dotnet;

    public bool IsAvailable(out string? unavailableReason)
    {
        // The SDK is effectively always present (this app runs on it); resolve the muxer for safety.
        unavailableReason = null;
        return true;
    }

    public async Task<PublishResult> PublishAsync(
        PublishJob job,
        Action<OutputLine> onOutput,
        CancellationToken cancellationToken = default)
    {
        var (props, redactedProps) = PublishArguments.BuildProperties(job);

        var args = new List<string>
        {
            "publish",
            job.Project.ProjectPath,
            "--configuration",
            job.Configuration,
        };
        args.AddRange(props);

        var redacted = new List<string> { "dotnet", "publish", job.Project.ProjectPath, "--configuration", job.Configuration };
        redacted.AddRange(redactedProps);
        var commandLine = string.Join(' ', redacted);

        onOutput(OutputLine.Info($"$ {commandLine}"));

        var muxer = DotnetMuxer.Path;
        try
        {
            var result = await processRunner
                .RunAsync(muxer, args, job.Project.ProjectDirectory, onOutput, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new PublishResult
            {
                JobId = job.Id,
                DisplayName = job.DisplayName,
                Status = result.ExitCode == 0 ? PublishStatus.Succeeded : PublishStatus.Failed,
                ExitCode = result.ExitCode,
                Duration = result.Duration,
                CommandLine = commandLine,
                ErrorMessage = result.ExitCode == 0 ? null : $"dotnet publish exited with code {result.ExitCode}.",
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

/// <summary>Resolves the path to the running <c>dotnet</c> host.</summary>
internal static class DotnetMuxer
{
    public static string Path { get; } = ResolvePath();

    private static string ResolvePath()
    {
        var main = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(main) &&
            System.IO.Path.GetFileNameWithoutExtension(main).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return main;
        }

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var candidate = System.IO.Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
                return candidate;
        }

        // Fall back to PATH resolution.
        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }
}
