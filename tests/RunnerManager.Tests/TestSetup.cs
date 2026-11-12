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
