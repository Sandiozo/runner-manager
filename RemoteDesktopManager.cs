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
        var security = ReadSecurityStatus();
        if (matches.Count == 0)
            return new RemoteInfo { Running = false, ActiveCalls = 0, LastUsedUtc = activity.LastUsedUtc, LastTool = activity.LastTool, SecurityText = security };
        var started = matches.Select(p => TryParseWmiDate(p.CreationDate))
            .Where(d => d.HasValue).Select(d => d!.Value).DefaultIfEmpty().Min();
        return new RemoteInfo
        {
            Running = true,
            Pids = matches.Select(p => p.ProcessId).Distinct().OrderBy(p => p).ToArray(),
            Started = started == default ? null : started,
            ActiveCalls = activity.ActiveCalls,
            LastUsedUtc = activity.LastUsedUtc,
            LastTool = activity.LastTool,
            SecurityText = security
        };
    }

    public static Task StartAsync() => Task.Run(() =>
    {
        if (ReadSnapshot().Running) return;
        LaunchRemote(visible: false);
    });

    public static Task SetupAsync() => Task.Run(() =>
    {
        if (IsConfigured()) { LaunchRemote(visible: false); return; }
        LaunchRemote(visible: true);
    });

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
        EnsureLauncherScript();
        var npx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npx.cmd");
        if (!File.Exists(npx)) npx = "npx.cmd";
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = !visible,
            WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", LauncherScriptPath, "-Npx", npx, "-ActivityLog", ActivityLogPath })
            psi.ArgumentList.Add(arg);
        Process.Start(psi);
    }

    private static void EnsureLauncherScript()
    {
        const string script = """
param([string]$Npx, [string]$ActivityLog)
& $Npx '@wonderwhy-er/desktop-commander@latest' 'remote' 2>&1 | ForEach-Object {
    $line = $_.ToString()
    Write-Host $line
    $ts = [DateTime]::UtcNow.ToString('O')
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
    public uint[] Pids { get; init; } = Array.Empty<uint>();
    public DateTime? Started { get; init; }
    public int ActiveCalls { get; init; }
    public DateTime? LastUsedUtc { get; init; }
    public string LastTool { get; init; } = string.Empty;
    public string SecurityText { get; init; } = string.Empty;
}

public sealed class RemoteActivityInfo
{
    public int ActiveCalls { get; init; }
    public DateTime? LastUsedUtc { get; init; }
    public string LastTool { get; init; } = string.Empty;
}
