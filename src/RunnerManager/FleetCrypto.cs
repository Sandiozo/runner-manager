using System.Security.Cryptography;
using System.Text;

namespace RunnerManager;

/// <summary>
/// Encryption for dashboard ↔ agent traffic. Every request and response is sealed with AES-256-GCM using a key derived
/// from the fleet pairing code, so only PCs with the same code can read or forge messages. Requests are bound to the
/// target PC and a timestamp, and nonces are remembered for a while to reject replays.
/// </summary>
public static class FleetCrypto
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);
    private static readonly byte[] TransportInfo = Encoding.ASCII.GetBytes("rw-transport-v1");
    private static readonly byte[] DiscoveryInfo = Encoding.ASCII.GetBytes("rw-discovery-v1");

    public static byte[] TransportKey(byte[] pairingKey) => HKDF.DeriveKey(HashAlgorithmName.SHA256, pairingKey, 32, info: TransportInfo);
    public static byte[] DiscoveryKey(byte[] pairingKey) => HKDF.DeriveKey(HashAlgorithmName.SHA256, pairingKey, 32, info: DiscoveryInfo);

    /// <summary>nonce | tag | ciphertext</summary>
    public static byte[] Seal(byte[] transportKey, byte[] plaintext, string associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(transportKey, TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(associatedData));
        var sealedBytes = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(sealedBytes, 0);
        tag.CopyTo(sealedBytes, NonceSize);
        cipher.CopyTo(sealedBytes, NonceSize + TagSize);
        return sealedBytes;
    }

    /// <summary>Returns the plaintext, or null when the message wasn't sealed with this key and associated data.</summary>
    public static byte[]? Open(byte[] transportKey, byte[] sealedBytes, string associatedData)
    {
        if (sealedBytes.Length < NonceSize + TagSize) return null;
        var nonce = sealedBytes.AsSpan(0, NonceSize);
        var tag = sealedBytes.AsSpan(NonceSize, TagSize);
        var cipher = sealedBytes.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(transportKey, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(associatedData));
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    public static string NonceOf(byte[] sealedBytes) => Convert.ToBase64String(sealedBytes, 0, Math.Min(NonceSize, sealedBytes.Length));

    /// <summary>What a request is bound to: the target PC and the time it was sent.</summary>
    public static string RequestContext(string targetMachineKey, long unixMilliseconds)
        => $"rw-request\n{targetMachineKey.ToUpperInvariant()}\n{unixMilliseconds}";

    /// <summary>A response is bound to the request it answers.</summary>
    public static string ResponseContext(string requestNonce) => $"rw-response\n{requestNonce}";

    /// <summary>Whether a request's time is within <see cref="MaxClockSkew"/> of this PC's clock (any value is safe to pass).</summary>
    public static bool IsFresh(long unixMilliseconds)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var skew = (long)MaxClockSkew.TotalMilliseconds;
        return unixMilliseconds >= now - skew && unixMilliseconds <= now + skew;
    }

    // ----- Discovery: authenticated (not secret) UDP probes and answers -----

    private static readonly byte[] ProbeMagic = Encoding.ASCII.GetBytes("RW1?");
    private static readonly byte[] AnswerMagic = Encoding.ASCII.GetBytes("RW1!");

    /// <summary>"RW1?" | nonce(16) | HMAC(nonce) — only fleet members can make a probe agents will answer.</summary>
    public static byte[] Probe(byte[] discoveryKey, out byte[] nonce)
    {
        nonce = RandomNumberGenerator.GetBytes(16);
        var mac = HMACSHA256.HashData(discoveryKey, Concat(Encoding.ASCII.GetBytes("probe"), nonce));
        return Concat(ProbeMagic, nonce, mac);
    }

    public static byte[]? VerifyProbe(byte[] discoveryKey, byte[] packet)
    {
        if (packet.Length != 4 + 16 + 32 || !packet.AsSpan(0, 4).SequenceEqual(ProbeMagic)) return null;
        var nonce = packet[4..20];
        var expected = HMACSHA256.HashData(discoveryKey, Concat(Encoding.ASCII.GetBytes("probe"), nonce));
        return CryptographicOperations.FixedTimeEquals(expected, packet.AsSpan(20)) ? nonce : null;
    }

    /// <summary>"RW1!" | HMAC(nonce | body) | body — proves the answer comes from a fleet member and fits this probe.</summary>
    public static byte[] Answer(byte[] discoveryKey, byte[] probeNonce, string body)
        => Answer(discoveryKey, probeNonce, Encoding.UTF8.GetBytes(body));

    public static byte[] Answer(byte[] discoveryKey, byte[] probeNonce, byte[] bodyBytes)
    {
        var mac = HMACSHA256.HashData(discoveryKey, Concat(probeNonce, bodyBytes));
        return Concat(AnswerMagic, mac, bodyBytes);
    }

    public static string? VerifyAnswer(byte[] discoveryKey, byte[] probeNonce, byte[] packet)
    {
        if (packet.Length < 4 + 32 || !packet.AsSpan(0, 4).SequenceEqual(AnswerMagic)) return null;
        var body = packet[36..];
        var expected = HMACSHA256.HashData(discoveryKey, Concat(probeNonce, body));
        return CryptographicOperations.FixedTimeEquals(expected, packet.AsSpan(4, 32)) ? Encoding.UTF8.GetString(body) : null;
    }

    // ----- Rekey: a new pairing code sent to a PC, under the old code and a fresh key exchange -----

    /// <summary>The ECDH secret between our key and the other side's public key (SubjectPublicKeyInfo).</summary>
    internal static byte[] EcdhSecret(ECDiffieHellman mine, byte[] otherPublicKey)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(otherPublicKey, out _);
        return mine.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);
    }

    /// <summary>The key a new pairing code is sealed with, so that someone who has the old code can't read it off the network.</summary>
    internal static byte[] RekeyKey(byte[] sharedSecret)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, info: Encoding.ASCII.GetBytes("rw-rekey"));

    internal const string RekeyContext = "rw-rekey";

    internal static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}

/// <summary>
/// Remembers recently seen request nonces so a captured request can't be sent again. The nonces are only kept in
/// memory, so requests sent before the guard was created (before the agent started) are refused as well.
/// </summary>
public sealed class ReplayGuard
{
    /// <summary>Room for clocks that are slightly apart, for requests sent just before the agent started.</summary>
    public static readonly TimeSpan StartAllowance = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly long _startedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Whether a request is older than this guard. Such a request could have been captured before a restart, when
    /// its nonce was remembered by a guard that is gone now.
    /// </summary>
    public bool IsFromBeforeStart(long unixMilliseconds)
        => unixMilliseconds < _startedUnixMilliseconds - (long)StartAllowance.TotalMilliseconds;

    public bool TryUse(string nonce)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_seen.Count > 2000)
                foreach (var old in _seen.Where(p => now - p.Value > FleetCrypto.MaxClockSkew * 2).Select(p => p.Key).ToList())
                    _seen.Remove(old);
            if (_seen.ContainsKey(nonce)) return false;
            _seen[nonce] = now;
            return true;
        }
    }
}
