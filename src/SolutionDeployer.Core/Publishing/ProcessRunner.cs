using System.Diagnostics;
using System.Text;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.Core.Publishing;

public sealed record ProcessRunResult(int ExitCode, TimeSpan Duration);

/// <summary>
/// Launches an external process, streaming stdout/stderr line-by-line to a callback as they arrive.
/// </summary>
public sealed class ProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with the given arguments. Output lines are reported via
    /// <paramref name="onOutput"/> on background threads. Honours cancellation by killing the tree.
    /// </summary>
    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        Action<OutputLine> onOutput,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                onOutput(OutputLine.Info(e.Data));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                onOutput(OutputLine.Error(e.Data));
        };

        var stopwatch = Stopwatch.StartNew();

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        stopwatch.Stop();
        return new ProcessRunResult(process.ExitCode, stopwatch.Elapsed);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort.
        }
    }
}
