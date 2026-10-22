using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Mcp;

namespace SolutionDeployer.App.Services;

/// <summary>Runs the MCP server for the app and keeps its access token.</summary>
public interface IMcpServerService
{
    bool IsRunning { get; }

    /// <summary>One line for the options panel, e.g. "Listening on http://localhost:47821/mcp".</summary>
    string StatusText { get; }

    /// <summary>Raised (on any thread) when <see cref="IsRunning"/> or <see cref="StatusText"/> changes.</summary>
    event Action? StatusChanged;

    /// <summary>Starts, restarts or stops the server to match the settings. Never throws: failures land in <see cref="StatusText"/>.</summary>
    Task ApplyAsync(bool enabled, int port);

    Task StopAsync();

    /// <summary>Replaces the access token; clients configured with the old one stop working.</summary>
    void RegenerateToken();

    string ClaudeCodeCommand { get; }

    string ClaudeDesktopConfig { get; }
}

/// <summary>
/// Hosts <see cref="McpServerHost"/> over the app's <see cref="IDeployerHost"/>. The access token is kept in the
/// OS credential store (never in settings.json); without one it lasts until the app exits.
/// </summary>
public sealed class McpServerService(
    Func<IDeployerHost> deployerHost,
    RunRegistry runs,
    ICredentialStore credentialStore) : IMcpServerService
{
    private const string TokenKey = "mcp:access-token";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpServerHost? _server;
    private string? _token;
    private int _port = McpServerHost.DefaultPort;

    public bool IsRunning => _server?.IsRunning == true;

    public string StatusText { get; private set; } = "Off";

    public event Action? StatusChanged;

    private string Url => $"http://localhost:{_port}{McpServerHost.EndpointPath}";

    public string ClaudeCodeCommand => McpConnectionInfo.ClaudeCodeCommand(Url, Token);

    public string ClaudeDesktopConfig => McpConnectionInfo.ClaudeDesktopConfig(Url, Token);

    private string Token
    {
        get
        {
            if (_token is not null)
                return _token;

            _token = credentialStore.IsAvailable ? credentialStore.Get(TokenKey) : null;
            if (string.IsNullOrEmpty(_token))
                StoreToken(McpServerHost.CreateToken());
            return _token!;
        }
    }

    public async Task ApplyAsync(bool enabled, int port)
    {
        await _gate.WaitAsync();
        try
        {
            _port = port;
            if (!enabled)
            {
                await StopServerAsync();
                SetStatus("Off");
                return;
            }

            if (_server is { IsRunning: true } running && running.Port == port)
                return;

            await StopServerAsync();
            var server = new McpServerHost(deployerHost(), runs);
            try
            {
                _ = Token; // created before the first request, so it can't race one
                await server.StartAsync(port, () => Token, AppVersion);
                _server = server;
                SetStatus(credentialStore.IsAvailable
                    ? $"Listening on {server.Url}"
                    : $"Listening on {server.Url} (no secure store: the token changes on every start)");
            }
            catch (Exception ex)
            {
                await server.DisposeAsync();
                SetStatus(ex is IOException
                    ? $"Could not listen on port {port}: it is in use by another program."
                    : $"Could not start: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopServerAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void RegenerateToken()
    {
        StoreToken(McpServerHost.CreateToken());
        StatusChanged?.Invoke();
    }

    private void StoreToken(string token)
    {
        _token = token;
        if (credentialStore.IsAvailable)
            credentialStore.Set(TokenKey, token);
    }

    private async Task StopServerAsync()
    {
        if (_server is null)
            return;
        await _server.DisposeAsync();
        _server = null;
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke();
    }

    private static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
}
