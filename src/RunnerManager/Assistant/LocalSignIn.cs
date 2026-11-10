using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RunnerManager;

/// <summary>
/// How <c>--mcp</c> and <c>ai</c> on one side and the fleet agent on the other show each other that they run as the
/// same Windows user. Each side seals a fresh random secret with DPAPI for its own user, and the other side shows
/// that it could open it, by answering with a keyed hash made with that secret.
/// <para>
/// The side that checks makes the sealed secret itself, for the current user. It never accepts a sealed value from
/// the side it checks: DPAPI opens a value that was sealed for the whole PC for every account, so being able to
/// hand one over shows nothing.
/// </para>
/// <para>
/// Neither side can be used to open other DPAPI data. Each direction has its own purpose, so only a secret sealed
/// for that direction opens. What was opened is never sent back, only a hash keyed with it over values that both
/// sides chose. The agent opens what a caller sent only after that caller passed its own check.
/// </para>
/// <para>
/// The session token is not sent either: both sides work it out from the two secrets. Windows lets another program
/// listen on a longer address under the agent's (http://127.0.0.1:47830/mcp/local/), which then gets the sign-in
/// requests and can pass them on to the agent. Such a program learns no secret and so no token, and the requests
/// that carry the token go to the agent's own address, which it cannot take while the agent listens.
/// </para>
/// <para>
/// After the sign-in the agent shows on every answer that it has the session key (<see cref="AnswerProof"/>). So a
/// program that takes the agent's address once the agent has stopped is not taken for it by a client that signed
/// in earlier.
/// </para>
/// </summary>
internal static class LocalSignIn
{
    public const int NonceBytes = 32;
    public const int SecretBytes = 32;
    public const int ProofBytes = 32;
    /// <summary>A sealed secret is about 250 bytes; nothing larger is read.</summary>
    public const int MaxSealedBytes = 1024;

    /// <summary>For the secret the agent seals and a client opens.</summary>
    public static readonly byte[] AgentChallenge = Encoding.UTF8.GetBytes("RunnerManager.LocalAssistant.v2.AgentChallenge");

    /// <summary>For the secret a client seals and the agent opens. Different, so neither side opens its own kind for anyone.</summary>
    public static readonly byte[] ClientChallenge = Encoding.UTF8.GetBytes("RunnerManager.LocalAssistant.v2.ClientChallenge");

    private static readonly byte[] ClientLabel = Encoding.ASCII.GetBytes("Runner Manager local sign-in v2, client proof");
    private static readonly byte[] AgentLabel = Encoding.ASCII.GetBytes("Runner Manager local sign-in v2, agent proof");
    private static readonly byte[] TokenLabel = Encoding.ASCII.GetBytes("Runner Manager local sign-in v2, session token");
    private static readonly byte[] KeyLabel = Encoding.ASCII.GetBytes("Runner Manager local sign-in v2, session key");
    private static readonly byte[] AnswerLabel = Encoding.ASCII.GetBytes("Runner Manager local sign-in v2, answer proof");

    /// <summary>On each request of a signed-in client: a fresh random value for the agent's proof on the answer.</summary>
    public const string NonceHeader = "X-Runner-Manager-Nonce";

    /// <summary>On the answer: the agent's proof for that random value.</summary>
    public const string ProofHeader = "X-Runner-Manager-Proof";

    /// <summary>Seals a secret so that only this Windows user can open it, and only for this purpose.</summary>
    public static byte[] Seal(byte[] secret, byte[] purpose) => ProtectedData.Protect(secret, purpose, DataProtectionScope.CurrentUser);

