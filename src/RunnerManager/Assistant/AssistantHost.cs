using System.Collections.Concurrent;
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

    /// <summary>An approval nobody answers in this time counts as declined.</summary>
    public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(10);

    /// <summary>After stdin closes, requests still running get this long to finish.</summary>
    private static readonly TimeSpan DrainLimit = TimeSpan.FromMinutes(15);

    /// <summary>The fleet agent's approvals, shown one at a time in the order they came in.</summary>
    public static ApprovalQueue Approvals { get; } = new(ShowApprovalAsync, ApprovalTimeout);

    public static bool IsAssistantLaunch(string[] args)
        => args.Length > 0 && (args[0].Equals(McpArgument, StringComparison.OrdinalIgnoreCase) || args[0].Equals(CliArgument, StringComparison.OrdinalIgnoreCase));

    public static Task<int> RunAsync(string[] args)
        => args[0].Equals(McpArgument, StringComparison.OrdinalIgnoreCase) ? RunStdioAsync() : RunCliAsync(args.Skip(1).ToArray());

    /// <summary>Asks the user in a Runner Manager window in front of other windows, after any approval asked before.</summary>
    public static async Task<bool> ApproveAsync(string client, string title, string message, CancellationToken cancellationToken = default)
    {
        try
        {
            var approved = await Approvals.AskAsync(client, title, message, cancellationToken);
            AssistantLog.Append($"{client}: {title} {(approved ? "Allowed." : "Declined.")}");
            return approved;
        }
        catch (OperationCanceledException)
        {
            AssistantLog.Append($"{client}: {title} Cancelled by the assistant.");
            throw;
        }
        catch (AssistantException)
        {
            AssistantLog.Append($"{client}: {title} Not answered in time, so declined.");
            throw;
        }
    }

    private static async Task<bool> ShowApprovalAsync(string client, string title, string message, CancellationToken cancellationToken)
    {
        var destructive = title.StartsWith("Delete", StringComparison.Ordinal) || title.StartsWith("Move", StringComparison.Ordinal);
        const string allow = "Allow";
        var choice = await Application.Current.Dispatcher.InvokeAsync(() => DialogWindow.ShowApprovalAsync(
            destructive ? DialogTone.Danger : DialogTone.Question, title, message.Replace("An AI assistant", client, StringComparison.Ordinal),
            allow, "Decline", cancellationToken)).Task.Unwrap();
        cancellationToken.ThrowIfCancellationRequested();
        return choice == allow;
    }

    // ----- MCP over stdio -----

    private static async Task<int> RunStdioAsync()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        await using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var writeGate = new SemaphoreSlim(1, 1);
        // Cancelled when the client is gone (stdout broke), or stops waiting for answers: requests still open end then.
        using var clientGone = new CancellationTokenSource();

        // One JSON message per line, never two writes mixed up.
        async Task SendAsync(JsonNode message)
        {
            var line = message.ToJsonString(McpServer.WireJson);
            await writeGate.WaitAsync();
            try { await output.WriteLineAsync(line); }
            catch (IOException) { clientGone.Cancel(); } // the client went away
            catch (ObjectDisposedException) { }
            finally { writeGate.Release(); }
        }

        using var link = new AgentLink();
        // Answers on its own only when the agent cannot be reached, so the AI tool still connects and can say why.
        var fallback = new McpServer((_, _, _, _) => Task.FromResult(false), _ => Task.CompletedTask);
        var pending = new List<Task>();
        while (await ReadLineAsync(input) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            pending.RemoveAll(t => t.IsCompleted);
            // Each request runs on its own, so a long tool call (creating a runner) does not hold up pings or reads.
            pending.Add(ForwardLineAsync(link, fallback, line, SendAsync, clientGone.Token));
        }
        // The client closed stdin: still answer what it asked (a script may pipe requests in), within reason. Then
        // end what is still open, which also closes an approval still waiting in Runner Manager.
        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(DrainLimit, clientGone.Token).ContinueWith(_ => { }));
        clientGone.Cancel();
        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(5)));
        return 0;
    }

    /// <summary>A line from stdin, or null when it closed or broke.</summary>
    private static async Task<string?> ReadLineAsync(StreamReader input)
    {
        try { return await input.ReadLineAsync(); }
        catch (IOException) { return null; }
    }

    private static async Task ForwardLineAsync(AgentLink link, McpServer fallback, string line, Func<JsonNode, Task> send, CancellationToken clientGone)
    {
        try
        {
            // Progress notifications are passed on as they arrive, so a long call does not hit the client's timeout.
            var answer = await link.SendAsync(line, async notification =>
            {
                if (JsonNode.Parse(notification) is { } node) await send(node);
            }, clientGone);
            if (answer != null && JsonNode.Parse(answer) is { } reply) await send(reply);
        }
        catch (OperationCanceledException) when (clientGone.IsCancellationRequested)
        {
        }
        catch (AgentLinkException ex) when (ex.AgentUnavailable)
        {
            fallback.ToolsUnavailable = ex.Message;
            await HandleLineAsync(fallback, line, send);
        }
        catch (AgentLinkException ex)
        {
            // The agent runs but could not answer this request: say so for it alone, not "tools unavailable".
            if (McpServer.ErrorsFor(line, ex.ErrorCode, ex.Message) is { } errors) await send(errors);
        }
        catch (Exception ex)
        {
            MachineRelay.AppendAgentLog($"MCP request failed: {ex.Message}");
            if (McpServer.ErrorsFor(line, McpServer.InternalError, $"Internal error: {ex.GetBaseException().Message}") is { } errors) await send(errors);
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
            if (await server.HandleDocumentAsync(document.RootElement) is { } answer) await send(answer);
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
        try { arguments = ParseArguments(AssistantTools.Find(args[0]), args.Skip(1)); }
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
                ["params"] = new JsonObject
                {
                    ["name"] = args[0],
                    ["arguments"] = JsonNode.Parse(arguments.GetRawText()),
                    ["_meta"] = new JsonObject { ["progressToken"] = 1 }
                }
            }.ToJsonString(), notification =>
            {
                // Progress goes to stderr, so stdout stays one JSON answer.
                try
                {
                    if (JsonNode.Parse(notification)?["params"]?["message"] is JsonValue progress && progress.TryGetValue<string>(out var text))
                        Console.Error.WriteLine(text);
                }
                catch (JsonException) { }
                return Task.CompletedTask;
            });
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

    /// <summary>
    /// "--repository owner/name --labels gpu,cuda --even-if-exists" → a JSON object of tool arguments, typed by the
    /// tool's input schema (so "--name 2024" stays text). Without a tool, values are guessed.
    /// </summary>
    internal static JsonElement ParseArguments(AssistantTool? tool, IEnumerable<string> args)
    {
        var properties = tool?.InputSchema["properties"] as JsonObject;
        var values = new JsonObject();
        string? pendingName = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pendingName != null) values[pendingName] = Value(pendingName, null);
                var text = arg[2..];
                var equals = text.IndexOf('=');
                if (equals > 0)
                {
                    var name = Key(text[..equals]);
                    values[name] = Value(name, text[(equals + 1)..]);
                    pendingName = null;
                }
                else pendingName = Key(text);
            }
            else if (pendingName != null)
            {
                values[pendingName] = Value(pendingName, arg);
                pendingName = null;
            }
            else throw new AssistantException($"Unexpected \"{arg}\". Arguments are written as --name value.");
        }
        if (pendingName != null) values[pendingName] = Value(pendingName, null);
        return JsonDocument.Parse(values.ToJsonString()).RootElement.Clone();

        string Key(string name)
        {
            var key = name.Replace('-', '_').ToLowerInvariant();
            if (properties != null && !properties.ContainsKey(key))
                throw new AssistantException($"{tool!.Name} has no argument --{name}. Its arguments: " +
                                             (properties.Count == 0 ? "none" : string.Join(", ", properties.Select(p => "--" + p.Key.Replace('_', '-')))) + ".");
            return key;
        }

        // A bare --flag is true. Otherwise the schema says what the text is.
        JsonNode Value(string name, string? text)
        {
            var type = properties?[name]?["type"]?.GetValue<string>();
            if (text == null)
                return type is null or "boolean" ? JsonValue.Create(true)
                    : throw new AssistantException($"--{name.Replace('_', '-')} needs a value.");
            switch (type)
            {
                case "string":
                    return JsonValue.Create(text);
                case "array":
                    return new JsonArray(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
                case "integer":
                    return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var whole) ? JsonValue.Create(whole)
                        : throw new AssistantException($"--{name.Replace('_', '-')} needs a whole number, not \"{text}\".");
                case "boolean":
                    return text.Equals("true", StringComparison.OrdinalIgnoreCase) ? JsonValue.Create(true)
                        : text.Equals("false", StringComparison.OrdinalIgnoreCase) ? JsonValue.Create(false)
                        : throw new AssistantException($"--{name.Replace('_', '-')} is true or false, not \"{text}\".");
                default:
                    return text.Equals("true", StringComparison.OrdinalIgnoreCase) ? JsonValue.Create(true)
                        : text.Equals("false", StringComparison.OrdinalIgnoreCase) ? JsonValue.Create(false)
                        : int.TryParse(text, out var number) ? JsonValue.Create(number)
                        : JsonValue.Create(text);
            }
        }
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

