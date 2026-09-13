using System.IO;
using System.Diagnostics;
using System.Management;

namespace RunnerWidget;

public static class RemoteDesktopManager
{
    public static bool IsConfigured()
    {
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".desktop-commander-device", "device.json");
        try { return File.Exists(config) && new FileInfo(config).Length > 0; }
        catch { return false; }
    }

    public static RemoteInfo ReadSnapshot()
    {
        var matches = ReadProcesses().Where(IsRemoteDesktopCommanderProcess).ToList();
        if (matches.Count == 0) return new RemoteInfo { Running = false };
        var started = matches.Select(p => TryParseWmiDate(p.CreationDate))
            .Where(d => d.HasValue).Select(d => d!.Value).DefaultIfEmpty().Min();
        return new RemoteInfo
        {
            Running = true,
            Pids = matches.Select(p => p.ProcessId).Distinct().OrderBy(p => p).ToArray(),
            Started = started == default ? null : started
        };
    }

    public static Task StartAsync()
    {
        return Task.Run(() =>
        {
            if (ReadSnapshot().Running) return;
            var npx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npx.cmd");
            if (!File.Exists(npx)) npx = "npx.cmd";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"Remote Desktop Commander\" /min \"{npx}\" @wonderwhy-er/desktop-commander@latest remote",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        });
    }

    public static Task StopAsync()
    {
        return Task.Run(() =>
        {
            var matches = ReadProcesses().Where(IsRemoteDesktopCommanderProcess).ToList();
            var ids = matches.Select(p => p.ProcessId).ToHashSet();
            foreach (var info in matches.Where(p => !ids.Contains(p.ParentProcessId)))
            {
                try
                {
                    using var process = Process.GetProcessById((int)info.ProcessId);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        });
    }

    public static async Task RestartAsync()
    {
        await StopAsync();
        await Task.Delay(900);
        await StartAsync();
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
        return command.Contains("desktop-commander", StringComparison.OrdinalIgnoreCase)
               && command.Contains("remote", StringComparison.OrdinalIgnoreCase);
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
}
