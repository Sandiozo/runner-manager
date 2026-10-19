using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace RunnerManager;

/// <summary>Cheap machine health readings for the status payload.</summary>
public static class SystemHealth
{
    private static readonly object CpuGate = new();
    private static ulong _lastIdle, _lastKernel, _lastUser;
    private static readonly ConcurrentDictionary<string, (DateTime MeasuredUtc, long Bytes)> FolderSizes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> FolderSizeRunning = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FolderSizeLifetime = TimeSpan.FromMinutes(10);

    public static HealthSnapshot Read(IEnumerable<string> runnerRoots)
    {
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var memoryOk = GlobalMemoryStatusEx(ref memory);
        return new HealthSnapshot
        {
            CpuPercent = SampleCpu(),
            MemoryPercent = memoryOk ? (int)memory.MemoryLoad : 0,
            MemoryTotalGb = memoryOk ? Math.Round(memory.TotalPhys / 1024d / 1024 / 1024, 1) : 0,
            BootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64),
            RebootPending = IsRebootPending(),
            Disks = ReadDisks(runnerRoots)
        };
    }

    /// <summary>CPU load since the previous call (the agent samples every few seconds).</summary>
    private static int SampleCpu()
    {
        lock (CpuGate)
        {
            if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return 0;
            ulong idle = idleTime.ToULong(), kernel = kernelTime.ToULong(), user = userTime.ToULong();
            if (_lastKernel == 0)
            {
                _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
                Thread.Sleep(200);
                if (!GetSystemTimes(out idleTime, out kernelTime, out userTime)) return 0;
                idle = idleTime.ToULong(); kernel = kernelTime.ToULong(); user = userTime.ToULong();
            }
            var idleDelta = idle - _lastIdle;
            var total = (kernel - _lastKernel) + (user - _lastUser); // kernel time includes idle time
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
            return total == 0 ? 0 : (int)Math.Clamp(Math.Round(100.0 * (total - idleDelta) / total), 0, 100);
        }
    }

    private static DiskStatus[] ReadDisks(IEnumerable<string> runnerRoots)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\"
        };
        foreach (var root in runnerRoots)
        {
            try
            {
                var drive = Path.GetPathRoot(root);
                if (!string.IsNullOrEmpty(drive)) roots.Add(drive);
            }
            catch { }
        }
        var disks = new List<DiskStatus>();
        foreach (var root in roots.OrderBy(r => r))
        {
            try
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady) continue;
                disks.Add(new DiskStatus
                {
                    Name = drive.Name.TrimEnd('\\'),
                    FreeGb = Math.Round(drive.AvailableFreeSpace / 1024d / 1024 / 1024),
                    TotalGb = Math.Round(drive.TotalSize / 1024d / 1024 / 1024)
                });
            }
            catch { }
        }
        return disks.ToArray();
    }

    private static bool IsRebootPending()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") != null) return true;
            if (hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") != null) return true;
            using var session = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            return session?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
        }
        catch { return false; }
    }

    /// <summary>
    /// Size of a folder from a background measurement. Walking a big _work folder is slow, so this returns the
    /// last measurement (null before the first one finishes) and refreshes it at most every 10 minutes.
    /// </summary>
    public static long? FolderSize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var cached = FolderSizes.TryGetValue(path, out var entry) ? entry : ((DateTime, long)?)null;
        if ((cached == null || DateTime.UtcNow - cached.Value.Item1 > FolderSizeLifetime) && FolderSizeRunning.TryAdd(path, true))
        {
            _ = Task.Run(() =>
            {
                try { FolderSizes[path] = (DateTime.UtcNow, MeasureFolder(path)); }
                finally { FolderSizeRunning.TryRemove(path, out _); }
            });
        }
        return cached?.Item2;
    }

    public static void ForgetFolderSize(string path) => FolderSizes.TryRemove(path, out _);

    private static long MeasureFolder(string path)
    {
        if (!Directory.Exists(path)) return 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                try { total += file.Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    /// <summary>Version of the installed GitHub Actions runner, from its listener binary.</summary>
    public static string RunnerVersion(string root)
    {
        try
        {
            foreach (var name in new[] { "Runner.Listener.dll", "Runner.Listener.exe" })
            {
                var file = Path.Combine(root, "bin", name);
                if (!File.Exists(file)) continue;
                var info = FileVersionInfo.GetVersionInfo(file);
                var version = info.ProductVersion ?? info.FileVersion ?? string.Empty;
                var plus = version.IndexOf('+');
                if (plus > 0) version = version[..plus];
                if (!string.IsNullOrWhiteSpace(version)) return version.Trim();
            }
        }
        catch { }
        return string.Empty;
    }

    public static string FormatBytes(long? bytes)
    {
        if (bytes == null) return "measuring…";
        double value = bytes.Value;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public ulong ToULong() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

public sealed class HealthSnapshot
{
    public int CpuPercent { get; init; }
    public int MemoryPercent { get; init; }
    public double MemoryTotalGb { get; init; }
    public DateTime BootUtc { get; init; }
    public bool RebootPending { get; init; }
    public DiskStatus[] Disks { get; init; } = Array.Empty<DiskStatus>();
}

public sealed record DiskStatus
{
    public string Name { get; init; } = string.Empty;
    public double FreeGb { get; init; }
    public double TotalGb { get; init; }
    [JsonIgnore]
    public bool IsLow => TotalGb > 0 && (FreeGb < 15 || FreeGb / TotalGb < 0.08);
}

public sealed record AgentEvent
{
    public DateTime Utc { get; init; }
    public string Text { get; init; } = string.Empty;
    public bool Warning { get; init; }
}
