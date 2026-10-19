using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RunnerWidget;

/// <summary>
/// Settings shared by the dashboard and the fleet agent on one PC (both run as the same Windows user),
/// stored in %LOCALAPPDATA%\RunnerWidget\fleet.json. Re-read automatically when the file changes.
/// </summary>
public sealed class FleetConfig
{
    public const string DefaultRelayRepository = "Sandiozo/runner-widget";
    public static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RunnerWidget");
    private static readonly string ConfigPath = Path.Combine(StateDir, "fleet.json");
    private static readonly object Gate = new();
    private static FleetConfig? _cached;
    private static DateTime _cachedStamp;

    /// <summary>Repository whose GitHub Releases Runner Manager installs updates from (the property name predates releases).</summary>
    public string RelayRepository { get; set; } = DefaultRelayRepository;
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

    public static FleetConfig Current
    {
        get
        {
            lock (Gate)
            {
                var stamp = File.Exists(ConfigPath) ? File.GetLastWriteTimeUtc(ConfigPath) : DateTime.MinValue;
                if (_cached != null && stamp == _cachedStamp) return _cached;
                _cached = Load();
                _cachedStamp = stamp;
                return _cached;
            }
        }
    }

    private static FleetConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var config = JsonSerializer.Deserialize<FleetConfig>(File.ReadAllText(ConfigPath)) ?? new FleetConfig();
                if (!IsValidRepository(config.RelayRepository)) config.RelayRepository = DefaultRelayRepository;
                return config;
            }
        }
        catch { }
        return new FleetConfig();
    }

    /// <summary>Applies a change to the on-disk config and saves it (retrying briefly if the other process is writing).</summary>
    public static void Update(Action<FleetConfig> change)
    {
        lock (Gate)
        {
            var config = Load();
            change(config);
            Directory.CreateDirectory(StateDir);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
                    break;
                }
                catch (IOException) when (attempt < 5) { Thread.Sleep(100); }
            }
            _cached = config;
            _cachedStamp = File.GetLastWriteTimeUtc(ConfigPath);
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

    public static bool IsValidRepository(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0 && p.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
    }

    // ----- Fleet key -----

    public byte[]? FleetKey
    {
        get
        {
            if (string.IsNullOrEmpty(ProtectedFleetKey)) return null;
            try { return ProtectedData.Unprotect(Convert.FromBase64String(ProtectedFleetKey), null, DataProtectionScope.CurrentUser); }
            catch { return null; }
        }
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

    /// <summary>Short public fingerprint of a key, published so dashboards can spot mismatched keys.</summary>
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
