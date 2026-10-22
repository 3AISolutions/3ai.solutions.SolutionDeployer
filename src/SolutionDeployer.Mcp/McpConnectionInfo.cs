using System.Text.Json;
using System.Text.Json.Nodes;

namespace SolutionDeployer.Mcp;

/// <summary>Ready-to-paste snippets that connect Claude clients to the running server.</summary>
public static class McpConnectionInfo
{
    /// <summary>Registers the server with Claude Code for every project (user scope).</summary>
    public static string ClaudeCodeCommand(string url, string token) =>
        $"claude mcp add --transport http --scope user {McpServerHost.ServerName} {url} --header \"Authorization: Bearer {token}\"";

    /// <summary>
    /// The <c>mcpServers</c> entry for Claude Desktop's <c>claude_desktop_config.json</c>. Desktop launches local
    /// servers over stdio, so <c>mcp-remote</c> bridges to the HTTP endpoint. The header goes through an
    /// environment variable without a space after the colon: Windows splits arguments on it otherwise.
    /// </summary>
    public static string ClaudeDesktopConfig(string url, string token)
    {
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [McpServerHost.ServerName] = new JsonObject
                {
                    ["command"] = "npx",
                    ["args"] = new JsonArray("-y", "mcp-remote", url, "--header", "Authorization:${SD_AUTH_HEADER}"),
                    ["env"] = new JsonObject { ["SD_AUTH_HEADER"] = $"Bearer {token}" },
                },
            },
        };
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
