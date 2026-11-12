using System.IO;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace RunnerManager;

/// <summary>"Allow AI assistants" and "Ask before changes".</summary>
internal readonly record struct AssistantSwitchValues(bool Enabled, bool AskBeforeChanges);

/// <summary>Where the AI assistant switches are kept.</summary>
internal interface IAssistantSwitchStore
{
    /// <summary>Where the switches are, for messages.</summary>
    string Where { get; }
    /// <summary>Whether this process may change them: only one with administrator rights.</summary>
    bool CanWrite { get; }
    /// <summary>The stored switches, or null when none are stored yet. Throws when they cannot be read.</summary>
    AssistantSwitchValues? Read();
    void Write(AssistantSwitchValues values);
    /// <summary>Removes the stored switches (for uninstall).</summary>
    void Remove();
}

/// <summary>
/// The AI assistant switches, kept where only an administrator can change them and everyone can read them, so that a
/// program running as the user without administrator rights cannot turn assistants on or approvals off. The dashboard and
/// the fleet agent run as administrator and change them. <c>--mcp</c> and <c>ai</c> neither read nor change them: they
/// forward every call to the fleet agent, which decides. Earlier versions kept them in fleet.json, which every program of
/// the user can change: they are moved from there once.
/// </summary>
internal static class AssistantSwitches
{
    internal static readonly AssistantSwitchValues Defaults = new(Enabled: false, AskBeforeChanges: true);

    internal const string NotAdministratorText =
        "Only a program with administrator rights can change the AI assistant settings. Change them in Runner Manager → Settings → AI assistants.";

    private static readonly object Gate = new();
    private static IAssistantSwitchStore? _checked;

    /// <summary>
    /// Where this process keeps the switches: <see cref="ShippedStore"/>, or <see cref="FleetAgentSwitchStore"/> in
    /// <c>--mcp</c> and <c>ai</c>. Tests set a store of their own here, in code.
    /// </summary>
    internal static IAssistantSwitchStore Store { get; set; } = ShippedStore();

    /// <summary>
    /// The registry, always. No environment variable, file or argument chooses another place: a program without
    /// administrator rights can set those for the elevated fleet agent and dashboard, and so would choose the switches.
    /// </summary>
    internal static IAssistantSwitchStore ShippedStore() => new RegistrySwitchStore();

    /// <summary>
    /// For <c>--mcp</c> and <c>ai</c>, which forward every call to the fleet agent. From here on this process neither
    /// reads nor changes the switches, also when it runs with administrator rights: the fleet agent alone decides.
    /// </summary>
    internal static void LeaveToFleetAgent() => Store = new FleetAgentSwitchStore();

    /// <summary>The switches. Nothing stored yet, or nothing readable, means the defaults: off, and asking.</summary>
    public static AssistantSwitchValues Current
    {
        get
        {
            try { return Store.Read() ?? Defaults; }
            catch (Exception ex) when (IsStoreError(ex)) { return Defaults; }
        }
    }

    /// <summary>Changes one switch or both, and logs each change. Throws, and changes nothing, without administrator rights.</summary>
    public static void Set(bool? enabled = null, bool? askBeforeChanges = null)
    {
        var store = Store;
        if (!store.CanWrite) throw new UnauthorizedAccessException(NotAdministratorText);
        lock (Gate)
        {
            AssistantSwitchValues? stored;
            try { stored = store.Read(); }
            catch (Exception ex) when (IsStoreError(ex))
            {
                throw new IOException($"Could not read the AI assistant settings in {store.Where}: {ex.Message}", ex);
            }
            var old = stored ?? Defaults;
            var now = new AssistantSwitchValues(enabled ?? old.Enabled, askBeforeChanges ?? old.AskBeforeChanges);
            if (stored == now) return;
            Write(store, now);
            if (now.Enabled != old.Enabled) Log("Allow AI assistants", old.Enabled, now.Enabled);
            if (now.AskBeforeChanges != old.AskBeforeChanges) Log("Ask before changes", old.AskBeforeChanges, now.AskBeforeChanges);
        }
    }

