using System.IO;
using System.Diagnostics;
using System.Management;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RunnerManager;

public static class RemoteDesktopManager
{
    private static readonly string StateDir = FleetConfig.StateDir;
    private static readonly string ActivityLogPath = Path.Combine(StateDir, "rdc-activity.log");
    private static readonly string RuntimeLogPath = Path.Combine(StateDir, "rdc-runtime.log");
    public static string RuntimeLogFile => RuntimeLogPath;
    private static readonly string LauncherScriptPath = Path.Combine(StateDir, "rdc-launch.ps1");
    // One shared client: creating an HttpClient per probe (every few seconds) leaks sockets over time.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static (DateTime FetchedUtc, string Url, string Key)? _relayBackend;
    /// <summary>Status snapshots run every few seconds; the relay is asked at most this often.</summary>
    private static readonly TimeSpan RelayProbeCacheTime = TimeSpan.FromSeconds(30);
    private static (DateTime ProbedUtc, RemoteRelayProbe Result)? _relayProbe;
    /// <summary>Only the end of the runtime log is read; the launcher keeps the log under about 1 MB.</summary>
    private const int RuntimeLogTailBytes = 256 * 1024;
    public static bool IsConfigured()
    {
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".desktop-commander-device", "device.json");
        try { return File.Exists(config) && new FileInfo(config).Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// The relay's view of this PC. Skipped when Remote Desktop Commander is not running, and cached for
    /// <see cref="RelayProbeCacheTime"/>, so a status snapshot does not make an HTTPS call every time.
    /// </summary>
    public static async Task<RemoteRelayProbe> ProbeRelayAsync()
    {
        var cached = _relayProbe;
        if (cached != null && DateTime.UtcNow - cached.Value.ProbedUtc < RelayProbeCacheTime) return cached.Value.Result;
        var running = await Task.Run(() =>
        {
            try { return ReadProcesses().Any(IsRemoteDesktopCommanderProcess); }
            catch { return true; }
        });
        var result = running
            ? await QueryRelayAsync()
            : new RemoteRelayProbe { Known = true, Connected = false, Detail = "Relay offline. Remote Desktop Commander is not running." };
        _relayProbe = (DateTime.UtcNow, result);
        return result;
    }

    /// <summary>Forgets the cached relay status, after Remote Desktop Commander was started or stopped.</summary>
    private static void ForgetRelayProbe() => _relayProbe = null;

    private static async Task<RemoteRelayProbe> QueryRelayAsync()
    {
        try
        {
            var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".desktop-commander-device", "device.json");
            if (!File.Exists(configPath))
                return new RemoteRelayProbe { Known = true, Connected = false, Detail = "Relay not configured" };

            using var configJson = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            var root = configJson.RootElement;
            var deviceId = root.TryGetProperty("deviceId", out var deviceIdElement)
                ? deviceIdElement.GetString()
                : null;
            var accessToken = root.TryGetProperty("session", out var session)
                && session.ValueKind == System.Text.Json.JsonValueKind.Object
                && session.TryGetProperty("access_token", out var tokenElement)
                    ? tokenElement.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(accessToken))
                return new RemoteRelayProbe { Known = false, Detail = "Relay authentication unavailable" };

            var backend = _relayBackend;
            if (backend == null || DateTime.UtcNow - backend.Value.FetchedUtc > TimeSpan.FromMinutes(30))
            {
                using var infoResponse = await Http.GetAsync("https://mcp.desktopcommander.app/api/mcp-info");
                if (!infoResponse.IsSuccessStatusCode)
                    return new RemoteRelayProbe { Known = false, Detail = $"Could not read the relay status (HTTP {(int)infoResponse.StatusCode})" };

                using var infoJson = System.Text.Json.JsonDocument.Parse(await infoResponse.Content.ReadAsStringAsync());
                var infoRoot = infoJson.RootElement;
                var url = infoRoot.TryGetProperty("supabaseUrl", out var urlElement) ? urlElement.GetString() : null;
                var key = infoRoot.TryGetProperty("supabasePublishableKey", out var keyElement) ? keyElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
                    return new RemoteRelayProbe { Known = false, Detail = "Relay backend configuration unavailable" };
                backend = (DateTime.UtcNow, url, key);
                _relayBackend = backend;
            }
            var supabaseUrl = backend.Value.Url;
            var anonKey = backend.Value.Key;

            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{supabaseUrl.TrimEnd('/')}/rest/v1/mcp_devices?id=eq.{Uri.EscapeDataString(deviceId)}&select=status,last_seen");
            request.Headers.TryAddWithoutValidation("apikey", anonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return new RemoteRelayProbe { Known = false, Detail = $"Could not read the relay status (HTTP {(int)response.StatusCode})" };

            using var deviceJson = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (deviceJson.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array
                || deviceJson.RootElement.GetArrayLength() == 0)
                return new RemoteRelayProbe { Known = true, Connected = false, Detail = "This PC is not registered with the relay" };

            var row = deviceJson.RootElement[0];
            var status = row.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
            DateTime? lastSeen = null;
            if (row.TryGetProperty("last_seen", out var seenElement)
                && DateTime.TryParse(seenElement.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                lastSeen = parsed.ToUniversalTime();

            var connected = string.Equals(status, "online", StringComparison.OrdinalIgnoreCase);
            return new RemoteRelayProbe
            {
                Known = true,
                Connected = connected,
                ChangedUtc = lastSeen,
                Detail = connected ? "Relay connected" : "Relay offline"
            };
        }
        catch
        {
            return new RemoteRelayProbe { Known = false, Detail = "Relay status unavailable" };
        }
    }

    public static RemoteInfo ReadSnapshot()
    {
        var matches = ReadProcesses().Where(IsRemoteDesktopCommanderProcess).ToList();
        var activity = ReadActivitySnapshot();
        var connection = ReadConnectionSnapshot();
        var security = ReadSecurityStatus();
        if (matches.Count == 0)
            return new RemoteInfo
            {
                Running = false,
                Connected = false,
                ConnectionKnown = true,
                ConnectionChangedUtc = connection.ChangedUtc,
                ConnectionText = "Relay offline. Remote Desktop Commander is not running.",
                ActiveCalls = 0,
                LastUsedUtc = activity.LastUsedUtc,
                LastTool = activity.LastTool,
                SecurityText = security
            };
        var started = matches.Select(p => TryParseWmiDate(p.CreationDate))
            .Where(d => d.HasValue).Select(d => d!.Value).DefaultIfEmpty().Min();
        return new RemoteInfo
        {
            Running = true,
            Connected = connection.Connected,
            ConnectionKnown = connection.Known,
            ConnectionChangedUtc = connection.ChangedUtc,
            ConnectionText = connection.Known
                ? connection.Detail
                : "Relay status unknown. Remote Desktop Commander was not started by Runner Manager.",
            Pids = matches.Select(p => p.ProcessId).Distinct().OrderBy(p => p).ToArray(),
            Started = started == default ? null : started,
            ActiveCalls = activity.ActiveCalls,
            LastUsedUtc = activity.LastUsedUtc,
            LastTool = activity.LastTool,
            SecurityText = security
        };
    }

    public static async Task StartAsync()
    {
        var current = ReadSnapshot();
        if (current.Running && current.Connected) return;
        if (current.Running) await StopAsync();
        await Task.Run(() => LaunchRemote(visible: false));
        await WaitForConnectedAsync(TimeSpan.FromSeconds(30));
    }

    public static async Task SetupAsync()
    {
        if (IsConfigured())
        {
            await StartAsync();
            return;
        }
        await Task.Run(() => LaunchRemote(visible: true));
    }

    public static Task StopAsync() => Task.Run(() =>
    {
        // npx can respawn its child briefly after the parent dies, so require a few clean checks in a row.
        var cleanChecks = 0;
        for (var attempt = 0; attempt < 10 && cleanChecks < 3; attempt++)
        {
            var matches = ReadProcesses().Where(IsRemoteDesktopCommanderProcess).ToList();
            if (matches.Count == 0)
            {
                cleanChecks++;
                Thread.Sleep(400);
                continue;
            }
            cleanChecks = 0;
            var ids = matches.Select(p => p.ProcessId).ToHashSet();
            foreach (var info in matches.Where(p => !ids.Contains(p.ParentProcessId)))
            {
                try
                {
                    using var process = Process.GetProcessById((int)info.ProcessId);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            Thread.Sleep(400);
        }
        ForgetRelayProbe();
    });

    public static async Task RestartAsync()
    {
        await StopAsync();
        await Task.Delay(900);
        await StartAsync();
    }

    private static void LaunchRemote(bool visible)
    {
        ForgetRelayProbe();
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(ActivityLogPath, string.Empty);
        File.WriteAllText(RuntimeLogPath, string.Empty);
        EnsureLauncherScript();
        var npx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npx.cmd");
        if (!File.Exists(npx)) npx = "npx.cmd";
        var psi = new ProcessStartInfo("powershell.exe")
        {
            // Not the install folder: a long-running process there would keep setup from moving or removing it.
            WorkingDirectory = StateDir,
            UseShellExecute = false,
            CreateNoWindow = !visible,
            WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
        };
        foreach (var arg in new[]
        {
            "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", LauncherScriptPath,
            "-Npx", npx, "-ActivityLog", ActivityLogPath, "-RuntimeLog", RuntimeLogPath
        })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi);
    }

    private static void EnsureLauncherScript()
    {
        const string script = """
param([string]$Npx, [string]$ActivityLog, [string]$RuntimeLog)
$lineCount = 0
& $Npx '--yes' '@wonderwhy-er/desktop-commander@latest' 'remote' 2>&1 | ForEach-Object {
    $line = $_.ToString()
    Write-Host $line
    $ts = [DateTime]::UtcNow.ToString('O')
    $safeLine = $line.Replace("`r", ' ').Replace("`n", ' ')
    Add-Content -LiteralPath $RuntimeLog -Value ($ts + '|' + $safeLine)
    # Keep the log small: when it is over 1 MB, keep only its last 256 KB (from the start of a line).
    $lineCount++
    if ($lineCount % 200 -eq 0) {
        try {
            $stream = [System.IO.File]::Open($RuntimeLog, 'Open', 'ReadWrite', 'ReadWrite')
            try {
                if ($stream.Length -gt 1MB) {
                    $keep = New-Object byte[] 256KB
                    [void]$stream.Seek(-$keep.Length, 'End')
                    $read = $stream.Read($keep, 0, $keep.Length)
                    $start = [Array]::IndexOf($keep, [byte]10) + 1
                    $stream.SetLength(0)
                    $stream.Write($keep, $start, $read - $start)
                }
            } finally { $stream.Dispose() }
        } catch { }
    }
    if ($line -match 'Received tool call ([^:]+): ([^ ]+)') {
        Add-Content -LiteralPath $ActivityLog -Value ($ts + '|START|' + $Matches[1] + '|' + $Matches[2])
    } elseif ($line -match 'Tool call ([^ ]+) completed') {
        Add-Content -LiteralPath $ActivityLog -Value ($ts + '|END||' + $Matches[1])
    } elseif ($line -match 'Tool call ([^ ]+) failed') {
        Add-Content -LiteralPath $ActivityLog -Value ($ts + '|FAIL||' + $Matches[1])
    }
}
""";
        File.WriteAllText(LauncherScriptPath, script);
    }

    private static async Task WaitForConnectedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = ReadSnapshot();
            // Ask the relay directly: the cached status is from before the start.
            var relay = await QueryRelayAsync();
            if (relay.Known && relay.Connected)
            {
                _relayProbe = (DateTime.UtcNow, relay);
                return;
            }
            if (!snapshot.Running && snapshot.ConnectionKnown)
                throw new InvalidOperationException("Remote Desktop Commander stopped before the relay connected. Check the Remote Desktop Commander log.");
            await Task.Delay(500);
        }

        var final = ReadSnapshot();
        var finalRelay = await QueryRelayAsync();
        var detail = finalRelay.Known
            ? finalRelay.Detail
            : string.IsNullOrWhiteSpace(final.ConnectionText)
                ? "The relay did not confirm the connection."
                : final.ConnectionText;
        throw new InvalidOperationException($"Remote Desktop Commander did not connect to the relay within {(int)timeout.TotalSeconds} seconds. {detail}");
    }

    private static RemoteConnectionInfo ReadConnectionSnapshot()
    {
        var info = new RemoteConnectionInfo();
        try
        {
            if (!File.Exists(RuntimeLogPath)) return info;
            foreach (var line in ReadTailLines(RuntimeLogPath, RuntimeLogTailBytes))
            {
                var parts = line.Split('|', 2);
                if (parts.Length != 2) continue;
                if (!DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)) continue;
                var message = parts[1];
                if (message.Contains("Device ready:", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("Device marked as online", StringComparison.OrdinalIgnoreCase))
                {
                    info = new RemoteConnectionInfo
                    {
                        Known = true,
                        Connected = true,
                        ChangedUtc = when.ToUniversalTime(),
                        Detail = "Relay connected"
                    };
                }
                else if (message.Contains("Device marked as offline", StringComparison.OrdinalIgnoreCase)
                         || message.Contains("Device startup failed:", StringComparison.OrdinalIgnoreCase))
                {
                    info = new RemoteConnectionInfo
                    {
                        Known = true,
                        Connected = false,
                        ChangedUtc = when.ToUniversalTime(),
                        Detail = message.Contains("startup failed", StringComparison.OrdinalIgnoreCase)
                            ? "Relay startup failed"
                            : "Relay disconnected"
                    };
                }
            }
        }
        catch { }
        return info;
    }

    /// <summary>The complete lines in the last <paramref name="maxBytes"/> of a file that another process is writing.</summary>
    private static IEnumerable<string> ReadTailLines(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var skipPartialLine = stream.Length > maxBytes;
        if (skipPartialLine) stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        // Starting in the middle of the file lands inside a line; skip that partial line.
        if (skipPartialLine) reader.ReadLine();
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    private static RemoteActivityInfo ReadActivitySnapshot()
    {
        var active = 0;
        DateTime? lastUsed = null;
        var lastTool = string.Empty;
        try
        {
            if (!File.Exists(ActivityLogPath)) return new RemoteActivityInfo();
            foreach (var line in File.ReadLines(ActivityLogPath))
            {
                var parts = line.Split('|', 4);
                if (parts.Length < 4 || !DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)) continue;
                if (parts[1] == "START") active++;
                else if (parts[1] is "END" or "FAIL") active = Math.Max(0, active - 1);
                lastUsed = when.ToUniversalTime();
                if (!string.IsNullOrWhiteSpace(parts[3])) lastTool = parts[3];
            }
        }
        catch { }
        return new RemoteActivityInfo { ActiveCalls = active, LastUsedUtc = lastUsed, LastTool = lastTool };
    }

    private static string ReadSecurityStatus()
    {
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".desktop-commander-device", "device.json");
        if (!File.Exists(config)) return "Security: Not set up";
        try
        {
            var acl = FileSystemAclExtensions.GetAccessControl(new FileInfo(config));
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
            foreach (var rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (sid.IsWellKnown(WellKnownSidType.WorldSid) || sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid) || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid))
                    return "Security: Review file permissions";
            }
            return "Security: Protected by Windows permissions";
        }
        catch { return "Security: Could not check permissions"; }
    }

    private static List<ProcessInfo> ReadProcesses()
    {
        var result = new List<ProcessInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, Name, CommandLine, CreationDate FROM Win32_Process " +
            "WHERE CommandLine LIKE '%desktop-commander%' OR CommandLine LIKE '%rdc-launch.ps1%'");
        foreach (ManagementObject process in searcher.Get())
        {
            result.Add(new ProcessInfo
            {
                ProcessId = ConvertToUInt(process["ProcessId"]),
                ParentProcessId = ConvertToUInt(process["ParentProcessId"]),
                Name = process["Name"]?.ToString() ?? string.Empty,
                CommandLine = process["CommandLine"]?.ToString() ?? string.Empty,
                CreationDate = process["CreationDate"]?.ToString() ?? string.Empty
            });
        }
        return result;
    }

    private static bool IsRemoteDesktopCommanderProcess(ProcessInfo process)
    {
        var command = process.CommandLine;
        return (command.Contains("desktop-commander", StringComparison.OrdinalIgnoreCase)
                && command.Contains("remote", StringComparison.OrdinalIgnoreCase))
               || command.Contains("rdc-launch.ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime? TryParseWmiDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(value); }
        catch { return null; }
    }

    private static uint ConvertToUInt(object? value)
    {
        try { return value == null ? 0 : Convert.ToUInt32(value); }
        catch { return 0; }
    }
}

public sealed class RemoteInfo
{
    public bool Running { get; init; }
    public bool Connected { get; init; }
    public bool ConnectionKnown { get; init; }
    public DateTime? ConnectionChangedUtc { get; init; }
    public string ConnectionText { get; init; } = string.Empty;
    public uint[] Pids { get; init; } = Array.Empty<uint>();
    public DateTime? Started { get; init; }
    public int ActiveCalls { get; init; }
    public DateTime? LastUsedUtc { get; init; }
    public string LastTool { get; init; } = string.Empty;
    public string SecurityText { get; init; } = string.Empty;
}

public sealed class RemoteConnectionInfo
{
    public bool Known { get; init; }
    public bool Connected { get; init; }
    public DateTime? ChangedUtc { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public sealed class RemoteRelayProbe
{
    public bool Known { get; init; }
    public bool Connected { get; init; }
    public DateTime? ChangedUtc { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public sealed class RemoteActivityInfo
{
    public int ActiveCalls { get; init; }
    public DateTime? LastUsedUtc { get; init; }
    public string LastTool { get; init; } = string.Empty;
}
