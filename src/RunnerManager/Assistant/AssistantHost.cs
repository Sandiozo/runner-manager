using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace RunnerManager;

/// <summary>
/// Entry points for AI assistants, started by the AI tool itself (no administrator rights needed):
/// <c>RunnerManager.exe --mcp</c> serves MCP over stdin/stdout, and <c>RunnerManager.exe ai &lt;tool&gt; --name value</c>
/// runs one tool and prints JSON, for assistants that run commands instead of MCP. Both forward to this PC's fleet
/// agent, which runs the tools with Runner Manager's real settings (see <see cref="AssistantHttpServer"/>).
/// </summary>
public static class AssistantHost
{
    public const string McpArgument = "--mcp";
    public const string CliArgument = "ai";

    public static bool IsAssistantLaunch(string[] args)
        => args.Length > 0 && (args[0].Equals(McpArgument, StringComparison.OrdinalIgnoreCase) || args[0].Equals(CliArgument, StringComparison.OrdinalIgnoreCase));

    public static Task<int> RunAsync(string[] args)
        => args[0].Equals(McpArgument, StringComparison.OrdinalIgnoreCase) ? RunStdioAsync() : RunCliAsync(args.Skip(1).ToArray());

    /// <summary>Asks the user in a Runner Manager window in front of other windows.</summary>
    public static async Task<bool> ApproveAsync(string client, string title, string message)
    {
        var dispatcher = Application.Current.Dispatcher;
        return await dispatcher.InvokeAsync(() =>
        {
            const string allow = "Allow";
            var destructive = title.StartsWith("Delete", StringComparison.Ordinal) || title.StartsWith("Move", StringComparison.Ordinal);
            var approved = DialogWindow.Show(null, destructive ? DialogTone.Danger : DialogTone.Question, title,
                message.Replace("An AI assistant", client, StringComparison.Ordinal), allow, cancel: "Decline", topmost: true) == allow;
            AssistantLog.Append($"{client}: {title} {(approved ? "Allowed." : "Declined.")}");
            return approved;
        });
    }

    // ----- MCP over stdio -----

    private static async Task<int> RunStdioAsync()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        await using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var writeGate = new SemaphoreSlim(1, 1);

        async Task SendAsync(JsonNode message)
        {
            var line = message.ToJsonString(McpServer.WireJson);
            await writeGate.WaitAsync();
            try { await output.WriteLineAsync(line); }
            catch (IOException) { } // the client went away
            finally { writeGate.Release(); }
        }

