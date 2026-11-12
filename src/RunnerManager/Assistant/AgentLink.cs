using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunnerManager;

/// <summary>The fleet agent could not be reached, or refused the request.</summary>
/// <param name="agentUnavailable">
/// True when the agent cannot be reached or signed in to, so no tool works; false when it runs but could not answer
/// this one request (too large, a broken connection).
/// </param>
/// <param name="errorCode">The JSON-RPC error code for that one request.</param>
public sealed class AgentLinkException(string message, bool agentUnavailable = true, int errorCode = McpServer.InternalError) : Exception(message)
{
    public bool AgentUnavailable { get; } = agentUnavailable;
    public int ErrorCode { get; } = errorCode;
}

/// <summary>
/// How <c>--mcp</c> and <c>ai</c> reach this PC's fleet agent, which runs the assistant tools. They read nothing from
/// Runner Manager's settings, so they work the same when an AI tool runs them in a sandbox.
/// </summary>
public sealed class AgentLink : IDisposable
{
    /// <summary>Overrides the agent's MCP URL (used by tests).</summary>
    public const string UrlVariable = "RUNNER_MANAGER_AGENT_MCP_URL";

    /// <summary>After a failed connection, later requests fail at once for a while instead of each waiting for Windows.</summary>
    private static readonly TimeSpan UnreachableMemory = TimeSpan.FromSeconds(10);

    private const string OlderAgent = "This PC's fleet agent runs an older Runner Manager. Open Runner Manager and update the fleet agent, or restart it, then try again.";

    /// <summary>
    /// How long the sign-in's two short calls may take. Long enough for a fleet agent on a PC whose runners use all
    /// of its CPU and memory, which is the usual state of such a PC. Only a program that holds the agent's port and
    /// never answers takes longer; without a limit the AI tool would wait for it without end.
    /// </summary>
    private static readonly TimeSpan DefaultSignInTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// After a sign-in that was not answered in time, the requests that waited for it and those that follow fail at
    /// once for a while, instead of each waiting <see cref="DefaultSignInTimeout"/> again. Then the next one asks again:
    /// a busy agent may only have been slow.
    /// </summary>
    private static readonly TimeSpan SignInTimeoutMemory = TimeSpan.FromSeconds(30);

    /// <summary>The most that is read of a sign-in answer, which is under 1 KB.</summary>
    private const int MaxSignInAnswerBytes = 16 * 1024;

    /// <summary>A sign-in: the token for this process's requests, and the key the agent proves itself with on each answer.</summary>
    private sealed class SignedIn(string token, byte[] key)
    {
        public string Token { get; } = token;
        public byte[] Key { get; } = key;
    }

