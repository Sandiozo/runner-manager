using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunnerManager;

/// <summary>
/// The fleet agent's MCP endpoint on 127.0.0.1 ("Streamable HTTP", answered with plain JSON). All assistant tools run
/// here, in the agent, with Runner Manager's real settings and administrator rights:
/// <list type="bullet">
/// <item><c>RunnerManager.exe --mcp</c> and <c>ai</c>, started by AI tools, forward to it after proving with DPAPI that
/// they run as the same Windows user. This also works when an AI tool runs them in a sandbox with its own copy of
/// %LOCALAPPDATA%.</item>
/// <item>AI tools that connect by URL use the access token from Settings → AI assistants, while that is turned on.</item>
/// </list>
/// </summary>
public sealed class AssistantHttpServer : IDisposable
{
    public const int Port = 47830;
    public static string Url => $"http://127.0.0.1:{Port}/mcp";
    private const int MaxRequestBytes = 256 * 1024;
    private const int MaxSessions = 50;
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>Mixed into the proof, so a DPAPI blob made for something else is never accepted.</summary>
    internal static readonly byte[] ProofEntropy = Encoding.UTF8.GetBytes("RunnerManager.LocalAssistant.v1");

    private readonly int _port;
    private readonly string _host;
    private readonly ConcurrentDictionary<string, McpServer> _sessions = new();
    private readonly ConcurrentDictionary<string, DateTime> _challenges = new();
    private readonly ConcurrentDictionary<string, DateTime> _localTokens = new();
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

    /// <summary>Listens until the agent exits, retrying when the port is taken.</summary>
    public async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (_http == null) Start();
            try { await Task.Delay(RetryInterval, _cts.Token); }
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

            var path = request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            if (path.EndsWith("/local/challenge", StringComparison.OrdinalIgnoreCase)) { await ChallengeAsync(request, response); return; }
            if (path.EndsWith("/local/session", StringComparison.OrdinalIgnoreCase)) { await LocalSessionAsync(request, response); return; }

            if (!IsAuthorized(request.Headers["Authorization"]))
            {
                response.AddHeader("WWW-Authenticate", "Bearer");
                await ReplyAsync(response, 401, McpServer.Error(null, -32001,
                    "Missing or wrong access token. Turn on Connect by URL in Runner Manager → Settings → AI assistants, and copy the setup from there."));
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

            var body = await ReadBodyAsync(request);
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

    // ----- Local sign-in: --mcp and ai prove they run as this Windows user -----

    /// <summary>Hands out a one-time random value for the caller to encrypt with DPAPI.</summary>
    private async Task ChallengeAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "POST") { await ReplyAsync(response, 405, null); return; }
        var now = DateTime.UtcNow;
        foreach (var old in _challenges.Where(c => now - c.Value > ChallengeLifetime).Select(c => c.Key).ToList()) _challenges.TryRemove(old, out _);
        if (_challenges.Count > 100) { await ReplyAsync(response, 429, null); return; }
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _challenges[nonce] = now;
        await ReplyAsync(response, 200, new JsonObject { ["nonce"] = nonce });
    }

    /// <summary>
    /// Exchanges a DPAPI-encrypted challenge for a session token. Only a process of the same Windows user can encrypt
    /// with that user's DPAPI key, which is the same trust as reading the pairing code from fleet.json.
    /// </summary>
    private async Task LocalSessionAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "POST") { await ReplyAsync(response, 405, null); return; }
        var body = await ReadBodyAsync(request);
        string? nonce = null, proof = null;
        try
        {
            using var document = JsonDocument.Parse(body ?? Array.Empty<byte>());
            nonce = document.RootElement.GetProperty("nonce").GetString();
            proof = document.RootElement.GetProperty("proof").GetString();
        }
        catch { }
        if (nonce == null || proof == null || !_challenges.TryRemove(nonce, out var issued) || DateTime.UtcNow - issued > ChallengeLifetime
            || !ProofMatches(nonce, proof))
        {
            await ReplyAsync(response, 403, McpServer.Error(null, -32001, "Runner Manager could not confirm that this program runs as your Windows user."));
            return;
        }
        var token = NewToken();
        if (_localTokens.Count > MaxSessions) _localTokens.Clear();
        _localTokens[token] = DateTime.UtcNow;
        await ReplyAsync(response, 200, new JsonObject { ["token"] = token });
    }

    internal static bool ProofMatches(string nonce, string proof)
    {
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(proof), ProofEntropy, DataProtectionScope.CurrentUser);
            return CryptographicOperations.FixedTimeEquals(plain, Convert.FromBase64String(nonce));
        }
        catch { return false; }
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
            if (_sessions.Count > MaxSessions) _sessions.Clear();
            _sessions[sessionId] = server;
            response.AddHeader("Mcp-Session-Id", sessionId);
        }
        return server;
    }

    /// <summary>A session token from the local sign-in, or the Connect by URL token while that is turned on.</summary>
    private bool IsAuthorized(string? authorization)
    {
        if (authorization == null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var given = authorization[7..].Trim();
        if (_localTokens.ContainsKey(given)) return true;
        var options = FleetConfig.Current.Assistant;
        var token = options.HttpEnabled ? options.HttpToken : string.Empty;
        return token.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token));
    }

    internal static bool IsLocalOrigin(string origin)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
           && (uri.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1" || uri.Scheme is "vscode-webview" or "vscode-file");

    private static async Task<byte[]?> ReadBodyAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxRequestBytes) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk)) > 0)
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
