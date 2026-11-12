using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace RunnerManager.Tests;

internal static class TestSetup
{
    internal static readonly string StateDir = Path.Combine(Path.GetTempPath(), $"runner-manager-tests-{Environment.ProcessId}");

    /// <summary>
    /// Keeps tests away from the real settings in %LOCALAPPDATA%\RunnerManager, and from the real AI assistant switches
    /// and install records in HKLM\SOFTWARE\RunnerManager. Runner Manager itself always keeps them in the registry: only
    /// code sets another store, never the environment.
    /// </summary>
    [ModuleInitializer]
    internal static void UseTemporaryStateDir()
    {
        // In code first: a process with administrator rights (the tests on GitHub's runners) ignores the variable. The
        // variable stays for the copies of RunnerManager.exe that tests start.
        AppPaths.TestStateDir = StateDir;
        Environment.SetEnvironmentVariable(AppPaths.StateDirVariable, StateDir);
        // The start helper is never written by a test unless the test gives its own bytes and folder, never under the
        // update lock that the installed Runner Manager on this PC uses, and is checked again right away.
        Launcher.Embedded = () => null;
        Launcher.MayBeInPlace = folder => folder.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);
        Launcher.UnderUpdateLock = (_, write) => write();
        Launcher.Settle = () => Task.CompletedTask;
        // No update waits to prove itself, whatever the data folder of an earlier test says.
        MachineRelay.UpdateWatched = () => false;
        AssistantSwitches.Store = new FileSwitchStore(Path.Combine(StateDir, "assistant-switches.json"));
        MachineInstall.Records = new MemoryInstallRecords();
        var tasks = new MemoryTasks();
        MachineInstall.Tasks = tasks;
        ScheduledTasks.Scheduler = tasks;
        MachineInstall.HasProfile = _ => true;
    }
}

/// <summary>
/// Scheduled tasks in memory, in place of Task Scheduler: what Runner Manager reads, and what it runs schtasks.exe for
/// (registering, deleting, starting and ending tasks), which is recorded in <see cref="Calls"/>. Only in the tests.
/// </summary>
internal sealed class MemoryTasks : ITaskReader, ITaskScheduler
{
    /// <summary>The tasks by name. A null definition stands for a task that exists but cannot be read.</summary>
    public Dictionary<string, string?> Definitions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>False stands for a list of tasks that cannot be read.</summary>
    public bool NamesReadable { get; set; } = true;

    /// <summary>The tasks that run now.</summary>
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What was done to the tasks, in order: "Create …", "Delete …", "Run …" or "End …" and the task's name. Reads are not recorded.</summary>
    public List<string> Calls { get; } = new();

    public IReadOnlyList<string>? Names() => NamesReadable ? Definitions.Keys.ToList() : null;

    public Task<string?> ReadAsync(string name) => Task.FromResult(Definitions.TryGetValue(name, out var xml) ? xml : string.Empty);

    private static readonly System.Text.RegularExpressions.Regex Arguments =
        new("^/(?<verb>Query|Create|Delete|Run|End)(?: /F)? /TN \"(?<name>[^\"]*)\"(?<xml> /XML(?: \"(?<path>[^\"]*)\")?)?$");

    public Task<(int ExitCode, string Output)> SchtasksAsync(string arguments)
    {
        var match = Arguments.Match(arguments);
        if (!match.Success) throw new InvalidOperationException($"The test scheduler does not know: schtasks {arguments}");
        var name = match.Groups["name"].Value;
        var exists = Definitions.TryGetValue(name, out var xml);
        (int, string) Missing() => (1, $"ERROR: The system cannot find the file specified. ({name})");
        lock (Calls)
        {
            switch (match.Groups["verb"].Value)
            {
                case "Query":
                    if (!exists) return Task.FromResult(Missing());
                    if (!match.Groups["xml"].Success) return Task.FromResult((0, name));
                    return Task.FromResult(xml == null ? (1, "ERROR: Access is denied.") : (0, xml));
                case "Create":
                    Definitions[name] = File.ReadAllText(match.Groups["path"].Value);
                    Unindexed.Remove(name);
                    Calls.Add($"Create {name}");
                    return Task.FromResult((0, $"SUCCESS: The scheduled task \"{name}\" has successfully been created."));
                case "Delete":
                    Calls.Add($"Delete {name}");
                    Running.Remove(name);
                    return Task.FromResult(Definitions.Remove(name) ? (0, "SUCCESS") : Missing());
                case "Run":
                    Calls.Add($"Run {name}");
                    if (!exists) return Task.FromResult(Missing());
                    Running.Add(name);
                    return Task.FromResult((0, "SUCCESS"));
                default:
                    Calls.Add($"End {name}");
                    Running.Remove(name);
                    return Task.FromResult(exists ? (0, "SUCCESS") : Missing());
            }
        }
    }

