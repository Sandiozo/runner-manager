using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunnerManager;

/// <summary>
/// Settings shared by the dashboard and the fleet agent on one PC (both run as the same Windows user),
/// stored in %LOCALAPPDATA%\RunnerManager\fleet.json. Re-read automatically when the file changes.
/// </summary>
public sealed class FleetConfig
{
    public static readonly string StateDir = AppPaths.StateDir;
    private static readonly string ConfigPath = Path.Combine(StateDir, "fleet.json");
    private static readonly object Gate = new();
    private static FleetConfig? _cached;
    private static DateTime _cachedStamp;
    private static DateTime? _readFailedUtc;
    private const int ReadAttempts = 5;
    private static readonly TimeSpan FailedReadRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The fleet pairing code, encrypted for this Windows user with DPAPI.</summary>
    public string ProtectedFleetKey { get; set; } = string.Empty;
    /// <summary>Other PCs in the fleet and where they were last reached.</summary>
    public List<PeerRecord> KnownPeers { get; set; } = new();
    /// <summary>Addresses added by hand, for PCs that broadcast discovery can't find (e.g. over Tailscale).</summary>
    public List<string> ManualAddresses { get; set; } = new();
    /// <summary>Restart runners and Remote Desktop Commander automatically when they stop unexpectedly.</summary>
    public bool Watchdog { get; set; } = true;
    /// <summary>Runner service names (and "rdc") the user stopped on purpose, so the watchdog leaves them alone.</summary>
    public List<string> ManuallyStopped { get; set; } = new();
    /// <summary>Runner folders found outside C:\GitHub-Runners (console runners), so they stay listed while stopped.</summary>
    public List<string> KnownRunnerRoots { get; set; } = new();
    /// <summary>Where new and moved runners are installed on this PC; empty means C:\GitHub-Runners.</summary>
    public string RunnerFolder { get; set; } = string.Empty;

    public static FleetConfig Current
    {
        get
        {
            lock (Gate)
            {
                var stamp = File.Exists(ConfigPath) ? File.GetLastWriteTimeUtc(ConfigPath) : DateTime.MinValue;
                var retryFailedRead = _readFailedUtc is { } failed && DateTime.UtcNow - failed > FailedReadRetryInterval;
                if (_cached != null && stamp == _cachedStamp && !retryFailedRead) return _cached;
                try
                {
                    _cached = Read(out var hasPlainKey);
                    _readFailedUtc = null;
                    if (hasPlainKey)
                    {
                        // Earlier builds also wrote the decrypted pairing code; save once without it.
                        // Write never reads the config back, so this cannot recurse.
                        try
                        {
                            Write(_cached);
                            stamp = File.GetLastWriteTimeUtc(ConfigPath);
                        }
                        catch { }
                    }
                }
                catch
                {
                    // Keep the last good settings (or defaults) and try again a little later.
                    _cached ??= new FleetConfig();
                    _readFailedUtc = DateTime.UtcNow;
                }
                _cachedStamp = stamp;
                return _cached;
            }
        }
    }

