using System.IO;
using System.Diagnostics;
using System.Management;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RunnerWidget;

public static class RemoteDesktopManager
{
    private static readonly string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RunnerWidget");
    private static readonly string ActivityLogPath = Path.Combine(StateDir, "rdc-activity.log");
    private static readonly string RuntimeLogPath = Path.Combine(StateDir, "rdc-runtime.log");
    private static readonly string LauncherScriptPath = Path.Combine(StateDir, "rdc-launch.ps1");
    public static bool IsConfigured()
    {
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".desktop-commander-device", "device.json");
        try { return File.Exists(config) && new FileInfo(config).Length > 0; }
        catch { return false; }
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
                ConnectionText = "Relay offline (process not running)",
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
                : "Relay status unknown (process was not started by Runner Manager)",
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
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var matches = ReadProcesses().Where(IsRemoteDesktopCommanderProcess).ToList();
            if (matches.Count == 0)
            {
                Thread.Sleep(400);
                continue;
            }
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
    });

    public static async Task RestartAsync()
    {
        await StopAsync();
        await Task.Delay(900);
        await StartAsync();
    }

    private static void LaunchRemote(bool visible)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(ActivityLogPath, string.Empty);
        File.WriteAllText(RuntimeLogPath, string.Empty);
        EnsureLauncherScript();
        var npx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npx.cmd");
        if (!File.Exists(npx)) npx = "npx.cmd";
        var psi = new ProcessStartInfo("powershell.exe")
        {
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
        Process.Start(psi);
    }

    private static void EnsureLauncherScript()
    {
        const string script = """
param([string]$Npx, [string]$ActivityLog, [string]$RuntimeLog)
& $Npx '--yes' '@wonderwhy-er/desktop-commander@latest' 'remote' 2>&1 | ForEach-Object {
    $line = $_.ToString()
    Write-Host $line
    $ts = [DateTime]::UtcNow.ToString('O')
    $safeLine = $line.Replace("`r", ' ').Replace("`n", ' ')
    Add-Content -LiteralPath $RuntimeLog -Value ($ts + '|' + $safeLine)
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
            if (snapshot.Running && snapshot.Connected) return;
            if (!snapshot.Running && snapshot.ConnectionKnown)
                throw new InvalidOperationException("RDC process exited before the relay connected.");
            await Task.Delay(500);
        }

        var final = ReadSnapshot();
        var detail = string.IsNullOrWhiteSpace(final.ConnectionText)
            ? "No relay-ready handshake was observed."
            : final.ConnectionText;
        throw new InvalidOperationException($"RDC process did not connect to the relay within {(int)timeout.TotalSeconds}s. {detail}");
    }

    private static RemoteConnectionInfo ReadConnectionSnapshot()
    {
        var info = new RemoteConnectionInfo();
        try
        {
            if (!File.Exists(RuntimeLogPath)) return info;
            foreach (var line in File.ReadLines(RuntimeLogPath))
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
        if (!File.Exists(config)) return "Security: not configured";
        try
        {
            var acl = FileSystemAclExtensions.GetAccessControl(new FileInfo(config));
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
            foreach (var rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (sid.IsWellKnown(WellKnownSidType.WorldSid) || sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid) || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid))
                    return "Security: review permissions";
            }
            return "Security: Windows-user protected";
        }
        catch { return "Security: ACL check unavailable"; }
    }

    private static List<ProcessInfo> ReadProcesses()
    {
        var result = new List<ProcessInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, Name, CommandLine, CreationDate FROM Win32_Process");
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

public sealed class RemoteActivityInfo
{
    public int ActiveCalls { get; init; }
    public DateTime? LastUsedUtc { get; init; }
    public string LastTool { get; init; } = string.Empty;
}