    private readonly HttpClient _http = new(NewHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _signInGate = new(1, 1);
    /// <summary>Only one request signs in again or starts a new session after the agent forgot this one.</summary>
    private readonly SemaphoreSlim _recoverGate = new(1, 1);
    private readonly string _url;
    /// <summary>The port this process connects to; part of what both sides sign in with.</summary>
    private readonly int _port;
    private readonly TimeSpan _signInTimeout;
    private volatile SignedIn? _signedIn;
    private volatile string? _sessionId;
    private volatile string? _initialize;
    private Task? _initializing;

    /// <summary>
    /// A failure that later requests repeat at once until <see cref="Until"/>. Immutable and kept in one volatile
    /// field, so a request that reads it outside the gate sees it whole, never half written.
    /// </summary>
    private sealed record Remembered(DateTime Until, string Message);

    private volatile Remembered? _unreachable;
    private volatile Remembered? _signInTimedOut;

    /// <summary>The clock for what this link remembers (tests pass their own).</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>
    /// How this process connects to the agent: straight to this PC. A proxy from Windows' settings or from the
    /// environment an AI tool starts its programs with (HTTP_PROXY) is not used. It would get the token and every request.
    /// </summary>
    internal static SocketsHttpHandler NewHandler() => new() { ConnectTimeout = TimeSpan.FromSeconds(5), UseProxy = false };

    /// <param name="signInTimeout">A shorter time for the sign-in, for tests.</param>
    public AgentLink(string? url = null, TimeSpan? signInTimeout = null)
    {
        _url = (url ?? Environment.GetEnvironmentVariable(UrlVariable) ?? AssistantHttpServer.Url).TrimEnd('/');
        _port = Uri.TryCreate(_url, UriKind.Absolute, out var uri) ? uri.Port : AssistantHttpServer.Port;
        _signInTimeout = signInTimeout ?? DefaultSignInTimeout;
    }

    /// <summary>Sends one JSON-RPC message (or batch). Returns the answer, or null when there is none (a notification).</summary>
    /// <param name="onNotification">Gets each notification (progress) the agent sends while it works on the request.</param>
    public async Task<string?> SendAsync(string message, Func<string, Task>? onNotification = null, CancellationToken cancellationToken = default)
    {
        if (IsInitialize(message))
        {
            _initialize = message;
            var task = SendCoreAsync(message, isInitialize: true, onNotification, cancellationToken);
            _initializing = task;
            return await task;
        }
        // Requests sent right after initialize wait for its session, so the agent knows which client asks.
        if (_initializing is { IsCompleted: false } initializing)
        {
            try { await initializing.WaitAsync(cancellationToken); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        }
        return await SendCoreAsync(message, isInitialize: false, onNotification, cancellationToken);
    }

    private async Task<string?> SendCoreAsync(string message, bool isInitialize, Func<string, Task>? onNotification, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var signedIn = await SignInAsync(cancellationToken);
            // The client's initialize did not reach the agent (it was not running yet): start the session now, so
            // the agent knows which client asks and a cancellation finds the call it is meant for.
            if (!isInitialize && _sessionId == null && _initialize != null) await StartSessionAsync(cancellationToken);
            var session = _sessionId;
            using var response = await PostAsync(message, signedIn, session, cancellationToken);
            var last = attempt >= 2;
            // The agent restarted and forgot this sign-in: sign in again, and tell it who is asking.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (last) throw new AgentLinkException("The fleet agent did not accept this program's sign-in. Restart Runner Manager's fleet agent, then try again.");
                await RecoverAsync(signedIn, null, !isInitialize, cancellationToken);
                continue;
            }
            // The agent forgot the session (it expired, or made room for others): start a new one the same way.
            if (response.StatusCode == HttpStatusCode.NotFound && session != null && !isInitialize && !last)
            {
                await RecoverAsync(null, session, true, cancellationToken);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Accepted) return null;
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                return await ReadEventsAsync(response, onNotification, cancellationToken);

            string text;
            try { text = await response.Content.ReadAsStringAsync(cancellationToken); }
            catch (Exception ex) when (ex is IOException or HttpRequestException && !cancellationToken.IsCancellationRequested) { throw Broken(); }
            // Answered with the request's own id by the caller, which can read it.
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                throw new AgentLinkException($"The request is larger than {AssistantHttpServer.MaxRequestBytes / 1024} KB, the most Runner Manager accepts.",
                    agentUnavailable: false, McpServer.InvalidRequest);
            // A JSON-RPC answer, also for errors such as a parse error (HTTP 400).
            if (IsJsonRpc(text)) return text;
            throw new AgentLinkException($"The fleet agent answered HTTP {(int)response.StatusCode}. {ErrorText(text)}".Trim(), agentUnavailable: false);
        }
    }

    /// <summary>
    /// Signs in again (after a 401) or starts a new session (after a 404), unless another request already did since
    /// this one failed.
    /// </summary>
    private async Task RecoverAsync(SignedIn? staleSignIn, string? staleSession, bool reinitialize, CancellationToken cancellationToken)
    {
        await _recoverGate.WaitAsync(cancellationToken);
        try
        {
            if (staleSignIn != null)
            {
                if (!ReferenceEquals(_signedIn, staleSignIn)) return;
                _signedIn = null;
                _sessionId = null;
            }
            else
            {
                if (_sessionId != staleSession) return;
                _sessionId = null;
            }
            if (reinitialize && _initialize is { } initialize)
            {
                using var again = await PostAsync(initialize, await SignInAsync(cancellationToken), null, cancellationToken);
            }
        }
        finally { _recoverGate.Release(); }
    }