    /// <summary>
    /// Reads fleet.json. Only a missing file gives defaults; a file that cannot be read or parsed (for example while
    /// another process writes it) is retried briefly and then reported, so callers never mistake it for empty settings.
    /// </summary>
    private static FleetConfig Read(out bool hasPlainKey)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                hasPlainKey = false;
                if (!File.Exists(ConfigPath)) return new FleetConfig();
                var json = File.ReadAllText(ConfigPath);
                using (var document = JsonDocument.Parse(json))
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Null) return new FleetConfig();
                    hasPlainKey = document.RootElement.ValueKind == JsonValueKind.Object
                                  && document.RootElement.TryGetProperty(nameof(FleetKey), out _);
                }
                return JsonSerializer.Deserialize<FleetConfig>(json, JsonOptions) ?? new FleetConfig();
            }
            catch (JsonException) when (attempt >= ReadAttempts - 1)
            {
                // Writes are atomic, so a file that stays invalid is damaged rather than half-written. Keep a copy for
                // inspection and continue with defaults, instead of blocking every later save.
                hasPlainKey = false;
                try { File.Move(ConfigPath, $"{ConfigPath}.damaged-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return new FleetConfig();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                if (attempt >= ReadAttempts - 1)
                    throw new IOException($"Could not read the fleet settings in {ConfigPath}: {ex.Message}", ex);
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Saves the config atomically: a reader sees either the old file or the new one, never a partial one.</summary>
    private static void Write(FleetConfig config)
    {
        Directory.CreateDirectory(StateDir);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        // One temporary file per process, so the dashboard and the agent never write the same one.
        var temp = $"{ConfigPath}.{Environment.ProcessId}.tmp";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, ConfigPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5) { Thread.Sleep(100); }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }
    }

    /// <summary>
    /// Applies a change to the on-disk config and saves it (retrying briefly if the other process is writing).
    /// Throws when the existing file cannot be read, instead of replacing it with defaults.
    /// </summary>
    public static void Update(Action<FleetConfig> change)
    {
        lock (Gate)
        {
            var config = Read(out _);
            change(config);
            Write(config);
            _cached = config;
            _cachedStamp = File.GetLastWriteTimeUtc(ConfigPath);
            _readFailedUtc = null;
        }
    }

    public static void SetManuallyStopped(string key, bool stopped)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var already = Current.ManuallyStopped.Contains(key, StringComparer.OrdinalIgnoreCase);
        if (already == stopped) return;
        Update(c =>
        {
            c.ManuallyStopped.RemoveAll(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (stopped) c.ManuallyStopped.Add(key);
        });
    }

    // ----- Fleet key -----

    private sealed record DecryptedKey(string Source, byte[]? Key);
    private DecryptedKey? _decryptedKey;

    /// <summary>
    /// The decrypted pairing code, or null. Never written to fleet.json. Decrypted once per instance (a new
    /// instance is loaded when the file changes); each caller gets its own copy.
    /// </summary>
    [JsonIgnore]
    public byte[]? FleetKey
    {
        get
        {
            var source = ProtectedFleetKey;
            var cached = _decryptedKey;
            if (cached == null || !ReferenceEquals(cached.Source, source))
                _decryptedKey = cached = new DecryptedKey(source, Unprotect(source));
            return (byte[]?)cached.Key?.Clone();
        }
    }

    private static byte[]? Unprotect(string protectedKey)
    {
        if (string.IsNullOrEmpty(protectedKey)) return null;
        try { return ProtectedData.Unprotect(Convert.FromBase64String(protectedKey), null, DataProtectionScope.CurrentUser); }
        catch { return null; }
    }

    public static void SetFleetKey(byte[]? key) => Update(c =>
        c.ProtectedFleetKey = key == null ? string.Empty
            : Convert.ToBase64String(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser)));
}

/// <summary>The fleet pairing code: 32 random bytes, shown as "rwk_…" so it can be copied between PCs.</summary>
public static class CommandSigner
{
    private const string KeyPrefix = "rwk_";

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    public static string FormatKey(byte[] key)
        => KeyPrefix + Convert.ToBase64String(key).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? ParseKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        if (!value.StartsWith(KeyPrefix, StringComparison.Ordinal)) return null;
        value = value[KeyPrefix.Length..].Replace('-', '+').Replace('_', '/');
        value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length == 32 ? bytes : null;
        }
        catch { return null; }
    }

    /// <summary>Short public fingerprint of a key, sent in status answers so dashboards can spot mismatched keys.</summary>
    public static string KeyId(byte[]? key)
        => key == null ? string.Empty : Convert.ToHexString(SHA256.HashData(key))[..8].ToLowerInvariant();
}

public sealed class PeerRecord
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
}
