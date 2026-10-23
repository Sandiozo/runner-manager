namespace RunnerManager;

/// <summary>How to connect one AI tool: what to copy and where it goes.</summary>
public sealed record AssistantClient(string Name, string Where, Func<string, string, string> Snippet, bool NeedsHttp = false);

/// <summary>Setup snippets for AI tools, shown in Settings → AI assistants.</summary>
public static class AssistantSetup
{
    private const string ServerName = "runner-manager";

    /// <param name="exe">The installed RunnerManager.exe.</param>
    /// <param name="token">The HTTP endpoint's token (only used by HTTP snippets).</param>
    public static IReadOnlyList<AssistantClient> Clients { get; } = new[]
    {
        new AssistantClient("Claude Desktop", "Claude → Settings → Developer → Edit Config. Add this to claude_desktop_config.json, then restart Claude.",
            (exe, _) => McpServersJson(exe)),
        new AssistantClient("Claude Code", "Run this in a terminal.",
            (exe, _) => $"claude mcp add --scope user {ServerName} -- \"{exe}\" {AssistantHost.McpArgument}"),
        new AssistantClient("Codex", "Add this to %USERPROFILE%\\.codex\\config.toml (used by the Codex CLI, IDE extension and app).",
            (exe, _) => $"[mcp_servers.{ServerName}]\ncommand = '{exe}'\nargs = [\"{AssistantHost.McpArgument}\"]"),
        new AssistantClient("VS Code", "Command Palette → “MCP: Open User Configuration”, and add this. Works with GitHub Copilot agent mode.",
            (exe, _) => $"{{\n  \"servers\": {{\n    \"{ServerName}\": {{\n      \"type\": \"stdio\",\n      \"command\": \"{Json(exe)}\",\n      \"args\": [\"{AssistantHost.McpArgument}\"]\n    }}\n  }}\n}}"),
        new AssistantClient("Cursor", "Add this to %USERPROFILE%\\.cursor\\mcp.json, or under Cursor Settings → MCP.",
            (exe, _) => McpServersJson(exe)),
        new AssistantClient("Windsurf", "Add this to %USERPROFILE%\\.codeium\\windsurf\\mcp_config.json.",
            (exe, _) => McpServersJson(exe)),
        new AssistantClient("Gemini CLI", "Add this to %USERPROFILE%\\.gemini\\settings.json.",
            (exe, _) => McpServersJson(exe)),
        new AssistantClient("LM Studio", "Program tab → Install → Edit mcp.json, and add this.",
            (exe, _) => McpServersJson(exe)),
        new AssistantClient("Other (URL)", "For AI tools that connect to an MCP server by URL. Send the token as a bearer token.",
            (_, token) => $"{{\n  \"mcpServers\": {{\n    \"{ServerName}\": {{\n      \"url\": \"{AssistantHttpServer.Url}\",\n      \"headers\": {{ \"Authorization\": \"Bearer {token}\" }}\n    }}\n  }}\n}}",
            NeedsHttp: true),
        new AssistantClient("Command line", "For assistants that run commands instead of MCP. Tell the assistant to run this first; it lists every tool.",
            (exe, _) => $"& \"{exe}\" {AssistantHost.CliArgument} help")
    };

    private static string McpServersJson(string exe)
        => $"{{\n  \"mcpServers\": {{\n    \"{ServerName}\": {{\n      \"command\": \"{Json(exe)}\",\n      \"args\": [\"{AssistantHost.McpArgument}\"]\n    }}\n  }}\n}}";

    private static string Json(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
