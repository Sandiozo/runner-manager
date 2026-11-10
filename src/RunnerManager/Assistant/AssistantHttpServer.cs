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
/// <item><c>RunnerManager.exe --mcp</c> and <c>ai</c>, started by AI tools, forward to it after showing with DPAPI that
/// they run as the same Windows user (see <see cref="LocalSignIn"/>). This also works when an AI tool runs them in a
/// sandbox with its own copy of %LOCALAPPDATA%.</item>
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
    private const int MaxSessions = 50;
    private const int MaxChallenges = 100;
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromSeconds(30);
    /// <summary>Sign-ins and sessions nobody used for this long are forgotten.</summary>
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The answer to a <c>--mcp</c> or <c>ai</c> process from before the sign-in changed (2.8.2 and older), which an AI
    /// tool started before an update and still runs. Plain characters only: the older <c>ai</c> prints others wrongly.
    /// </summary>
    internal const string OlderProgram =
        "Runner Manager was updated, but this AI tool still runs the older program that it started before the update. " +
        "Restart the AI tool, then try again. If that does not help, copy the setup again from the AI assistants page in Runner Manager's Settings.";

    private readonly int _port;
    private readonly string _host;
    private readonly Func<McpServer> _newServer;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, Challenge> _challenges = new();
    private readonly ConcurrentDictionary<string, LocalClient> _localTokens = new();
    private readonly CancellationTokenSource _cts = new();
    private HttpListener? _http;
    /// <summary>Why the endpoint could not start at the last try; logged when it changes, not at every try.</summary>
    private string? _startProblem;

    private sealed class Session(McpServer server)
    {
        public McpServer Server { get; } = server;
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>A sign-in the agent started: its random value, and the secret only this Windows user can open.</summary>
    private sealed record Challenge(DateTime IssuedUtc, byte[] Nonce, byte[] Secret, byte[] Sealed);

    /// <summary>A signed-in <c>--mcp</c> or <c>ai</c> process, by its token: the key for the proof on each answer to it.</summary>
    private sealed class LocalClient(byte[] key)
    {
        private long _lastUsedTicks = DateTime.UtcNow.Ticks;
        public byte[] Key { get; } = key;

        /// <summary>Set by every request of the client, which can be several at once.</summary>
        public DateTime LastUsedUtc
        {
            get => new(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);
            set => Interlocked.Exchange(ref _lastUsedTicks, value.Ticks);
        }
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

    /// <summary>Tries to listen. A failure is logged once until it changes or ends, however often this is tried again.</summary>
    internal void Start()
    {
        var http = new HttpListener();
        try
        {
            http.Prefixes.Add($"http://{_host}:{_port}/mcp/");
            http.Start();
        }
        catch (Exception ex)
        {
            try { http.Close(); } catch { }
            if (ex.Message != _startProblem)
                MachineRelay.AppendAgentLog($"Could not start the AI assistant endpoint on port {_port}: {ex.Message} " +
                                            $"Trying again every {(int)RetryInterval.TotalSeconds} seconds.");
            _startProblem = ex.Message;
            return;
        }
        _http = http;
        _ = Task.Run(() => AcceptLoopAsync(http));
        if (_startProblem != null) MachineRelay.AppendAgentLog($"The AI assistant endpoint listens on port {_port} now.");
        _startProblem = null;
    }

    private void Stop()
    {
        var http = Interlocked.Exchange(ref _http, null);
        if (http == null) return;
        try { http.Close(); } catch { }
        _sessions.Clear();
        // A sign-in ends with the listener it was made on. Its token may go to whoever listens on the port next.
        _localTokens.Clear();
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

            if (!IsAuthorized(request.Headers["Authorization"], out var local))
            {
                response.AddHeader("WWW-Authenticate", "Bearer");
                await ReplyAsync(response, 401, McpServer.Error(null, -32001,
                    "Missing or wrong access token. Turn on Connect by URL in Runner Manager → Settings → AI assistants, and copy the setup from there."));
                return;
            }
            // Shows a signed-in client, on every answer from here on, that the answer comes from its agent.
            if (local != null && LocalSignIn.TryDecode(request.Headers[LocalSignIn.NonceHeader], LocalSignIn.NonceBytes, out var asked))
                response.AddHeader(LocalSignIn.ProofHeader, Convert.ToBase64String(LocalSignIn.AnswerProof(local.Key, asked)));
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

    // ----- Local sign-in: --mcp and ai show that they run as this Windows user (see LocalSignIn) -----

    /// <summary>
    /// Starts a sign-in: hands out a one-time random value and a fresh secret, sealed so that only this Windows user
    /// can open it. Anyone on this PC can ask, so this route opens nothing a caller sent, and when there are too many
    /// open sign-ins the oldest is dropped: a flood can delay a sign-in (the client asks again), never block it.
    /// </summary>
    private async Task ChallengeAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "POST") { await ReplyAsync(response, 405, null); return; }
        var now = DateTime.UtcNow;
        foreach (var old in _challenges.Where(c => now - c.Value.IssuedUtc > ChallengeLifetime).Select(c => c.Key).ToList()) Drop(old);
        while (_challenges.Count >= MaxChallenges && _challenges.MinBy(c => c.Value.IssuedUtc) is { Key: { } oldest }) Drop(oldest);
        var nonce = RandomNumberGenerator.GetBytes(LocalSignIn.NonceBytes);
        var secret = RandomNumberGenerator.GetBytes(LocalSignIn.SecretBytes);
        var sealedSecret = LocalSignIn.Seal(secret, LocalSignIn.AgentChallenge);
        var id = Convert.ToBase64String(nonce);
        _challenges[id] = new Challenge(now, nonce, secret, sealedSecret);
        await ReplyAsync(response, 200, new JsonObject { ["nonce"] = id, ["challenge"] = Convert.ToBase64String(sealedSecret) });
    }

    /// <summary>Forgets a sign-in that was not finished, and its secret.</summary>
    private void Drop(string id)
    {
        if (_challenges.TryRemove(id, out var challenge)) CryptographicOperations.ZeroMemory(challenge.Secret);
    }

    /// <summary>
    /// Finishes a sign-in and starts its session. The caller must show that it opened this sign-in's secret, which
    /// only a process of the same Windows user can: the same trust as reading the pairing code from fleet.json. The
    /// answer shows the same about the agent, for the secret the caller sealed, so the caller knows it is talking to
    /// its own user's agent. The session's token is not in the answer: both sides work it out from the two secrets.
    /// </summary>
    private async Task LocalSessionAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "POST") { await ReplyAsync(response, 405, null); return; }
        var body = await ReadBodyAsync(request);
        string? nonce = null, clientNonce = null, clientChallenge = null, proof = null;
        try
        {
            using var document = JsonDocument.Parse(body ?? Array.Empty<byte>());
            nonce = Text(document.RootElement, "nonce");
            clientNonce = Text(document.RootElement, "client_nonce");
            clientChallenge = Text(document.RootElement, "client_challenge");
            proof = Text(document.RootElement, "proof");
        }
        // Text that is not valid JSON, or a string that cannot be read (half of a surrogate pair).
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        // A sign-in gets one answer, right or wrong.
        Challenge? challenge = null;
        if (nonce != null) _challenges.TryRemove(nonce, out challenge);
        try
        {
            // Up to 2.8.2 the caller sent the random value back encrypted with DPAPI, which every Windows account can
            // do in a way that decrypts here. That is not accepted any more: the program is told how to get the current one.
            if (proof != null && clientChallenge == null)
            {
                await ReplyAsync(response, 400, McpServer.Error(null, -32001, OlderProgram));
                return;
            }
            if (challenge == null || DateTime.UtcNow - challenge.IssuedUtc > ChallengeLifetime
                || !LocalSignIn.TryDecode(clientNonce, LocalSignIn.NonceBytes, out var clientNonceBytes)
                || !LocalSignIn.TryDecodeSealed(clientChallenge, out var clientSealed)
                || !LocalSignIn.TryDecode(proof, LocalSignIn.ProofBytes, out var proofBytes)
                || !LocalSignIn.Matches(LocalSignIn.Proof(challenge.Secret, byAgent: false, _port, challenge.Nonce, clientNonceBytes, challenge.Sealed, clientSealed), proofBytes)
                // Opened only now that the caller has shown that it runs as this user.
                || LocalSignIn.Open(clientSealed, LocalSignIn.ClientChallenge) is not { } clientSecret)
            {
                await ReplyAsync(response, 403, McpServer.Error(null, -32001, "Runner Manager could not confirm that this program runs as your Windows user."));
                return;
            }
            var agentProof = LocalSignIn.Proof(clientSecret, byAgent: true, _port, challenge.Nonce, clientNonceBytes, challenge.Sealed, clientSealed);
            // The caller works out the same token and key, so the answer carries neither: see LocalSignIn.Token.
            var token = LocalSignIn.Token(challenge.Secret, clientSecret, _port, challenge.Nonce, clientNonceBytes, challenge.Sealed, clientSealed);
            var key = LocalSignIn.SessionKey(challenge.Secret, clientSecret, _port, challenge.Nonce, clientNonceBytes, challenge.Sealed, clientSealed);
            CryptographicOperations.ZeroMemory(clientSecret);
            RemoveIdle(_localTokens);
            while (_localTokens.Count >= MaxSessions && OldestKey(_localTokens) is { } oldest) _localTokens.TryRemove(oldest, out _);
            _localTokens[token] = new LocalClient(key);
            await ReplyAsync(response, 200, new JsonObject { ["agent_proof"] = Convert.ToBase64String(agentProof) });
        }
        finally
        {
            if (challenge != null) CryptographicOperations.ZeroMemory(challenge.Secret);
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

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
    /// <param name="local">The signed-in <c>--mcp</c> or <c>ai</c> process that the token belongs to; null for the Connect by URL token.</param>
    private bool IsAuthorized(string? authorization, out LocalClient? local)
    {
        local = null;
        if (authorization == null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var given = authorization[7..].Trim();
        if (_localTokens.TryGetValue(given, out var client))
        {
            if (DateTime.UtcNow - client.LastUsedUtc <= IdleLifetime)
            {
                client.LastUsedUtc = DateTime.UtcNow;
                local = client;
                return true;
            }
            _localTokens.TryRemove(given, out _);
        }
        var options = FleetConfig.Current.Assistant;
        var token = options.HttpEnabled ? options.HttpToken : string.Empty;
        return token.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token));
    }

    private static void RemoveIdle(ConcurrentDictionary<string, LocalClient> entries)
    {
        var now = DateTime.UtcNow;
        foreach (var old in entries.Where(e => now - e.Value.LastUsedUtc > IdleLifetime).Select(e => e.Key).ToList()) entries.TryRemove(old, out _);
    }

    private static string? OldestKey(ConcurrentDictionary<string, LocalClient> entries)
        => entries.IsEmpty ? null : entries.MinBy(e => e.Value.LastUsedUtc).Key;

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
