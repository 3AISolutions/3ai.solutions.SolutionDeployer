namespace SolutionDeployer.Mcp.Tests;

public sealed class DeploymentRunTests
{
    [Fact]
    public void Keeps_only_the_latest_output_and_counts_what_it_dropped()
    {
        var run = new DeploymentRun("deploy", [("a", "A")]);
        for (var i = 0; i < DeploymentRun.MaxLogLines + 5; i++)
            run.AppendLog($"line {i}");

        var snapshot = run.Snapshot(logLines: 2);

        Assert.Equal([$"line {DeploymentRun.MaxLogLines + 3}", $"line {DeploymentRun.MaxLogLines + 4}"], snapshot.Log);
        Assert.Equal(DeploymentRun.MaxLogLines + 3, snapshot.EarlierLogLinesOmitted);
    }

    [Fact]
    public async Task Wait_returns_when_the_run_completes()
    {
        var run = new DeploymentRun("deploy", [("a", "A")]);
        var wait = run.WaitAsync(TimeSpan.FromSeconds(30));

        run.Complete(RunState.Failed, "1 failed");
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RunState.Failed, run.State);
        Assert.Equal("failed", run.Snapshot(0).State);
        run.Complete(RunState.Succeeded, "ignored"); // only the first outcome counts
        Assert.Equal("1 failed", run.Message);
    }

    [Fact]
    public void Cancel_reaches_the_host_only_while_running()
    {
        var run = new DeploymentRun("deploy", [("a", "A")]);
        var cancelled = 0;
        run.OnCancel(() => cancelled++);

        Assert.True(run.RequestCancel());
        run.Complete(RunState.Cancelled, "Deployment cancelled.");
        Assert.False(run.RequestCancel());
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void Registry_finds_runs_by_id_or_the_newest()
    {
        var registry = new RunRegistry();
        var first = new DeploymentRun("deploy", []);
        var second = new DeploymentRun("restore", []);
        registry.Add(first);
        registry.Add(second);

        Assert.Same(second, registry.Find(null));
        Assert.Same(first, registry.Find(first.Id.ToUpperInvariant()));
        Assert.Null(registry.Find("nope"));
    }

    [Fact]
    public void Profile_ids_ignore_how_the_path_is_written()
    {
        var path = Path.Combine(Path.GetTempPath(), "Web", "Properties", "PublishProfiles", "Prod.pubxml");
        var dotted = Path.Combine(Path.GetTempPath(), "Web", "Properties", "..", "Properties", "PublishProfiles", "Prod.pubxml");

        Assert.Equal(TargetIds.ForProfile(path), TargetIds.ForProfile(dotted));
        Assert.NotEqual(TargetIds.ForProfile(path), TargetIds.ForProfile(path.Replace("Prod", "Staging")));
    }

    [Fact]
    public void Connection_snippets_carry_the_url_and_token()
    {
        const string url = "http://localhost:47821/mcp";

        Assert.Equal(
            "claude mcp add --transport http --scope user solution-deployer http://localhost:47821/mcp --header \"Authorization: Bearer abc\"",
            McpConnectionInfo.ClaudeCodeCommand(url, "abc"));

        var desktop = System.Text.Json.JsonDocument.Parse(McpConnectionInfo.ClaudeDesktopConfig(url, "abc")).RootElement
            .GetProperty("mcpServers").GetProperty("solution-deployer");
        Assert.Equal("npx", desktop.GetProperty("command").GetString());
        Assert.Contains(url, desktop.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("Bearer abc", desktop.GetProperty("env").GetProperty("SD_AUTH_HEADER").GetString());
    }
}
