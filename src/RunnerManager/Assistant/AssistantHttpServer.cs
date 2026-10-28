using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunnerManager;

/// <summary>
/// The fleet agent's MCP endpoint on 127.0.0.1 ("Streamable HTTP"). All assistant tools run here, in the agent, with
/// Runner Manager's real settings and administrator rights:
/// <list type="bullet">
/// <item><c>RunnerManager.exe --mcp</c> and <c>ai</c>, started by AI tools, forward to it after proving with DPAPI that
/// they run as the same Windows user. This also works when an AI tool runs them in a sandbox with its own copy of
/// %LOCALAPPDATA%.</item>
/// <item>AI tools that connect by URL use the access token from Settings → AI assistants, while that is turned on.</item>
/// </list>
/// Most answers are plain JSON. A tool call is answered as an event stream when the client accepts one: progress
/// while it works, then the result, with keep-alives in between, so a call of several minutes does not look stuck.
/// </summary>
public sealed class AssistantHttpServer : IDisposable
{
    public const int Port = 47830;
    public static string Url => $"http://127.0.0.1:{Port}/mcp";
    public const int MaxRequestBytes = 256 * 1024;
    internal const int ChallengeBytes = 32;
    private const int MaxSessions = 50;
    private const int MaxChallenges = 100;
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromSeconds(30);
    /// <summary>Sign-ins and sessions nobody used for this long are forgotten.</summary>
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);

    /// <summary>Mixed into the proof, so a DPAPI blob made for something else is never accepted.</summary>
    internal static readonly byte[] ProofEntropy = Encoding.UTF8.GetBytes("RunnerManager.LocalAssistant.v1");

    /// <summary>For the agent's proof back to the client; different from the client's, so one can never stand in for the other.</summary>
    internal static readonly byte[] AgentProofEntropy = Encoding.UTF8.GetBytes("RunnerManager.LocalAssistant.Agent.v1");

    private readonly int _port;
    private readonly string _host;
    private readonly Func<McpServer> _newServer;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, DateTime> _challenges = new();
    private readonly ConcurrentDictionary<string, DateTime> _localTokens = new();
    private readonly CancellationTokenSource _cts = new();
    private HttpListener? _http;

    private sealed class Session(McpServer server)
    {
        public McpServer Server { get; } = server;
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <param name="port">Another port, for tests.</param>
    /// <param name="host">"localhost" for tests, which run without administrator rights.</param>
    /// <param name="newServer">Makes the MCP server for a session; tests use one with their own approvals and tools.</param>
    public AssistantHttpServer(int port = Port, string host = "127.0.0.1", Func<McpServer>? newServer = null)
    {
        _port = port;
        _host = host;
        _newServer = newServer ?? (() => new McpServer(AssistantHost.ApproveAsync, _ => Task.CompletedTask));
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
        var started = false;
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
            // No server-to-client stream of its own: answers come back on the POST itself.
            if (request.HttpMethod != "POST") { response.AddHeader("Allow", "POST, DELETE"); await ReplyAsync(response, 405, null); return; }

            var body = await ReadBodyAsync(request);
            if (body == null)
            {
                await ReplyAsync(response, 413, McpServer.Error(null, McpServer.InvalidRequest, $"The request is larger than {MaxRequestBytes / 1024} KB."));
                return;
            }
            JsonDocument document;
            try { document = JsonDocument.Parse(body); }
            catch (JsonException) { await ReplyAsync(response, 400, McpServer.Error(null, -32700, "Parse error")); return; }

            using (document)
            {
                var root = document.RootElement;
                if (SessionFor(request, root, response) is not { } server)
                {
                    // The session ended (or never was): the client starts a new one with initialize, as MCP says.
                    await ReplyAsync(response, 404, McpServer.Error(null, -32001, "Unknown or expired session. Send initialize again."));
                    return;
                }
                if (WantsStream(request, root))
                {
                    started = true;
                    await StreamAsync(response, server, root);
                    return;
                }
                var answer = await server.HandleDocumentAsync(root, cancellationToken: _cts.Token);
                // Notifications and responses are acknowledged without a body.
                await ReplyAsync(response, answer == null ? 202 : 200, answer);
            }
        }
        catch (Exception ex)
        {
            MachineRelay.AppendAgentLog($"AI assistant request failed: {ex.Message}");
            // Answer instead of dropping the connection, which the client would take for an agent that is not running.
            if (!started)
            {
                try { await ReplyAsync(response, 500, McpServer.Error(null, McpServer.InternalError, $"Internal error: {ex.GetBaseException().Message}")); }
                catch { try { response.Abort(); } catch { } }
            }
            else
            {
                try { response.Close(); } catch { }
            }
        }
    }

    /// <summary>
    /// Whether to answer as an event stream: for tool calls (which can take minutes) when the client accepts one, as
    /// MCP clients must. Older Runner Manager bridges accept only JSON, so they get JSON.
    /// </summary>
    private static bool WantsStream(HttpListenerRequest request, JsonElement root)
    {
        var calls = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new List<JsonElement> { root };
        var toolCalls = calls.Where(c => McpServer.IsMethod(c, "tools/call") && c.TryGetProperty("id", out _)).ToList();
        if (toolCalls.Count == 0) return false;
        var accept = request.Headers["Accept"] ?? string.Empty;
        if (accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)) return true;
        // Progress was asked for, and the client accepts anything.
        var wantsProgress = toolCalls.Any(c => c.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object
                                               && p.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                                               && meta.TryGetProperty("progressToken", out _));
        return wantsProgress && (accept.Length == 0 || accept.Contains("*/*", StringComparison.Ordinal));
    }

    /// <summary>
    /// Answers as server-sent events: notifications as they come, keep-alives while waiting, then the answer. When a
    /// write fails the client is gone, so its calls are cancelled (an open approval closes as declined).
    /// </summary>
    private async Task StreamAsync(HttpListenerResponse response, McpServer server, JsonElement root)
    {
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var writeGate = new SemaphoreSlim(1, 1);
        var finished = false;
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.SendChunked = true;
        response.AddHeader("Cache-Control", "no-cache");

        async Task WriteAsync(string text)
        {
            await writeGate.WaitAsync();
            try
            {
                if (finished || gone.IsCancellationRequested) return;
                var bytes = Encoding.UTF8.GetBytes(text);
                await response.OutputStream.WriteAsync(bytes);
                await response.OutputStream.FlushAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                try { gone.Cancel(); } catch (ObjectDisposedException) { }
            }
            finally { writeGate.Release(); }
        }

        static string Event(JsonNode message) => $"event: message\ndata: {message.ToJsonString(McpServer.WireJson)}\n\n";

        // Sends the headers at once, so the client knows the request arrived.
        await WriteAsync(": started\n\n");
        var work = server.HandleDocumentAsync(root, notification => WriteAsync(Event(notification)), gone.Token);
        while (!work.IsCompleted)
        {
            await Task.WhenAny(work, Task.Delay(KeepAliveInterval));
            if (!work.IsCompleted) await WriteAsync(": keep-alive\n\n");
        }
        if (await work is { } answer) await WriteAsync(Event(answer));
        await writeGate.WaitAsync();
        finished = true;
        writeGate.Release();
        try { response.Close(); } catch { }
    }

    // ----- Local sign-in: --mcp and ai prove they run as this Windows user -----

    /// <summary>
    /// Hands out a one-time random value for the caller to encrypt with DPAPI. Anyone on this PC can ask for one, so
    /// when there are too many the oldest is dropped: a flood can delay a sign-in (the client asks again), never block it.
    /// </summary>
    private async Task ChallengeAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "POST") { await ReplyAsync(response, 405, null); return; }
        var now = DateTime.UtcNow;
        foreach (var old in _challenges.Where(c => now - c.Value > ChallengeLifetime).Select(c => c.Key).ToList()) _challenges.TryRemove(old, out _);
        while (_challenges.Count >= MaxChallenges && OldestKey(_challenges) is { } oldest) _challenges.TryRemove(oldest, out _);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(ChallengeBytes));
        _challenges[nonce] = now;
        await ReplyAsync(response, 200, new JsonObject { ["nonce"] = nonce });
    }

    /// <summary>
    /// Exchanges a DPAPI-encrypted challenge for a session token. Only a process of the same Windows user can encrypt
    /// with that user's DPAPI key, which is the same trust as reading the pairing code from fleet.json. The answer
    /// carries the agent's own proof, made from the caller's random value, so the caller knows it is talking to its
    /// own user's agent.
    /// </summary>
    private async Task LocalSessionAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "POST") { await ReplyAsync(response, 405, null); return; }
        var body = await ReadBodyAsync(request);
        string? nonce = null, proof = null, clientNonce = null;
        try
        {
            using var document = JsonDocument.Parse(body ?? Array.Empty<byte>());
            nonce = document.RootElement.GetProperty("nonce").GetString();
            proof = document.RootElement.GetProperty("proof").GetString();
            if (document.RootElement.TryGetProperty("client_nonce", out var c) && c.ValueKind == JsonValueKind.String) clientNonce = c.GetString();
        }
        catch { }
        if (nonce == null || proof == null || !_challenges.TryRemove(nonce, out var issued) || DateTime.UtcNow - issued > ChallengeLifetime
            || !ProofMatches(nonce, proof))
        {
            await ReplyAsync(response, 403, McpServer.Error(null, -32001, "Runner Manager could not confirm that this program runs as your Windows user."));
            return;
        }
        var token = NewToken();
        RemoveIdle(_localTokens);
        while (_localTokens.Count >= MaxSessions && OldestKey(_localTokens) is { } oldest) _localTokens.TryRemove(oldest, out _);
        _localTokens[token] = DateTime.UtcNow;
        var answer = new JsonObject { ["token"] = token };
        if (clientNonce != null && TryFromBase64(clientNonce, out var clientBytes)) answer["agent_proof"] = AgentProof(clientBytes);
        await ReplyAsync(response, 200, answer);
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

    /// <summary>What the agent encrypts to prove itself: the client's random value and a fixed label.</summary>
    internal static byte[] AgentProofText(byte[] clientNonce)
        => clientNonce.Concat(Encoding.UTF8.GetBytes("Runner Manager fleet agent")).ToArray();

    internal static string AgentProof(byte[] clientNonce)
        => Convert.ToBase64String(ProtectedData.Protect(AgentProofText(clientNonce), AgentProofEntropy, DataProtectionScope.CurrentUser));

    private static bool TryFromBase64(string text, out byte[] bytes)
    {
        var buffer = new byte[ChallengeBytes + 3];
        var ok = Convert.TryFromBase64String(text, buffer, out var written) && written == ChallengeBytes;
        bytes = ok ? buffer[..written] : Array.Empty<byte>();
        return ok;
    }

    /// <summary>
    /// One MCP server per client session, so each approval names the right AI tool. Null for a session id this
    /// endpoint does not know (any more).
    /// </summary>
    private McpServer? SessionFor(HttpListenerRequest request, JsonElement root, HttpListenerResponse response)
    {
        var sessionId = request.Headers["Mcp-Session-Id"];
        if (McpServer.IsMethod(root, "initialize"))
        {
            var server = _newServer();
            var id = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            foreach (var old in _sessions.Where(s => now - s.Value.LastUsedUtc > IdleLifetime).Select(s => s.Key).ToList()) _sessions.TryRemove(old, out _);
            while (_sessions.Count >= MaxSessions && _sessions.MinBy(s => s.Value.LastUsedUtc) is { Key: { } oldest }) _sessions.TryRemove(oldest, out _);
            _sessions[id] = new Session(server);
            response.AddHeader("Mcp-Session-Id", id);
            return server;
        }
        if (sessionId == null) return _newServer(); // a client without sessions
        if (!_sessions.TryGetValue(sessionId, out var session)) return null;
        if (DateTime.UtcNow - session.LastUsedUtc > IdleLifetime)
        {
            _sessions.TryRemove(sessionId, out _);
            return null;
        }
        session.LastUsedUtc = DateTime.UtcNow;
        return session.Server;
    }

    /// <summary>Forgets every session, as when they expire (used by tests).</summary>
    internal void ForgetSessions() => _sessions.Clear();

    /// <summary>A session token from the local sign-in, or the Connect by URL token while that is turned on.</summary>
    private bool IsAuthorized(string? authorization)
    {
        if (authorization == null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var given = authorization[7..].Trim();
        if (_localTokens.TryGetValue(given, out var lastUsed))
        {
            if (DateTime.UtcNow - lastUsed <= IdleLifetime)
            {
                _localTokens[given] = DateTime.UtcNow;
                return true;
            }
            _localTokens.TryRemove(given, out _);
        }
        var options = FleetConfig.Current.Assistant;
        var token = options.HttpEnabled ? options.HttpToken : string.Empty;
        return token.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token));
    }

    private static void RemoveIdle(ConcurrentDictionary<string, DateTime> entries)
    {
        var now = DateTime.UtcNow;
        foreach (var old in entries.Where(e => now - e.Value > IdleLifetime).Select(e => e.Key).ToList()) entries.TryRemove(old, out _);
    }

    private static string? OldestKey(ConcurrentDictionary<string, DateTime> entries)
        => entries.IsEmpty ? null : entries.MinBy(e => e.Value).Key;

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