    /// <summary>The secret, or null when this Windows user cannot open it, it was sealed for something else, or it is not a secret of ours.</summary>
    public static byte[]? Open(byte[] sealedSecret, byte[] purpose)
    {
        byte[] secret;
        // The scope is ignored when opening, which is why only the side that sealed a secret may rely on it.
        try { secret = ProtectedData.Unprotect(sealedSecret, purpose, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { return null; }
        if (secret.Length == SecretBytes) return secret;
        CryptographicOperations.ZeroMemory(secret);
        return null;
    }

    /// <summary>
    /// What one side sends to show that it opened the other side's secret: a hash keyed with that secret over the
    /// whole exchange. The random values make every sign-in different, so an old proof is of no use later. The port
    /// is the one the agent listens on and the client connects to, so an exchange passed on through another port
    /// does not match. The sealed secrets are part of it, so neither can be swapped on the way.
    /// </summary>
    /// <param name="secret">The opened secret: the agent's for a client's proof, the client's for the agent's proof.</param>
    public static byte[] Proof(byte[] secret, bool byAgent, int port, byte[] agentNonce, byte[] clientNonce, byte[] agentSealed, byte[] clientSealed)
        => HMACSHA256.HashData(secret, Exchange(byAgent ? AgentLabel : ClientLabel, port, agentNonce, clientNonce, agentSealed, clientSealed));

    /// <summary>
    /// The token for the session that a sign-in starts. Each side works it out from both secrets, so it is not sent
    /// during the sign-in. A program that passes a sign-in on between a client and the agent sees every message of
    /// it and still has no token: it could open neither secret.
    /// </summary>
    public static string Token(byte[] agentSecret, byte[] clientSecret, int port, byte[] agentNonce, byte[] clientNonce, byte[] agentSealed, byte[] clientSealed)
        => "rmai_" + Convert.ToHexString(FromBoth(TokenLabel, agentSecret, clientSecret, port, agentNonce, clientNonce, agentSealed, clientSealed)).ToLowerInvariant();

    /// <summary>
    /// The key for the agent's proof on each answer of the session (<see cref="AnswerProof"/>). Worked out by each
    /// side like the token, and never sent at all.
    /// </summary>
    public static byte[] SessionKey(byte[] agentSecret, byte[] clientSecret, int port, byte[] agentNonce, byte[] clientNonce, byte[] agentSealed, byte[] clientSealed)
        => FromBoth(KeyLabel, agentSecret, clientSecret, port, agentNonce, clientNonce, agentSealed, clientSealed);

    /// <summary>
    /// The agent's proof on one answer: a hash keyed with the session key over the random value that the request
    /// carried. The token alone does not show who answers. When the agent stops, another program can listen in its
    /// place and gets the client's next request, token included. It has no key, so the client does not take its answer.
    /// </summary>
    public static byte[] AnswerProof(byte[] sessionKey, byte[] requestNonce)
    {
        var text = new byte[AnswerLabel.Length + 1 + NonceBytes];
        AnswerLabel.CopyTo(text, 0);
        Fixed(requestNonce, NonceBytes).CopyTo(text, AnswerLabel.Length + 1);
        return HMACSHA256.HashData(Fixed(sessionKey, SecretBytes), text);
    }

    /// <summary>A value that only someone with both secrets can work out, for one purpose and one exchange.</summary>
    private static byte[] FromBoth(byte[] label, byte[] agentSecret, byte[] clientSecret, int port, byte[] agentNonce, byte[] clientNonce, byte[] agentSealed, byte[] clientSealed)
    {
        var key = new byte[SecretBytes * 2];
        Fixed(agentSecret, SecretBytes).CopyTo(key, 0);
        Fixed(clientSecret, SecretBytes).CopyTo(key, SecretBytes);
        var value = HMACSHA256.HashData(key, Exchange(label, port, agentNonce, clientNonce, agentSealed, clientSealed));
        CryptographicOperations.ZeroMemory(key);
        return value;
    }

    /// <summary>What a proof or a token is made over: what it is for, and every value of the sign-in that both sides saw.</summary>
    private static byte[] Exchange(byte[] label, int port, byte[] agentNonce, byte[] clientNonce, byte[] agentSealed, byte[] clientSealed)
    {
        // The label ends with a zero byte and every part after it has a fixed length, so no two exchanges read the same.
        var text = new byte[label.Length + 1 + sizeof(int) + NonceBytes * 2 + SHA256.HashSizeInBytes * 2];
        var rest = text.AsSpan();
        label.CopyTo(rest);
        rest = rest[(label.Length + 1)..];
        BinaryPrimitives.WriteInt32BigEndian(rest, port);
        rest = rest[sizeof(int)..];
        Fixed(agentNonce, NonceBytes).CopyTo(rest);
        rest = rest[NonceBytes..];
        Fixed(clientNonce, NonceBytes).CopyTo(rest);
        rest = rest[NonceBytes..];
        SHA256.HashData(agentSealed, rest);
        rest = rest[SHA256.HashSizeInBytes..];
        SHA256.HashData(clientSealed, rest);
        return text;
    }

    private static byte[] Fixed(byte[] value, int length)
        => value.Length == length ? value : throw new ArgumentException("A sign-in value has the wrong length.", nameof(value));

    /// <summary>Compares in a time that does not depend on where the two differ.</summary>
    public static bool Matches(byte[] expected, byte[] given) => CryptographicOperations.FixedTimeEquals(expected, given);

    /// <summary>Base64 text of exactly this many bytes.</summary>
    public static bool TryDecode(string? text, int length, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (text == null) return false;
        var buffer = new byte[length + 3];
        if (!Convert.TryFromBase64String(text, buffer, out var written) || written != length) return false;
        bytes = buffer[..written];
        return true;
    }

    /// <summary>Base64 text of a sealed secret, within the size one can have.</summary>
    public static bool TryDecodeSealed(string? text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(text)) return false;
        var buffer = new byte[MaxSealedBytes];
        if (!Convert.TryFromBase64String(text, buffer, out var written) || written == 0) return false;
        bytes = buffer[..written];
        return true;
    }
}
