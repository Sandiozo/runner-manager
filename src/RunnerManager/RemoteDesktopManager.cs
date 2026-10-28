using System.IO;
using System.Diagnostics;
using System.Management;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;

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
    private const string PackageName = "@wonderwhy-er/desktop-commander";
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$");

    // Status snapshots, the watchdog and commands run on different threads. The caches are immutable records, so a
    // reader always sees a whole one (never half of an old and half of a new value).
    private sealed record RelayBackend(DateTime FetchedUtc, string Url, string Key);
    private sealed record CachedProbe(DateTime ProbedUtc, RemoteRelayProbe Result);
    private static RelayBackend? _relayBackend;
    /// <summary>Status snapshots run every few seconds; the relay is asked at most this often.</summary>
    private static readonly TimeSpan RelayProbeCacheTime = TimeSpan.FromSeconds(30);
    private static CachedProbe? _relayProbe;
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
        var cached = Volatile.Read(ref _relayProbe);
        if (cached != null && DateTime.UtcNow - cached.ProbedUtc < RelayProbeCacheTime) return cached.Result;
        var running = await Task.Run(() =>
        {
            try { return FindProcesses().Count > 0; }
            catch { return true; }
        });
        var result = running
            ? await QueryRelayAsync()
            : new RemoteRelayProbe { Known = true, Connected = false, Detail = "Relay offline. Remote Desktop Commander is not running." };
        Volatile.Write(ref _relayProbe, new CachedProbe(DateTime.UtcNow, result));
        return result;
    }

    /// <summary>Forgets the cached relay status, after Remote Desktop Commander was started or stopped.</summary>
    private static void ForgetRelayProbe() => Volatile.Write(ref _relayProbe, null);

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

            var backend = Volatile.Read(ref _relayBackend);
            if (backend == null || DateTime.UtcNow - backend.FetchedUtc > TimeSpan.FromMinutes(30))
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
                backend = new RelayBackend(DateTime.UtcNow, url, key);
                Volatile.Write(ref _relayBackend, backend);
            }
            var supabaseUrl = backend.Url;
            var anonKey = backend.Key;

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
        var matches = FindProcesses();
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
        var package = await PackageAsync(update: false);
        await Task.Run(() => LaunchRemote(package, visible: false));
        await WaitForConnectedAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>Sets it up, or when it is set up already, starts it again with its newest version.</summary>
    public static async Task SetupAsync()
    {
        var previous = $"{PackageName}@{FleetConfig.Current.RdcVersion}";
        var package = await PackageAsync(update: true);
        if (IsConfigured())
        {
            var current = ReadSnapshot();
            if (current.Running && current.Connected && package == previous) return;
            if (current.Running) await StopAsync();
            await Task.Run(() => LaunchRemote(package, visible: false));
            await WaitForConnectedAsync(TimeSpan.FromSeconds(30));
            return;
        }
        await Task.Run(() => LaunchRemote(package, visible: true));
    }

    /// <summary>
    /// The package to run, with a fixed version. The agent runs it with administrator rights on every PC, so it does
    /// not take a newly published release each time it restarts it: the version is chosen when the user sets it up
    /// (or runs setup again, with <paramref name="update"/>). A PC set up before versions were kept stays on the
    /// version already in its npm cache.
    /// </summary>
    private static async Task<string> PackageAsync(bool update)
    {
        var pinned = FleetConfig.Current.RdcVersion;
        var version = !update && VersionPattern.IsMatch(pinned) ? pinned
            : (update ? null : CachedVersion()) ?? await LatestVersionAsync(fallback: VersionPattern.IsMatch(pinned) ? pinned : null);
        if (version != pinned) FleetConfig.Update(c => c.RdcVersion = version);
        return $"{PackageName}@{version}";
    }

    /// <summary>The newest release on npm, or <paramref name="fallback"/> when npm can't be reached.</summary>
    private static async Task<string> LatestVersionAsync(string? fallback)
    {
        try
        {
            using var response = await Http.GetAsync($"https://registry.npmjs.org/{PackageName}/latest");
            response.EnsureSuccessStatusCode();
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var version = json.RootElement.TryGetProperty("version", out var value) ? value.GetString() : null;
            if (version != null && VersionPattern.IsMatch(version)) return version;
            throw new InvalidDataException("npm returned no version.");
        }
        catch (Exception ex) when (fallback == null && ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not look up the current Remote Desktop Commander version on npm: {ex.Message}");
        }
        catch (Exception) when (fallback != null) { return fallback; }
    }

    /// <summary>The newest version npx already downloaded (in %LOCALAPPDATA%\npm-cache\_npx), if any.</summary>
    private static string? CachedVersion()
    {
        try
        {
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache", "_npx");
            if (!Directory.Exists(cache)) return null;
            return Directory.EnumerateDirectories(cache)
                .Select(d => Path.Combine(d, "node_modules", "@wonderwhy-er", "desktop-commander", "package.json"))
                .Where(File.Exists)
                .Select(file =>
                {
                    try
                    {
                        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                        return json.RootElement.TryGetProperty("version", out var value) ? value.GetString() : null;
                    }
                    catch { return null; }
                })
                .Where(v => v != null && VersionPattern.IsMatch(v))
                .OrderByDescending(v => Version.TryParse(v!.Split('-')[0], out var parsed) ? parsed : new Version())
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// npx from a Node.js install in Program Files, which only administrators can change. The agent runs with
    /// administrator rights, so an npx found on the PATH (often in folders the user can write to) is not used.
    /// </summary>
    private static string TrustedNpx()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(folder);
            if (root.Length == 0) continue;
            var npx = Path.Combine(root, "nodejs", "npx.cmd");
            if (File.Exists(npx)) return npx;
        }
        throw new InvalidOperationException("Node.js was not found in Program Files. Install it from nodejs.org with its standard installer, then try again.");
    }

    public static Task StopAsync() => Task.Run(() =>
    {
        // npx can respawn its child briefly after the parent dies, so require a few clean checks in a row.
        var cleanChecks = 0;
        for (var attempt = 0; attempt < 10 && cleanChecks < 3; attempt++)
        {
            var matches = FindProcesses();
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

    private static void LaunchRemote(string package, bool visible)
    {
        var npx = TrustedNpx();
        ForgetRelayProbe();
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(ActivityLogPath, string.Empty);
        File.WriteAllText(RuntimeLogPath, string.Empty);
        EnsureLauncherScript();
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
            "-Npx", npx, "-Package", package, "-ActivityLog", ActivityLogPath, "-RuntimeLog", RuntimeLogPath
        })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi);
    }

    private static void EnsureLauncherScript()
    {
        const string script = """
param([string]$Npx, [string]$Package, [string]$ActivityLog, [string]$RuntimeLog)
$lineCount = 0
& $Npx '--yes' $Package 'remote' 2>&1 | ForEach-Object {
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
                Volatile.Write(ref _relayProbe, new CachedProbe(DateTime.UtcNow, relay));
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

    /// <summary>Remote Desktop Commander's processes: the launcher, npx and node in remote mode, and what they started.</summary>
    private static List<ProcessInfo> FindProcesses()
    {
        var all = ReadProcesses().Where(p => p.ProcessId != Environment.ProcessId).ToList();
        var byId = all.GroupBy(p => p.ProcessId).ToDictionary(g => g.Key, g => g.First());
        bool Matches(ProcessInfo process, int depth)
            => IsRemoteDesktopCommanderProcess(process)
               // For example the cmd.exe that npx starts for the package's command, which names neither.
               || depth < 8 && process.ParentProcessId != process.ProcessId
                  && byId.TryGetValue(process.ParentProcessId, out var parent) && Matches(parent, depth + 1);
        return all.Where(p => Matches(p, 0)).ToList();
    }

    private static readonly Regex PackageArgument = new(@"@wonderwhy-er[\\/]desktop-commander(@[^\s""']*)?([\\/""'\s]|$)", RegexOptions.IgnoreCase);
    private static readonly Regex RemoteArgument = new(@"(^|[\s""'])remote([\s""']|$)", RegexOptions.IgnoreCase);

    /// <summary>
    /// Only the launcher this app writes, and cmd, npx or node running the package with the "remote" argument, so
    /// that other programs that merely mention desktop-commander (an editor, the MCP server mode) are left alone.
    /// </summary>
    internal static bool IsRemoteDesktopCommanderProcess(ProcessInfo process)
    {
        var command = process.CommandLine;
        if (process.Name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            return command.Contains(LauncherScriptPath, StringComparison.OrdinalIgnoreCase);
        if (!process.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) && !process.Name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        return PackageArgument.IsMatch(command) && RemoteArgument.IsMatch(command);
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
