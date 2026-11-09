using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RunnerManager;

/// <summary>
/// A task registered with Task Scheduler's default priority starts below normal, with low I/O and memory priority (see
/// <see cref="ScheduledTasks.TaskPriority"/>). The fleet agent and the dashboard raise themselves to normal when they start,
/// so a task that an older version registered does not slow them down until it is registered again. Programs they start
/// (gh, sc) inherit the priority class.
/// </summary>
internal static class ProcessPriority
{
    private const int ProcessIoPriority = 33;
    private const int IoPriorityNormal = 2;
    private const int ProcessMemoryPriority = 0;
    private const int MemoryPriorityNormal = 5;

    /// <summary>Raises this process to normal CPU, I/O and memory priority where it is lower. Returns whether anything changed.</summary>
    public static bool RaiseToNormal()
    {
        var raised = false;
        try
        {
            using var self = Process.GetCurrentProcess();
            if (self.PriorityClass is ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle)
            {
                self.PriorityClass = ProcessPriorityClass.Normal;
                raised = true;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
        try
        {
            var handle = GetCurrentProcess();
            if (NtQueryInformationProcess(handle, ProcessIoPriority, out var io, sizeof(int), out _) == 0 && io < IoPriorityNormal)
            {
                var normal = IoPriorityNormal;
                raised |= NtSetInformationProcess(handle, ProcessIoPriority, ref normal, sizeof(int)) == 0;
            }
            var memory = new MemoryPriorityInformation();
            var size = Marshal.SizeOf<MemoryPriorityInformation>();
            if (GetProcessInformation(handle, ProcessMemoryPriority, ref memory, size) && memory.MemoryPriority < MemoryPriorityNormal)
            {
                memory.MemoryPriority = MemoryPriorityNormal;
                raised |= SetProcessInformation(handle, ProcessMemoryPriority, ref memory, size);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return raised;
    }

    /// <summary>This process's CPU priority class, I/O priority (0 very low to 3 high) and memory priority (1 to 5); -1 when unknown.</summary>
    internal static (ProcessPriorityClass Cpu, int Io, int Memory) Read()
    {
        using var self = Process.GetCurrentProcess();
        var handle = GetCurrentProcess();
        var io = NtQueryInformationProcess(handle, ProcessIoPriority, out var value, sizeof(int), out _) == 0 ? value : -1;
        var memory = new MemoryPriorityInformation();
        var memoryPriority = GetProcessInformation(handle, ProcessMemoryPriority, ref memory, Marshal.SizeOf<MemoryPriorityInformation>())
            ? memory.MemoryPriority : -1;
        return (self.PriorityClass, io, memoryPriority);
    }

    /// <summary>Lowers this process as a task with the default priority starts it (tests only).</summary>
    internal static void LowerForTest()
    {
        using var self = Process.GetCurrentProcess();
        self.PriorityClass = ProcessPriorityClass.BelowNormal;
        var handle = GetCurrentProcess();
        var low = 1;
        NtSetInformationProcess(handle, ProcessIoPriority, ref low, sizeof(int));
        var memory = new MemoryPriorityInformation { MemoryPriority = 2 };
        SetProcessInformation(handle, ProcessMemoryPriority, ref memory, Marshal.SizeOf<MemoryPriorityInformation>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryPriorityInformation
    {
        public int MemoryPriority;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(IntPtr process, int informationClass, ref MemoryPriorityInformation information, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref MemoryPriorityInformation information, int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, out int information, int length, out int returned);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr process, int informationClass, ref int information, int length);
}
