using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
    private static readonly TimeSpan FileLockTimeout = TimeSpan.FromSeconds(10);
    /// <summary>
    /// Serializes changes to fleet.json between processes: the dashboard and the agent (both elevated) and AI
    /// assistant tools (not elevated) all change it, as the same user.
    /// </summary>
    private static readonly Lazy<Mutex?> FileMutex = new(CreateFileMutex);

    /// <summary>The fleet pairing code, encrypted for this Windows user with DPAPI.</summary>
    public string ProtectedFleetKey { get; set; } = string.Empty;
    /// <summary>Other PCs in the fleet and where they were last reached.</summary>
    public List<PeerRecord> KnownPeers { get; set; } = new();
    /// <summary>Addresses added by hand, for PCs that broadcast discovery can't find (e.g. over Tailscale).</summary>
    public List<string> ManualAddresses { get; set; } = new();
    /// <summary>Restart runners automatically when they stop unexpectedly.</summary>
    public bool Watchdog { get; set; } = true;
    /// <summary>Runner service names the user stopped on purpose, so the watchdog leaves them alone.</summary>
    public List<string> ManuallyStopped { get; set; } = new();
    /// <summary>Runner folders found outside C:\GitHub-Runners (console runners), so they stay listed while stopped.</summary>
    public List<string> KnownRunnerRoots { get; set; } = new();
    /// <summary>Where new and moved runners are installed on this PC; empty means C:\GitHub-Runners.</summary>
    public string RunnerFolder { get; set; } = string.Empty;
    /// <summary>What AI assistants may do through Runner Manager (Settings → AI assistants).</summary>
    public AssistantOptions Assistant { get; set; } = new();
    /// <summary>
    /// This PC's own random ID, made once. Other PCs use it to recognize this PC after it was renamed, and to tell
    /// two PCs with the same name apart. Use <see cref="LocalMachineId"/>.
    /// </summary>
    public string MachineId { get; set; } = string.Empty;

    private static string? _machineId;

    /// <summary>
    /// This PC's machine ID, created on first use; empty if fleet.json can't be saved. Once known it stays the same for
    /// this process: a Runner Manager before 2.4 that saves fleet.json drops the ID from the file, and it is written back.
    /// </summary>
    public static string LocalMachineId
    {
        get
        {
            var known = _machineId;
            var id = Current.MachineId;
            if (known != null)
            {
                if (id.Length == 0)
                {
                    try { Update(c => { if (c.MachineId.Length == 0) c.MachineId = known; }); }
                    catch (IOException) { }
                }
                return known;
            }
            if (id.Length == 0)
            {
                try { Update(c => { if (c.MachineId.Length == 0) c.MachineId = Guid.NewGuid().ToString("N"); }); }
                catch (IOException) { return string.Empty; }
                id = Current.MachineId;
            }
            if (id.Length > 0) _machineId = id;
            return id;
        }
    }

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
                            WithFileLock(() =>
                            {
                                _cached = Read(out _);
                                Write(_cached);
                            });
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
            string? json = null;
            try
            {
                hasPlainKey = false;
                if (!File.Exists(ConfigPath)) return new FleetConfig();
                json = File.ReadAllText(ConfigPath);
                using (var document = JsonDocument.Parse(json))
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Null) return new FleetConfig();
                    hasPlainKey = document.RootElement.ValueKind == JsonValueKind.Object
                                  && document.RootElement.TryGetProperty(nameof(FleetKey), out _);
                }
                var config = JsonSerializer.Deserialize<FleetConfig>(json, JsonOptions) ?? new FleetConfig();
                config.Normalize();
                return config;
            }
            catch (JsonException) when (attempt >= ReadAttempts - 1)
            {
                // Writes are atomic, so a file that stays invalid is damaged rather than half-written. Keep a copy for
                // inspection and continue with what can still be read, instead of blocking every later save.
                hasPlainKey = false;
                try { File.Move(ConfigPath, $"{ConfigPath}.damaged-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return Salvage(json);
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
            WithFileLock(() =>
            {
                var config = Read(out _);
                change(config);
                Write(config);
                _cached = config;
                _cachedStamp = File.GetLastWriteTimeUtc(ConfigPath);
                _readFailedUtc = null;
            });
        }
    }

    /// <summary>The name of the mutex that guards fleet.json: one per settings file, so other users and tests don't share it.</summary>
    internal static string FileLockName
        => @"Global\RunnerManagerFleetConfig-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ConfigPath.ToUpperInvariant())))[..16];

    private static Mutex? CreateFileMutex()
    {
        // Global, so an agent in another session of the same user shares it. The default security of an elevated
        // process grants only Administrators and SYSTEM, which locks out the user's processes without administrator
        // rights, so grant this user (elevated or not) and SYSTEM access explicitly. Objects created elevated get no
        // high integrity label, so the DACL is all that matters.
        try
        {
            var user = WindowsIdentity.GetCurrent().User!.Value;
            var security = new MutexSecurity();
            security.SetSecurityDescriptorSddlForm($"D:(A;;GA;;;{user})(A;;GA;;;SY)");
            return MutexAcl.Create(initiallyOwned: false, FileLockName, out _, security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException or ArgumentException)
        {
            // Without the lock, a change made at the same moment in another process can be lost; the file stays whole.
            try { return Mutex.OpenExisting(FileLockName); }
            catch { return null; }
        }
    }

    /// <summary>Runs a read-modify-write of fleet.json while holding the cross-process lock (or after waiting for it in vain).</summary>
    private static void WithFileLock(Action action)
    {
        var mutex = FileMutex.Value;
        var owned = false;
        if (mutex != null)
        {
            try { owned = mutex.WaitOne(FileLockTimeout); }
            // The process that held it ended without releasing it. Writes are atomic, so the file is still whole.
            catch (AbandonedMutexException) { owned = true; }
        }
        try { action(); }
        finally
        {
            if (owned) mutex!.ReleaseMutex();
        }
    }

    /// <summary>A setting's string value, found in text that is not valid JSON.</summary>
    private static string? StringValue(string json, string name)
        => Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([A-Za-z0-9+/=]+)\"") is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>
    /// What can still be read from a damaged fleet.json: every setting that reads on its own, or at least the pairing
    /// code, so one bad value doesn't take this PC out of the fleet.
    /// </summary>
    internal static FleetConfig Salvage(string? json)
    {
        var config = new FleetConfig();
        if (string.IsNullOrWhiteSpace(json)) return config;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return config;
            foreach (var property in typeof(FleetConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanWrite || property.IsDefined(typeof(JsonIgnoreAttribute))
                    || !document.RootElement.TryGetProperty(property.Name, out var value)) continue;
                try { property.SetValue(config, value.Deserialize(property.PropertyType, JsonOptions)); }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { }
            }
        }
        catch (JsonException)
        {
            // Not even valid JSON (cut off, for example): the pairing code may still be in it.
            config.ProtectedFleetKey = StringValue(json, nameof(ProtectedFleetKey)) ?? string.Empty;
            config.MachineId = StringValue(json, nameof(MachineId)) ?? string.Empty;
        }
        config.Normalize();
        return config;
    }

    /// <summary>Replaces nulls from a hand-edited file (for example "Assistant": null) with defaults.</summary>
    private void Normalize()
    {
        ProtectedFleetKey ??= string.Empty;
        ProtectedPendingFleetKey ??= string.Empty;
        KnownPeers ??= new();
        KnownPeers.RemoveAll(p => p == null);
        foreach (var peer in KnownPeers)
        {
            peer.Name ??= string.Empty;
            peer.Address ??= string.Empty;
            peer.Version ??= string.Empty;
            peer.MachineId ??= string.Empty;
        }
        KnownPeers.RemoveAll(p => p.Name.Length == 0);
        ManualAddresses ??= new();
        ManualAddresses.RemoveAll(string.IsNullOrWhiteSpace);
        ManuallyStopped ??= new();
        // "rdc" was Remote Desktop Commander, which Runner Manager no longer manages (2.6).
        ManuallyStopped.RemoveAll(k => string.IsNullOrWhiteSpace(k) || k.Equals("rdc", StringComparison.OrdinalIgnoreCase));
        KnownRunnerRoots ??= new();
        KnownRunnerRoots.RemoveAll(string.IsNullOrWhiteSpace);
        RunnerFolder ??= string.Empty;
        MachineId ??= string.Empty;
        Assistant ??= new();
        Assistant.ProtectedHttpToken ??= string.Empty;
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
    {
        c.ProtectedFleetKey = Protect(key);
        c.ProtectedPendingFleetKey = string.Empty;
    });

    private static string Protect(byte[]? key)
        => key == null ? string.Empty : Convert.ToBase64String(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));

    /// <summary>
    /// A new pairing code sent by "Change fleet code" on another PC, encrypted like the pairing code. The agent
    /// accepts it next to the current code until the first request that uses it; then it replaces the current code.
    /// </summary>
    public string ProtectedPendingFleetKey { get; set; } = string.Empty;

    [JsonIgnore]
    public byte[]? PendingFleetKey => Unprotect(ProtectedPendingFleetKey);

    public static void SetPendingFleetKey(byte[] key) => Update(c => c.ProtectedPendingFleetKey = Protect(key));

    /// <summary>
    /// Makes the pending code the pairing code, if it still is <paramref name="key"/> (in fleet.json, or the copy the agent
    /// kept in memory). The old code stops working.
    /// </summary>
    public static void UsePendingFleetKey(byte[] key, byte[]? remembered = null) => Update(c =>
    {
        var pending = c.PendingFleetKey ?? remembered;
        if (pending == null || !CryptographicOperations.FixedTimeEquals(pending, key)) return;
        c.ProtectedFleetKey = Protect(key);
        c.ProtectedPendingFleetKey = string.Empty;
    });
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

/// <summary>AI assistant access: off by default; reading is always allowed once on, changes follow these rules.</summary>
public sealed class AssistantOptions
{
    public bool Enabled { get; set; }
    /// <summary>Ask before an assistant creates, starts, stops or restarts a runner. Moving and removing always ask.</summary>
    public bool AskBeforeChanges { get; set; } = true;
    /// <summary>Also serve MCP over HTTP on this PC only (127.0.0.1), for AI tools that connect to a URL.</summary>
    public bool HttpEnabled { get; set; }
    /// <summary>The HTTP endpoint's access token, encrypted for this Windows user with DPAPI.</summary>
    public string ProtectedHttpToken { get; set; } = string.Empty;

    [JsonIgnore]
    public string HttpToken
    {
        get
        {
            if (string.IsNullOrEmpty(ProtectedHttpToken)) return string.Empty;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedHttpToken), null, DataProtectionScope.CurrentUser)); }
            catch { return string.Empty; }
        }
    }

    public static string ProtectToken(string token)
        => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
}

public sealed class PeerRecord
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
    /// <summary>The PC's own ID (see <see cref="FleetConfig.MachineId"/>); empty for PCs with an older agent.</summary>
    public string MachineId { get; set; } = string.Empty;
}
