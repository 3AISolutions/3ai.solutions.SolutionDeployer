using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace SolutionDeployer.Mcp;

/// <summary>
/// Serves the deployer's MCP tools over Streamable HTTP on <c>http://localhost:{port}/mcp</c>. Only local
/// connections are accepted (it binds the loopback interfaces), and every request must carry
/// <c>Authorization: Bearer {token}</c>, so other users' processes and web pages (DNS rebinding) can't deploy.
/// </summary>
public sealed class McpServerHost(IDeployerHost host, RunRegistry runs) : IAsyncDisposable
{
    public const int DefaultPort = 47821;
    public const string EndpointPath = "/mcp";
    public const string ServerName = "solution-deployer";

    private const string Instructions =
        "Solution Deployer publishes .NET projects from their publish profiles and runs deployment scripts. " +
        "Call list_targets to see what can be deployed, then deploy with the target ids. A deploy returns at once with a run id: " +
        "call get_run with waitSeconds until the run is no longer 'running', and report each target's result. " +
        "Deploys and restores change live servers, so confirm the exact targets with the user before starting one. " +
        "Passwords are never sent over MCP: a target with credentialsReady=false needs its password saved in the app first.";

    private WebApplication? _app;

    public bool IsRunning => _app is not null;

    public int Port { get; private set; }

    public string Url => $"http://localhost:{Port}{EndpointPath}";

    /// <summary>Starts listening. Throws (e.g. <see cref="IOException"/> when the port is taken) if it can't.</summary>
    /// <param name="token">Returns the current access token, read per request so it can change without a restart.</param>
    public async Task StartAsync(int port, Func<string> token, string version, CancellationToken cancellationToken = default)
    {
        await StopAsync();

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(McpServerHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

        builder.Services.AddSingleton(host);
        builder.Services.AddSingleton(runs);
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = ServerName, Title = "3ai Solution Deployer", Version = version };
                options.ServerInstructions = Instructions;
            })
            // Stateless for current clients; older ones that still initialize get a session.
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients)
            .WithTools<DeployerTools>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!IsAuthorized(context.Request, token()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            await next(context);
        });
        app.MapMcp(EndpointPath);

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        _app = app;
        Port = port;
    }

    public async Task StopAsync()
    {
        if (_app is not { } app)
            return;

        _app = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Open streams didn't close in time; disposing drops them.
        }

        await app.DisposeAsync();
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    internal static bool IsAuthorized(HttpRequest request, string token)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(token) || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var presented = Encoding.UTF8.GetBytes(header[scheme.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(token));
    }

    /// <summary>A new random access token (URL-safe, 256 bits).</summary>
    public static string CreateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
