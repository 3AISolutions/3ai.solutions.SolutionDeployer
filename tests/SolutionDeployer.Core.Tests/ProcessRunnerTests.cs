using System.Collections.Concurrent;
using System.Diagnostics;
using SolutionDeployer.Core.Models;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.Core.Tests;

public sealed class ProcessRunnerTests
{
    private static (string FileName, string[] Args) Shell(string windows, string unix) =>
        OperatingSystem.IsWindows() ? ("cmd.exe", ["/c", windows]) : ("/bin/sh", ["-c", unix]);

    [Fact]
    public async Task A_process_left_running_does_not_hold_the_run_open()
    {
        // The shell exits at once, but the background ping/sleep it started keeps the output pipes open.
        // The drain window is generous enough for a slow CI runner to deliver "done", and well short of the 30 s
        // the leftover process lives.
        var (fileName, args) = Shell("start /b ping -n 30 127.0.0.1 >nul & echo done", "sleep 30 & echo done");
        var runner = new ProcessRunner(TimeSpan.FromSeconds(4));
        var lines = new ConcurrentQueue<OutputLine>();
        var stopwatch = Stopwatch.StartNew();

        var result = await runner
            .RunAsync(fileName, args, workingDirectory: null, lines.Enqueue)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(lines, l => l.Text.Trim() == "done");
        Assert.Contains(lines, l => l.Text.Contains("still holds its output open"));
    }

    [Fact]
    public async Task Output_written_just_before_exit_is_not_lost()
    {
        var (fileName, args) = Shell("for /l %i in (1,1,200) do @echo line %i", "for i in $(seq 1 200); do echo line $i; done");
        var runner = new ProcessRunner();
        var lines = new ConcurrentQueue<OutputLine>();

        await runner.RunAsync(fileName, args, workingDirectory: null, lines.Enqueue);

        Assert.Contains(lines, l => l.Text.Trim() == "line 200");
        Assert.Equal(200, lines.Count(l => l.Text.StartsWith("line ")));
    }
}