    /// <summary>Sends the client's initialize again when there is no session, unless another request already did.</summary>
    private async Task StartSessionAsync(CancellationToken cancellationToken)
    {
        await _recoverGate.WaitAsync(cancellationToken);
        try
        {
            if (_sessionId != null || _initialize is not { } initialize) return;
            using var again = await PostAsync(initialize, await SignInAsync(cancellationToken), null, cancellationToken);
        }
        finally { _recoverGate.Release(); }
    }

    /// <summary>
    /// Sends one message as the signed-in client. Every answer must carry the agent's proof for this request, or it
    /// is not used: when the agent stops, another program can listen on its port and gets the next request.
    /// </summary>
    private async Task<HttpResponseMessage> PostAsync(string message, SignedIn signedIn, string? sessionId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new StringContent(message, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signedIn.Token);
        // A tool call is answered as a stream of events: progress first, then the result.
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (sessionId != null) request.Headers.Add("Mcp-Session-Id", sessionId);
        var nonce = RandomNumberGenerator.GetBytes(LocalSignIn.NonceBytes);
        request.Headers.Add(LocalSignIn.NonceHeader, Convert.ToBase64String(nonce));
        var response = await Send(request, cancellationToken);
        // An agent that restarted has forgotten the sign-in and its key, and answers 401. That answer is used for
        // nothing but signing in again, which checks who answers.
        if (response.StatusCode == HttpStatusCode.Unauthorized) return response;
        var proof = response.Headers.TryGetValues(LocalSignIn.ProofHeader, out var proofs) ? proofs.FirstOrDefault() : null;
        if (!LocalSignIn.TryDecode(proof, LocalSignIn.ProofBytes, out var given) || !LocalSignIn.Matches(LocalSignIn.AnswerProof(signedIn.Key, nonce), given))
        {
            response.Dispose();
            // The token went to that program, so it is not sent again. The next request signs in anew.
            if (ReferenceEquals(_signedIn, signedIn))
            {
                _signedIn = null;
                _sessionId = null;
            }
            throw new AgentLinkException("An answer on Runner Manager's port did not show that it comes from your fleet agent, so it was not used. " +
                                         "Try again. If this happens again, check which program uses port " + _port + ".");
        }
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) _sessionId = ids.First();
        return response;
    }

    /// <summary>Reads an event stream: notifications go to the callback as they arrive, and the answer is returned.</summary>
    private static async Task<string> ReadEventsAsync(HttpResponseMessage response, Func<string, Task>? onNotification, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken), new UTF8Encoding(false));
            var data = new StringBuilder();
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(line))
                {
                    // A blank line ends an event.
                    if (data.Length > 0)
                    {
                        var json = data.ToString();
                        data.Clear();
                        if (!IsNotification(json)) return json;
                        if (onNotification != null) await onNotification(json);
                    }
                    if (line == null) break;
                    continue;
                }
                if (line.StartsWith(':')) continue; // keep-alive
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException && !cancellationToken.IsCancellationRequested) { }
        throw Broken();
    }

    private static AgentLinkException Broken()
        => new("The connection to the fleet agent broke before it answered. If this was a change, check with list_runners whether it was made.",
            agentUnavailable: false);

    /// <summary>
    /// Signs in to the agent (see <see cref="LocalSignIn"/>). This process shows that it runs as the agent's Windows
    /// user by opening the secret the agent sealed. The agent shows the same back for a secret this process sealed,
    /// so a program of another user that took the agent's port is not trusted, and nothing of the session is sent to it.
    /// The session token and the key for the agent's proof on each answer come from both secrets and are not sent.
    /// </summary>
    private async Task<SignedIn> SignInAsync(CancellationToken cancellationToken)
    {
        if (_signedIn is { } current) return current;
        ThrowIfSignInTimedOut();
        await _signInGate.WaitAsync(cancellationToken);
        try
        {
            if (_signedIn is { } already) return already;
            // The requests that waited here for a sign-in that timed out do not each try again.
            ThrowIfSignInTimedOut();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(_signInTimeout);
            try { return _signedIn = await NewSignInAsync(limit.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var message = "The fleet agent did not answer this program's sign-in in time. Try again in a minute. " +
                              "If this happens again, restart Runner Manager's fleet agent, or check which program uses port " + _port + ".";
                _signInTimedOut = new Remembered(UtcNow() + SignInTimeoutMemory, message);
                throw new AgentLinkException(message);
            }
        }
        finally { _signInGate.Release(); }
    }

    private void ThrowIfSignInTimedOut()
    {
        if (_signInTimedOut is { } known && UtcNow() < known.Until) throw new AgentLinkException(known.Message);
    }

    /// <summary>
    /// One sign-in. Until the other side has proven itself it may be any program on this PC, so none of its text is
    /// shown: every refusal here is in this program's own words.
    /// </summary>
    private async Task<SignedIn> NewSignInAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var challenge = await Send(new HttpRequestMessage(HttpMethod.Post, $"{_url}/local/challenge"), cancellationToken);
            if (!challenge.IsSuccessStatusCode)
                throw new AgentLinkException(challenge.StatusCode switch
                {
                    // Before 2.3.1 the agent had no local sign-in, and asked for the Connect by URL token instead.
                    HttpStatusCode.Unauthorized or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => OlderAgent,
                    HttpStatusCode.Forbidden => "The fleet agent refused the connection (HTTP 403).",
                    HttpStatusCode.TooManyRequests => "The fleet agent is busy with other sign-ins (HTTP 429). Try again in a minute.",
                    var status => $"The fleet agent could not sign in this program (HTTP {(int)status}). See the fleet agent log."
                });
            var challengeText = await ReadSignInAnswerAsync(challenge, cancellationToken);
            if (!LocalSignIn.TryDecode(ReadString(challengeText, "nonce"), LocalSignIn.NonceBytes, out var agentNonce))
                throw new AgentLinkException("The fleet agent sent an invalid sign-in challenge.");
            // Up to 2.8.2 the agent sent only the random value and took it back encrypted, which every Windows
            // account can do in a way that decrypts there. This program does not sign in that way any more.
            if (ReadString(challengeText, "challenge") is not { } sealedText) throw new AgentLinkException(OlderAgent);
            if (!LocalSignIn.TryDecodeSealed(sealedText, out var agentSealed))
                throw new AgentLinkException("The fleet agent sent an invalid sign-in challenge.");
            // Opens only a secret sealed for this purpose, and never sends it on: see LocalSignIn.
            var agentSecret = LocalSignIn.Open(agentSealed, LocalSignIn.AgentChallenge) ?? throw new AgentLinkException(
                "The fleet agent on this PC runs as another Windows user, or another program answers on Runner Manager's port, so it is not used. " +
                "Start the AI tool as the Windows user that runs Runner Manager, or check which program uses port " + _port + ".");

            var clientNonce = RandomNumberGenerator.GetBytes(LocalSignIn.NonceBytes);
            var clientSecret = RandomNumberGenerator.GetBytes(LocalSignIn.SecretBytes);
            try
            {
                byte[] clientSealed;
                try { clientSealed = LocalSignIn.Seal(clientSecret, LocalSignIn.ClientChallenge); }
                catch (CryptographicException ex) { throw new AgentLinkException($"Windows could not prepare the sign-in to the fleet agent for this user: {ex.Message}"); }
                var body = new JsonObject
                {
                    ["nonce"] = Convert.ToBase64String(agentNonce),
                    ["client_nonce"] = Convert.ToBase64String(clientNonce),
                    ["client_challenge"] = Convert.ToBase64String(clientSealed),
                    ["proof"] = Convert.ToBase64String(LocalSignIn.Proof(agentSecret, byAgent: false, _port, agentNonce, clientNonce, agentSealed, clientSealed))
                }.ToJsonString();
                using var session = await Send(new HttpRequestMessage(HttpMethod.Post, $"{_url}/local/session")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                }, cancellationToken);
                // Many sign-ins at once can push a challenge out before it is used: ask for another.
                if (session.StatusCode == HttpStatusCode.Forbidden && attempt < 2) continue;
                if (!session.IsSuccessStatusCode)
                    throw new AgentLinkException(session.StatusCode == HttpStatusCode.Forbidden
                        ? "The fleet agent did not accept this program's sign-in (HTTP 403). Try again in a minute. If this happens again, restart Runner Manager's fleet agent."
                        : $"The fleet agent could not sign in this program (HTTP {(int)session.StatusCode}). See the fleet agent log.");
                var text = await ReadSignInAnswerAsync(session, cancellationToken);
                // Only this user's agent could open the secret sealed above. Without that nothing more is sent.
                var expected = LocalSignIn.Proof(clientSecret, byAgent: true, _port, agentNonce, clientNonce, agentSealed, clientSealed);
                if (!LocalSignIn.TryDecode(ReadString(text, "agent_proof"), LocalSignIn.ProofBytes, out var agentProof) || !LocalSignIn.Matches(expected, agentProof))
                    throw new AgentLinkException("The program answering on Runner Manager's port could not prove that it is your fleet agent, so it is not used. " +
                                                 "Restart this PC, or check which program uses port " + _port + ".");
                // The agent works out the same token and key; neither was sent. So a program that passed this sign-in on has none.
                return new SignedIn(
                    LocalSignIn.Token(agentSecret, clientSecret, _port, agentNonce, clientNonce, agentSealed, clientSealed),
                    LocalSignIn.SessionKey(agentSecret, clientSecret, _port, agentNonce, clientNonce, agentSealed, clientSealed));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(agentSecret);
                CryptographicOperations.ZeroMemory(clientSecret);
            }
        }
    }

    /// <summary>Reads a sign-in answer, which is short. Whoever sends it is not proven yet and could send text without end.</summary>
    private static async Task<string> ReadSignInAnswerAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxSignInAnswerBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        // Longer than any sign-in answer: read as no answer.
        return length > MaxSignInAnswerBytes ? string.Empty : Encoding.UTF8.GetString(buffer, 0, length);
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_unreachable is { } known && UtcNow() < known.Until) throw new AgentLinkException(known.Message);
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            throw Unreachable(ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unreachable("the connection timed out");
        }
        catch (HttpRequestException)
        {
            // The agent is there, but the connection broke: that says nothing about the next request.
            throw Broken();
        }
    }

    private AgentLinkException Unreachable(string reason)
    {
        var message = $"Runner Manager's fleet agent is not running on this PC ({reason}). " +
                      "Open Runner Manager and turn on Settings → AI assistants; it starts the agent.";
        _unreachable = new Remembered(UtcNow() + UnreachableMemory, message);
        return new AgentLinkException(message);
    }

    private static bool IsInitialize(string message)
    {
        if (!message.Contains("\"initialize\"", StringComparison.Ordinal)) return false;
        try
        {
            using var document = JsonDocument.Parse(message);
            return McpServer.IsMethod(document.RootElement, "initialize");
        }
        catch (JsonException) { return false; }
    }

    private static bool IsNotification(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("method", out _) && !root.TryGetProperty("id", out _);
        }
        catch (JsonException) { return false; }
    }

    private static bool IsJsonRpc(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Array
                   || (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("jsonrpc", out _) && root.TryGetProperty("id", out _));
        }
        catch (JsonException) { return false; }
    }

    private static string? ReadString(string json, string name)
    {
        try { return JsonNode.Parse(json)?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null; }
        // Not JSON, or JSON that is not an object.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    private static string ErrorText(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? string.Empty; }
        catch { return string.Empty; }
    }

    public void Dispose()
    {
        _http.Dispose();
        _signInGate.Dispose();
        _recoverGate.Dispose();
    }
}
