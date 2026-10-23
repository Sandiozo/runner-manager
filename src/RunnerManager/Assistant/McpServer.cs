using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunnerManager;

/// <summary>
/// The Model Context Protocol (JSON-RPC 2.0) side of the assistant tools, independent of the transport. Any AI tool
/// that supports MCP can use it: Claude, ChatGPT/Codex, Copilot in VS Code, Cursor, Windsurf, Gemini CLI, LM Studio…
/// </summary>
public sealed class McpServer
{
    public static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    internal static readonly JsonSerializerOptions ResultJson = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>For messages on the wire: one line each, without needless escaping.</summary>
    internal static readonly JsonSerializerOptions WireJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string Instructions =
        "Runner Manager manages GitHub Actions self-hosted runners on the user's Windows PCs. " +
        "When a project needs a self-hosted runner, call find_runner_for_repo first and only call create_runner when it reports none. " +
        "After creating one, tell the user which runs-on labels to put in the workflow. " +
        "Changes may need the user's approval in a Runner Manager window; a declined change returns an error, which you should not retry.";

    private readonly Func<string, string, string, Task<bool>> _approve;
    private readonly Func<JsonObject, Task> _notify;
    private string _client = "An AI assistant";

    /// <param name="approve">Asks the user (client name, title, message) and returns whether they approved.</param>
    /// <param name="notify">Sends a notification to the client, such as progress.</param>
    public McpServer(Func<string, string, string, Task<bool>> approve, Func<JsonObject, Task> notify)
    {
        _approve = approve;
        _notify = notify;
    }

    /// <summary>Handles one JSON-RPC message. Returns the response, or null for notifications and responses.</summary>
    public async Task<JsonObject?> HandleAsync(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("method", out var methodElement))
            return null;
        var method = methodElement.GetString() ?? string.Empty;
        var hasId = message.TryGetProperty("id", out var idElement);
        var id = hasId ? JsonNode.Parse(idElement.GetRawText()) : null;
        var parameters = message.TryGetProperty("params", out var p) ? p : default;
        if (!hasId) return null; // notifications/initialized, notifications/cancelled: nothing to answer

        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(),
                "tools/call" => await CallToolAsync(parameters),
                _ => throw new McpError(-32601, $"Method not found: {method}")
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (McpError error)
        {
            return Error(id, error.Code, error.Message);
        }
    }

    public static JsonObject Error(JsonNode? id, int code, string message)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private JsonObject Initialize(JsonElement parameters)
    {
        var requested = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var v) ? v.GetString() : null;
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("clientInfo", out var info)
            && info.ValueKind == JsonValueKind.Object && info.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } clientName)
            _client = clientName;
        return new JsonObject
        {
            ["protocolVersion"] = SupportedVersions.Contains(requested) ? requested : SupportedVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "runner-manager", ["title"] = "Runner Manager", ["version"] = AppUpdater.CurrentVersion },
            ["instructions"] = Instructions
        };
    }

    private static JsonObject ListTools()
    {
        var tools = new JsonArray();
        foreach (var tool in AssistantTools.All)
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = tool.Kind == AssistantToolKind.Read,
                    ["destructiveHint"] = tool.Kind == AssistantToolKind.Destructive,
                    ["idempotentHint"] = tool.Kind == AssistantToolKind.Read,
                    ["openWorldHint"] = true
                }
            });
        return new JsonObject { ["tools"] = tools };
    }

    private async Task<JsonObject> CallToolAsync(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out var nameElement) || nameElement.GetString() is not { } name)
            throw new McpError(-32602, "tools/call needs a tool name.");
        var arguments = parameters.TryGetProperty("arguments", out var a) ? a : default;

        // Progress messages for clients that ask for them (creating a runner takes a few minutes).
        JsonNode? progressToken = parameters.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                                  && meta.TryGetProperty("progressToken", out var token) ? JsonNode.Parse(token.GetRawText()) : null;
        var progress = 0;
        Action<string>? log = progressToken == null ? null : message => _ = _notify(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/progress",
            ["params"] = new JsonObject { ["progressToken"] = progressToken.DeepClone(), ["progress"] = ++progress, ["message"] = message }
        });

        try
        {
            var result = await AssistantTools.CallAsync(name, arguments, (title, message) => _approve(_client, title, message), log);
            return ToolResult(JsonSerializer.Serialize(result, ResultJson), isError: false);
        }
        catch (AssistantException ex)
        {
            return ToolResult(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            return ToolResult(ex.GetBaseException().Message, isError: true);
        }
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError
    };

    private sealed class McpError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