    /// <summary>Tasks that exist but are missing from Task Scheduler's index, so they cannot run.</summary>
    public HashSet<string> Unindexed { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool? IsIndexed(string name) => !Definitions.ContainsKey(name) ? null : !Unindexed.Contains(name);

    public bool? IsRegistered(string name) => NamesReadable ? Definitions.ContainsKey(name) : null;

    public Task<bool?> IsRunningAsync(string name) => Task.FromResult<bool?>(Running.Contains(name));

    public Task<IDisposable> LockAsync() => Task.FromResult<IDisposable>(new NoLock());

    private sealed class NoLock : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// Gives a test install records and scheduled tasks of its own, in memory, and puts back the ones before it when
/// disposed. For tests in <see cref="InstallStateCollection"/>, which run one at a time.
/// </summary>
internal sealed class TestInstallState : IDisposable
{
    private readonly IInstallRecordStore _records = MachineInstall.Records;
    private readonly ITaskReader _tasks = MachineInstall.Tasks;
    private readonly ITaskScheduler _scheduler = ScheduledTasks.Scheduler;
    private readonly Func<string, bool> _hasProfile = MachineInstall.HasProfile;

    public TestInstallState()
    {
        MachineInstall.Records = Records;
        MachineInstall.Tasks = Tasks;
        ScheduledTasks.Scheduler = Tasks;
        MachineInstall.HasProfile = sid => !WithoutProfile.Contains(sid);
        MachineInstall.Forget();
        MachineRelay.ForgetAgentInstalled();
        AgentOwnership.Forget();
        ScheduledTasks.ForgetLogged();
    }

    public MemoryInstallRecords Records { get; } = new();

    public MemoryTasks Tasks { get; } = new();

    /// <summary>The SIDs of Windows users who have no profile on this PC (their account was removed).</summary>
    public HashSet<string> WithoutProfile { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        MachineInstall.Records = _records;
        MachineInstall.Tasks = _tasks;
        ScheduledTasks.Scheduler = _scheduler;
        MachineInstall.HasProfile = _hasProfile;
        MachineInstall.Forget();
        MachineRelay.ForgetAgentInstalled();
        AgentOwnership.Forget();
    }
}

/// <summary>The install records and scheduled tasks are process-wide, so tests that set their own run on their own.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class InstallStateCollection
{
    public const string Name = "Install state";
}

/// <summary>Install records in memory, in place of HKLM\SOFTWARE\RunnerManager\Installs. Only in the tests.</summary>
internal sealed class MemoryInstallRecords : IInstallRecordStore
{
    private readonly List<InstallRecord> _records = new();

    public IReadOnlyList<InstallRecord> ReadAll()
    {
        lock (_records) return _records.ToList();
    }

    public void Write(string sid, string? folder)
    {
        lock (_records)
        {
            _records.RemoveAll(r => r.Sid.Equals(sid, StringComparison.OrdinalIgnoreCase));
            if (folder != null) _records.Add(new InstallRecord(sid, folder));
        }
    }
}

/// <summary>
/// The AI assistant switches in a file, in place of the registry, so tests never touch the real registry.
/// <see cref="CanWrite"/> stands in for administrator rights. Only in the tests: Runner Manager has no file store.
/// </summary>
internal sealed class FileSwitchStore(string path) : IAssistantSwitchStore
{
    public string Where => path;

    public bool CanWrite { get; init; } = true;

    public AssistantSwitchValues? Read()
        => File.Exists(path) ? JsonSerializer.Deserialize<AssistantSwitchValues>(File.ReadAllText(path)) : null;

    public void Write(AssistantSwitchValues values)
    {
        if (!CanWrite) throw new UnauthorizedAccessException($"Access to {path} is denied.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(values));
        File.Move(temp, path, overwrite: true);
    }

    public void Remove() => File.Delete(path);
}

/// <summary>
/// Gives a test AI assistant switches of its own, stored in a file of its own, and puts back the store before it when
/// disposed. For tests in <see cref="FleetSettingsCollection"/>, which run one at a time.
/// </summary>
internal sealed class TestSwitches : IDisposable
{
    private readonly IAssistantSwitchStore _before = AssistantSwitches.Store;

    public TestSwitches(bool enabled = false, bool askBeforeChanges = true, bool administrator = true)
    {
        Path = System.IO.Path.Combine(TestSetup.StateDir, $"assistant-switches-{Guid.NewGuid():N}.json");
        new FileSwitchStore(Path).Write(new AssistantSwitchValues(enabled, askBeforeChanges));
        AssistantSwitches.Store = new FileSwitchStore(Path) { CanWrite = administrator };
    }

    public string Path { get; }

    public void Dispose()
    {
        AssistantSwitches.Store = _before;
        try { File.Delete(Path); } catch (IOException) { }
    }
}

/// <summary>Starts test listeners on free ports.</summary>
internal static class FreePorts
{
    /// <summary>
    /// Starts a pairing host with port 0 for both its ports: the system picks free ports as the host binds them, so a
    /// test running at the same time cannot take one in between. <paramref name="port"/> is the pairing port it got.
    /// </summary>
    /// <param name="create">Makes the host from a pairing port and an announce port (both 0).</param>
    public static Pairing.PairingHost Host(Func<int, int, Pairing.PairingHost> create, out int port)
    {
        var host = create(0, 0);
        try { host.Start(); }
        catch
        {
            host.Dispose();
            throw;
        }
        port = host.Port;
        return host;
    }

    /// <summary>
    /// Starts a listener on ports that <see cref="TestPorts.FreeRange"/> found free. It finds them first and the listener
    /// takes them after, so a test running at the same time may take one in between; other ports are then tried.
    /// Only for the fleet agent's server: it listens with HttpListener, whose address names its port, and HttpListener
    /// cannot be given port 0 to pick one. Everything that can bind port 0 does so instead (see <see cref="Host"/>).
    /// </summary>
    public static T Listen<T>(int count, Func<int, T> create, Action<T> start) where T : IDisposable
    {
        for (var attempt = 1; ; attempt++)
        {
            var listener = create(TestPorts.FreeRange(count));
            try
            {
                start(listener);
                return listener;
            }
            catch (Exception ex) when (attempt < 5 && ex is System.Net.Sockets.SocketException or System.Net.HttpListenerException)
            {
                listener.Dispose();
            }
        }
    }
}
