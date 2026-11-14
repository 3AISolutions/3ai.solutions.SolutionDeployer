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
    /// <summary>How long to keep reading a process's output after it exited.</summary>
    private readonly TimeSpan outputDrainTimeout;

    public ProcessRunner()
        : this(TimeSpan.FromSeconds(10))
    {
    }

    internal ProcessRunner(TimeSpan outputDrainTimeout) => this.outputDrainTimeout = outputDrainTimeout;

    /// <summary>
    /// Runs <paramref name="fileName"/> with the given arguments. Output lines are reported via
    /// <paramref name="onOutput"/> on background threads. Honours cancellation by killing the tree.
    /// </summary>
    public Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        Action<OutputLine> onOutput,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(fileName, psi =>
        {
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);
        }, workingDirectory, onOutput, environment, cancellationToken);

    /// <summary>
    /// Like <see cref="RunAsync"/>, but passes <paramref name="commandLine"/> through verbatim — for tools
    /// such as msdeploy that parse their own command line and reject standard per-argument quoting.
    /// </summary>
    public Task<ProcessRunResult> RunRawAsync(
        string fileName,
        string commandLine,
        string? workingDirectory,
        Action<OutputLine> onOutput,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(fileName, psi => psi.Arguments = commandLine, workingDirectory, onOutput, environment, cancellationToken);

    private async Task<ProcessRunResult> RunCoreAsync(
        string fileName,
        Action<ProcessStartInfo> applyArguments,
        string? workingDirectory,
        Action<OutputLine> onOutput,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            // An empty stdin, closed right after start (see below). Otherwise the child — and everything it
            // starts, e.g. ssh inside a deploy script — inherits whatever stdin this app has, which never
            // reaches end-of-file: Windows OpenSSH then intermittently never exits after its remote command.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        applyArguments(psi);

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.Exited += (_, _) => exited.TrySetResult();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                outputDone.TrySetResult();
            else
                onOutput(OutputLine.Info(e.Data));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                errorDone.TrySetResult();
            else
                onOutput(OutputLine.Error(e.Data));
        };

        var stopwatch = Stopwatch.StartNew();

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Already gone (exited at once); nothing to close.
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        // The rest of its output normally follows at once. But a process it left running (an orphaned ssh, a
        // build server) still holds the pipes and would keep them open — and this run — indefinitely.
        var drained = Task.WhenAll(outputDone.Task, errorDone.Task);
        if (await Task.WhenAny(drained, Task.Delay(outputDrainTimeout, cancellationToken)).ConfigureAwait(false) != drained)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onOutput(OutputLine.Error(
                $"'{Path.GetFileName(fileName)}' exited, but a process it started still holds its output open; not waiting for it."));
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
