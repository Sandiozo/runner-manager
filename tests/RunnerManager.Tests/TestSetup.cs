using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace RunnerManager.Tests;

internal static class TestSetup
{
    internal static readonly string StateDir = Path.Combine(Path.GetTempPath(), $"runner-manager-tests-{Environment.ProcessId}");

    /// <summary>
    /// Keeps tests away from the real settings in %LOCALAPPDATA%\RunnerManager, and from the real AI assistant switches
    /// in HKLM\SOFTWARE\RunnerManager. Runner Manager itself always keeps the switches in the registry: only code sets
    /// another store, never the environment.
    /// </summary>
    [ModuleInitializer]
    internal static void UseTemporaryStateDir()
    {
        Environment.SetEnvironmentVariable("RUNNER_MANAGER_STATE_DIR", StateDir);
        AssistantSwitches.Store = new FileSwitchStore(Path.Combine(StateDir, "assistant-switches.json"));
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

/// <summary>
/// Starts a listener on ports that <see cref="TestPorts.FreeRange"/> found free. It finds them first and the listener
/// takes them after, so a test running at the same time may take one in between; other ports are then tried.
/// </summary>
internal static class FreePorts
{
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