        using var link = new AgentLink();
        // Answers on its own only when the agent cannot be reached, so the AI tool still connects and can say why.
        var fallback = new McpServer((_, _, _) => Task.FromResult(false), _ => Task.CompletedTask);
        var pending = new List<Task>();
        while (await input.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            pending.RemoveAll(t => t.IsCompleted);
            // Each request runs on its own, so a long tool call (creating a runner) does not hold up pings or reads.
            pending.Add(ForwardLineAsync(link, fallback, line, SendAsync));
        }
        // The client closed stdin: still answer what it asked (a script may pipe requests in), within reason.
        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromMinutes(15)));
        return 0;
    }

    private static async Task ForwardLineAsync(AgentLink link, McpServer fallback, string line, Func<JsonNode, Task> send)
    {
        try
        {
            if (await link.SendAsync(line) is { } answer && JsonNode.Parse(answer) is { } node) await send(node);
        }
        catch (AgentLinkException ex)
        {
            fallback.ToolsUnavailable = ex.Message;
            await HandleLineAsync(fallback, line, send);
        }
        catch (Exception ex)
        {
            MachineRelay.AppendAgentLog($"MCP request failed: {ex.Message}");
        }
    }

    private static async Task HandleLineAsync(McpServer server, string line, Func<JsonNode, Task> send)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException)
        {
            await send(McpServer.Error(null, -32700, "Parse error"));
            return;
        }
        using (document)
        {
            try
            {
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    // JSON-RPC batches (MCP 2025-03-26).
                    var responses = new JsonArray();
                    foreach (var response in await Task.WhenAll(document.RootElement.EnumerateArray().Select(server.HandleAsync).ToList()))
                        if (response != null) responses.Add(response);
                    if (responses.Count > 0) await send(responses);
                }
                else if (await server.HandleAsync(document.RootElement) is { } response)
                    await send(response);
            }
            catch (Exception ex)
            {
                MachineRelay.AppendAgentLog($"MCP request failed: {ex.Message}");
            }
        }
    }

    // ----- Command line -----

    private static async Task<int> RunCliAsync(string[] args)
    {
        AttachToParentConsole();
        var stdout = Console.Out;
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            stdout.WriteLine(Usage());
            return 0;
        }
        if (args[0] == "tools")
        {
            var tools = AssistantTools.All.Select(t => new { name = t.Name, description = t.Description, kind = t.Kind.ToString().ToLowerInvariant(), input = t.InputSchema });
            stdout.WriteLine(JsonSerializer.Serialize(tools, McpServer.ResultJson));
            return 0;
        }

        JsonElement arguments;
        try { arguments = ParseArguments(args.Skip(1)); }
        catch (AssistantException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        try
        {
            using var link = new AgentLink();
            await link.SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 0, ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = McpServer.SupportedVersions[0],
                    ["clientInfo"] = new JsonObject { ["name"] = "An AI assistant (command line)" }
                }
            }.ToJsonString());
            var answer = await link.SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = args[0], ["arguments"] = JsonNode.Parse(arguments.GetRawText()) }
            }.ToJsonString());
            var reply = JsonNode.Parse(answer ?? "{}");
            if (reply?["result"] is not { } result)
                throw new AgentLinkException(reply?["error"]?["message"]?.GetValue<string>() ?? "The fleet agent sent no answer.");
            var text = result["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
            if (result["isError"]?.GetValue<bool>() == true) throw new AssistantException(text);
            stdout.WriteLine(text);
            return 0;
        }
        catch (Exception ex)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new { error = ex is AssistantException or AgentLinkException ? ex.Message : ex.GetBaseException().Message },
                McpServer.ResultJson));
            return 1;
        }
    }

    /// <summary>"--repository owner/name --labels gpu,cuda --even-if-exists" → a JSON object of tool arguments.</summary>
    internal static JsonElement ParseArguments(IEnumerable<string> args)
    {
        var values = new JsonObject();
        string? pendingName = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pendingName != null) values[pendingName] = true;
                var text = arg[2..];
                var equals = text.IndexOf('=');
                if (equals > 0)
                {
                    values[Key(text[..equals])] = Value(text[(equals + 1)..]);
                    pendingName = null;
                }
                else pendingName = Key(text);
            }
            else if (pendingName != null)
            {
                values[pendingName] = Value(arg);
                pendingName = null;
            }
            else throw new AssistantException($"Unexpected \"{arg}\". Arguments are written as --name value.");
        }
        if (pendingName != null) values[pendingName] = true;
        return JsonDocument.Parse(values.ToJsonString()).RootElement.Clone();

        static string Key(string name) => name.Replace('-', '_').ToLowerInvariant();
        static JsonNode Value(string text)
            => text.Equals("true", StringComparison.OrdinalIgnoreCase) ? JsonValue.Create(true)
                : text.Equals("false", StringComparison.OrdinalIgnoreCase) ? JsonValue.Create(false)
                : int.TryParse(text, out var number) ? JsonValue.Create(number)
                : JsonValue.Create(text);
    }

    private static string Usage()
    {
        var text = new StringBuilder();
        text.AppendLine("Runner Manager for AI assistants");
        text.AppendLine();
        text.AppendLine("  RunnerManager.exe --mcp                      MCP server over stdio (for MCP clients)");
        text.AppendLine("  RunnerManager.exe ai <tool> [--name value]    Run one tool and print JSON");
        text.AppendLine("  RunnerManager.exe ai tools                    List the tools and their arguments as JSON");
        text.AppendLine();
        text.AppendLine("Tools:");
        foreach (var tool in AssistantTools.All) text.AppendLine($"  {tool.Name,-22}{tool.Description.Split(". ")[0].TrimEnd('.')}.");
        text.AppendLine();
        text.AppendLine("Example:");
        text.AppendLine("  RunnerManager.exe ai create_runner --repository owner/name --labels gpu");
        text.AppendLine();
        text.Append("Turn on access in Runner Manager → Settings → AI assistants first.");
        return text.ToString();
    }

    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);

    /// <summary>
    /// Runner Manager is a Windows app, so it has no console of its own. When output is not redirected (typed in a
    /// terminal), write to the terminal it was started from.
    /// </summary>
    private static void AttachToParentConsole()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle != IntPtr.Zero && handle != new IntPtr(-1)) return;
        if (AttachConsole(AttachParentProcess)) Console.WriteLine();
    }
}
