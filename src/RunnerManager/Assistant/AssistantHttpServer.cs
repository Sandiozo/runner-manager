using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunnerManager;

/// <summary>
/// MCP over HTTP ("Streamable HTTP", answered with plain JSON) for AI tools that connect to a URL instead of starting a
/// program. Runs in the fleet agent, listens on 127.0.0.1 only, and needs the access token from Settings → AI assistants.
/// </summary>
public sealed class AssistantHttpServer : IDisposable
{
    public const int Port = 47830;
    public static string Url => $"http://127.0.0.1:{Port}/mcp";
    private const int MaxRequestBytes = 256 * 1024;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3);

    private readonly int _port;
    private readonly string _host;
    private readonly ConcurrentDictionary<string, McpServer> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private HttpListener? _http;

    /// <param name="port">Another port, for tests.</param>
    /// <param name="host">"localhost" for tests, which run without administrator rights.</param>
    public AssistantHttpServer(int port = Port, string host = "127.0.0.1")
    {
        _port = port;
        _host = host;
    }

    public static string NewToken() => "rmai_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>Starts and stops the listener as the setting changes, until the agent exits.</summary>
    public async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var options = FleetConfig.Current.Assistant;
            var wanted = options.Enabled && options.HttpEnabled && options.HttpToken.Length > 0;
            if (wanted && _http == null) Start();
            else if (!wanted && _http != null) Stop();
            try { await Task.Delay(CheckInterval, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
        Stop();
    }

    private void Start()
    {
        try
        {
            var http = new HttpListener();
            http.Prefixes.Add($"http://{_host}:{_port}/mcp/");
            http.Start();
            _http = http;
            _ = Task.Run(() => AcceptLoopAsync(http));
            MachineRelay.AppendAgentLog($"AI assistant endpoint listening on http://{_host}:{_port}/mcp.");
        }
        catch (Exception ex)
        {
            MachineRelay.AppendAgentLog($"Could not start the AI assistant endpoint on port {_port}: {ex.Message}");
        }
    }

    private void Stop()
    {
        var http = Interlocked.Exchange(ref _http, null);
        if (http == null) return;
        try { http.Close(); } catch { }
        _sessions.Clear();
        MachineRelay.AppendAgentLog("AI assistant endpoint stopped.");
    }

    private async Task AcceptLoopAsync(HttpListener http)
    {
        while (http.IsListening)
        {
            HttpListenerContext context;
            try { context = await http.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            // http.sys routes by the Host header, so also check that the connection itself comes from this PC.
            if (request.RemoteEndPoint is not { } remote || !IPAddress.IsLoopback(remote.Address))
            { await ReplyAsync(response, 403, null); return; }
            // A web page in a browser must not reach the endpoint (DNS rebinding); AI tools send no Origin or a local one.
            if (request.Headers["Origin"] is { Length: > 0 } origin && !IsLocalOrigin(origin))
            { await ReplyAsync(response, 403, null); return; }
            if (!HasToken(request.Headers["Authorization"]))
            {
                response.AddHeader("WWW-Authenticate", "Bearer");
                await ReplyAsync(response, 401, McpServer.Error(null, -32001, "Missing or wrong access token. Copy it from Runner Manager → Settings → AI assistants."));
                return;
            }
            if (request.HttpMethod == "DELETE")
            {
                if (request.Headers["Mcp-Session-Id"] is { } ended) _sessions.TryRemove(ended, out _);
                await ReplyAsync(response, 200, null);
                return;
            }
            // No server-to-client stream: answers come back on the POST itself.
            if (request.HttpMethod != "POST") { response.AddHeader("Allow", "POST, DELETE"); await ReplyAsync(response, 405, null); return; }
            if (request.ContentLength64 > MaxRequestBytes) { await ReplyAsync(response, 413, null); return; }

            var body = await ReadBodyAsync(request.InputStream);
            if (body == null) { await ReplyAsync(response, 413, null); return; }
            JsonDocument document;
            try { document = JsonDocument.Parse(body); }
            catch (JsonException) { await ReplyAsync(response, 400, McpServer.Error(null, -32700, "Parse error")); return; }

            using (document)
            {
                var root = document.RootElement;
                var server = SessionFor(request, root, response);
                JsonNode? answer;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    var list = new JsonArray();
                    foreach (var item in await Task.WhenAll(root.EnumerateArray().Select(server.HandleAsync).ToList()))
                        if (item != null) list.Add(item);
                    answer = list.Count > 0 ? list : null;
                }
                else answer = await server.HandleAsync(root);
                // Notifications and responses are acknowledged without a body.
                await ReplyAsync(response, answer == null ? 202 : 200, answer);
            }
        }
        catch (Exception ex)
        {
            MachineRelay.AppendAgentLog($"AI assistant request failed: {ex.Message}");
            try { response.Abort(); } catch { }
        }
    }

    /// <summary>One MCP server per client session, so each approval names the right AI tool.</summary>
    private McpServer SessionFor(HttpListenerRequest request, JsonElement root, HttpListenerResponse response)
    {
        var isInitialize = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("method", out var m) && m.GetString() == "initialize";
        if (!isInitialize && request.Headers["Mcp-Session-Id"] is { } id && _sessions.TryGetValue(id, out var existing)) return existing;

        // Progress notifications need a stream back to the client, which this endpoint does not keep, so they are dropped.
        var server = new McpServer(AssistantHost.ApproveAsync, _ => Task.CompletedTask);
        if (isInitialize)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            if (_sessions.Count > 50) _sessions.Clear();
            _sessions[sessionId] = server;
            response.AddHeader("Mcp-Session-Id", sessionId);
        }
        return server;
    }

    private static bool HasToken(string? authorization)
    {
        var token = FleetConfig.Current.Assistant.HttpToken;
        if (token.Length == 0 || authorization == null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var given = Encoding.UTF8.GetBytes(authorization[7..].Trim());
        return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(token));
    }

    internal static bool IsLocalOrigin(string origin)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
           && (uri.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1" || uri.Scheme is "vscode-webview" or "vscode-file");

    private static async Task<byte[]?> ReadBodyAsync(Stream input)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > MaxRequestBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task ReplyAsync(HttpListenerResponse response, int status, JsonNode? body)
    {
        response.StatusCode = status;
        if (body != null)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToJsonString(McpServer.WireJson));
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
        response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        Stop();
    }
}
