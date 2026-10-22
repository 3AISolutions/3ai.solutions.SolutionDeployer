using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SolutionDeployer.Mcp.Tests;

/// <summary>The real server on a free localhost port, driven by the SDK's MCP client.</summary>
public sealed class McpServerTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private readonly FakeDeployerHost _host = new();
    private readonly RunRegistry _runs = new();
    private McpServerHost _server = null!;

    public async Task InitializeAsync()
    {
        _server = new McpServerHost(_host, _runs);
        await _server.StartAsync(FreePort(), () => Token, "1.0.0");
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private Task<McpClient> ConnectAsync(string token = Token) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_server.Url),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }));

    [Fact]
    public async Task Requests_without_the_token_are_rejected()
    {
        using var http = new HttpClient();
        using var noToken = await http.PostAsync(_server.Url, new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync("wrong"));
    }

    [Fact]
    public async Task Exposes_the_deploy_tools()
    {
        await using var client = await ConnectAsync();

        var tools = await client.ListToolsAsync();

        Assert.Equal(
            ["cancel_run", "deploy", "get_release_summary", "get_run", "list_backups", "list_targets", "refresh_targets", "restore_backup"],
            tools.Select(t => t.Name).Order());
        Assert.True(tools.Single(t => t.Name == "deploy").ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools.Single(t => t.Name == "list_targets").ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public async Task Lists_targets_filtered_by_name()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("list_targets", new Dictionary<string, object?> { ["filter"] = "staging" });

        Assert.NotEqual(true, result.IsError);
        var json = Text(result);
        Assert.Contains("profile-staging", json);
        Assert.DoesNotContain("profile-prod", json);
    }

    [Fact]
    public async Task Deploy_starts_a_run_that_get_run_follows_to_the_end()
    {
        await using var client = await ConnectAsync();

        var started = await client.CallToolAsync("deploy", new Dictionary<string, object?> { ["targetIds"] = new[] { "profile-prod" } });
        Assert.NotEqual(true, started.IsError);
        var runId = JsonDocument.Parse(Text(started)).RootElement.GetProperty("runId").GetString();
        Assert.Equal(["profile-prod"], _host.LastRequest!.TargetIds);

        _host.LastRun!.AppendLog("[Web → Prod] Publish succeeded.");
        _host.LastRun.SetTargetStatus("profile-prod", "succeeded", "OK");
        _host.LastRun.Complete(RunState.Succeeded, "All 1 target(s) succeeded.");

        var finished = await client.CallToolAsync("get_run", new Dictionary<string, object?> { ["runId"] = runId, ["waitSeconds"] = 5 });
        var run = JsonDocument.Parse(Text(finished)).RootElement;
        Assert.Equal("succeeded", run.GetProperty("state").GetString());
        Assert.Equal("succeeded", run.GetProperty("targets")[0].GetProperty("status").GetString());
        Assert.Contains("Publish succeeded.", run.GetProperty("log")[0].GetString());
    }

    [Fact]
    public async Task Host_errors_come_back_as_tool_errors()
    {
        await using var client = await ConnectAsync();
        _host.DeployError = "A deployment or restore is already running in the app.";

        var result = await client.CallToolAsync("deploy", new Dictionary<string, object?> { ["targetIds"] = new[] { "profile-prod" } });

        Assert.True(result.IsError);
        Assert.Contains("already running", Text(result));
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class FakeDeployerHost : IDeployerHost
    {
        public DeployRequest? LastRequest { get; private set; }
        public DeploymentRun? LastRun { get; private set; }
        public string? DeployError { get; set; }

        public Task<DeployerSnapshot> GetTargetsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeployerSnapshot(false, false, false, true,
            [
                new SourceInfo("Shop.slnx", @"C:\src\Shop.slnx", "solution", null,
                [
                    Target("profile-prod", "Prod"),
                    Target("profile-staging", "Staging"),
                ]),
            ]));

        private static TargetInfo Target(string id, string name) =>
            new(id, "profile", name, "Web", "MSDeploy", "dotnet", $"https://{name.ToLowerInvariant()}:8172/msdeploy.axd",
                true, true, true, null, null, null);

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DeploymentRun> StartDeployAsync(DeployRequest request, CancellationToken cancellationToken = default)
        {
            if (DeployError is not null)
                throw new DeployerException(DeployError);
            LastRequest = request;
            LastRun = new DeploymentRun("deploy", request.TargetIds.Select(id => (id, id)));
            return Task.FromResult(LastRun);
        }

        public Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(string targetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackupInfo>>([]);

        public Task<DeploymentRun> StartRestoreAsync(string targetId, string backupId, CancellationToken cancellationToken = default) =>
            throw new DeployerException("No snapshots.");

        public Task<string> GetReleaseSummaryAsync(string targetId, CancellationToken cancellationToken = default) =>
            Task.FromResult("No changes.");
    }
}