    /// <summary>
    /// Run on each read of fleet.json. The first process with administrator rights that finds no stored switches copies
    /// them from fleet.json; after that, the values in fleet.json are never read again, and the next save drops them.
    /// A process without those rights uses the defaults until then.
    /// </summary>
    internal static void MoveFromFile(AssistantOptions file)
    {
        var store = Store;
        if (ReferenceEquals(_checked, store)) return;
        lock (Gate)
        {
            if (ReferenceEquals(_checked, store)) return;
            try
            {
                if (store.Read() == null)
                {
                    if (!store.CanWrite) return;
                    var moved = new AssistantSwitchValues(file.EnabledInFile ?? Defaults.Enabled, file.AskBeforeChangesInFile ?? Defaults.AskBeforeChanges);
                    Write(store, moved);
                    MachineRelay.AppendAgentLog($"The AI assistant settings moved from fleet.json to {store.Where}, where only an administrator can change them. "
                                                + $"\"Allow AI assistants\" is {OnOff(moved.Enabled)}. \"Ask before changes\" is {OnOff(moved.AskBeforeChanges)}.");
                }
                _checked = store;
            }
            // Tried again at the next read; until then the defaults count.
            catch (Exception ex) when (IsStoreError(ex)) { }
        }
    }

    /// <summary>
    /// Removes the stored switches from this PC, for uninstall. Needs administrator rights. This process does not move
    /// them from fleet.json again afterwards.
    /// </summary>
    internal static void Remove()
    {
        var store = Store;
        lock (Gate)
        {
            store.Remove();
            _checked = store;
        }
    }

    private static void Write(IAssistantSwitchStore store, AssistantSwitchValues values)
    {
        try { store.Write(values); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            throw new UnauthorizedAccessException(NotAdministratorText, ex);
        }
        catch (IOException ex)
        {
            throw new IOException($"Could not save the AI assistant settings in {store.Where}: {ex.Message}", ex);
        }
    }

    private static void Log(string setting, bool old, bool now)
        => MachineRelay.AppendAgentLog($"\"{setting}\" changed from {OnOff(old)} to {OnOff(now)}.");

    private static string OnOff(bool on) => on ? "on" : "off";

    private static bool IsStoreError(Exception ex)
        => ex is IOException or UnauthorizedAccessException or SecurityException or JsonException;
}

/// <summary>
/// HKEY_LOCAL_MACHINE\SOFTWARE\RunnerManager, in the 64-bit registry: by default only administrators (and SYSTEM) can
/// change it, and every user can read it.
/// </summary>
internal sealed class RegistrySwitchStore : IAssistantSwitchStore
{
    internal const string KeyPath = @"SOFTWARE\RunnerManager";
    internal const string EnabledValue = "AssistantEnabled";
    internal const string AskBeforeChangesValue = "AssistantAskBeforeChanges";

    private static readonly Lazy<bool> Elevated = new(() => Elevation.IsElevated);

    public string Where => @"HKEY_LOCAL_MACHINE\" + KeyPath;

    public bool CanWrite => Elevated.Value;

    public AssistantSwitchValues? Read()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(KeyPath);
        var enabled = key?.GetValue(EnabledValue);
        var ask = key?.GetValue(AskBeforeChangesValue);
        if (enabled == null && ask == null) return null;
        // A value that is missing or not a number counts as its safe default.
        return new AssistantSwitchValues(
            enabled is int e ? e != 0 : AssistantSwitches.Defaults.Enabled,
            ask is int a ? a != 0 : AssistantSwitches.Defaults.AskBeforeChanges);
    }

    public void Write(AssistantSwitchValues values)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.CreateSubKey(KeyPath, writable: true);
        key.SetValue(EnabledValue, values.Enabled ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue(AskBeforeChangesValue, values.AskBeforeChanges ? 1 : 0, RegistryValueKind.DWord);
    }

    public void Remove()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (var key = root.OpenSubKey(KeyPath, writable: true))
        {
            if (key == null) return;
            key.DeleteValue(EnabledValue, throwOnMissingValue: false);
            key.DeleteValue(AskBeforeChangesValue, throwOnMissingValue: false);
            if (key.ValueCount > 0 || key.SubKeyCount > 0) return;
        }
        root.DeleteSubKey(KeyPath, throwOnMissingSubKey: false);
    }
}

/// <summary>
/// The store of a process that forwards every call to the fleet agent (<c>--mcp</c>, <c>ai</c>): it holds nothing and
/// changes nothing, so the defaults (off, asking) are all such a process could see.
/// </summary>
internal sealed class FleetAgentSwitchStore : IAssistantSwitchStore
{
    public string Where => "the fleet agent";

    public bool CanWrite => false;

    public AssistantSwitchValues? Read() => null;

    public void Write(AssistantSwitchValues values) => throw new UnauthorizedAccessException(AssistantSwitches.NotAdministratorText);

    public void Remove() => throw new UnauthorizedAccessException(AssistantSwitches.NotAdministratorText);
}