/// <summary>
/// Approvals waiting for the user, shown one at a time in the order they were asked, so an older request is never
/// hidden under a newer one. Each one counts as declined when nobody answers within the timeout, and ends at once
/// when its call is cancelled.
/// </summary>
public sealed class ApprovalQueue
{
    private readonly Func<string, string, string, CancellationToken, Task<bool>> _show;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    /// <summary>Completes when the last approval in line is done; the next one waits for it.</summary>
    private Task _tail = Task.CompletedTask;

    /// <param name="show">Shows one approval (client, title, message) and returns whether it was allowed; closes it when the token is cancelled.</param>
    public ApprovalQueue(Func<string, string, string, CancellationToken, Task<bool>> show, TimeSpan timeout)
    {
        _show = show;
        _timeout = timeout;
    }

    /// <summary>
    /// Waits for its turn, then asks. Throws <see cref="OperationCanceledException"/> when the call is cancelled, and
    /// <see cref="AssistantException"/> when nobody answered in time.
    /// </summary>
    public async Task<bool> AskAsync(string client, string title, string message, CancellationToken cancellationToken)
    {
        // An update restarts the agent only after the user has answered.
        using var work = AgentWork.Begin();
        // The time to answer starts when the approval is shown. The ones ahead each time out, so the wait in line ends too.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_gate)
        {
            previous = _tail;
            _tail = done.Task;
        }
        try
        {
            await previous.WaitAsync(cancellationToken);
            timeout.CancelAfter(_timeout);
            return await _show(client, title, message, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            var wait = _timeout.TotalMinutes >= 1 ? $"{(int)_timeout.TotalMinutes} minutes" : $"{Math.Max(1, (int)_timeout.TotalSeconds)} seconds";
            throw new AssistantException($"Nobody answered the approval in Runner Manager within {wait}, so it counts as declined. " +
                                         "Ask the user before trying again.");
        }
        finally
        {
            // The next one goes after this one, and never before the ones ahead of this one.
            _ = previous.ContinueWith(_ => done.TrySetResult(), TaskScheduler.Default);
        }
    }
}
