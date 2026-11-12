namespace RunnerManager;

/// <summary>Addresses of paired PCs on another network, added in Settings → Fleet.</summary>
internal static class FleetAddressRules
{
    /// <summary>Null when the address can be looked up and called, otherwise the message to show.</summary>
    internal static string? Problem(string address, string thisPcName, IEnumerable<string> localAddresses)
    {
        var text = address.Trim();
        if (text.Length == 0) return "Enter the PC's name, Tailscale name or IPv4 address.";
        if (text.Contains("://") || text.Contains('/') || text.Contains('\\') || text.Any(char.IsWhiteSpace))
            return "Enter only the name or address, such as mypc.example.ts.net or 100.101.102.103, with an optional :port.";
        // Discovery and the firewall rules for Tailscale use IPv4 only.
        if (text.StartsWith('[') || text.Count(c => c == ':') > 1) return "IPv6 addresses are not supported. Enter the PC's IPv4 address or its name.";
        var (host, port) = FleetNetwork.SplitHostPort(text);
        if (text.Contains(':') && port is not (>= 1 and <= 65535)) return "The port must be a number from 1 to 65535.";
        // Windows PC names can have letters of any alphabet, such as JĀNIS-PC.
        if (host.Length == 0 || !host.All(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_'))
            return "A PC name or address can only contain letters, digits, '-', '_' and '.'.";
        if (host.Equals(thisPcName, StringComparison.OrdinalIgnoreCase) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("127.", StringComparison.Ordinal) || localAddresses.Contains(host, StringComparer.OrdinalIgnoreCase))
            return "This is the address of this PC. Enter the address of another PC.";
        return null;
    }

    internal static bool SameHost(string a, string b)
        => FleetNetwork.HostOf(a).Equals(FleetNetwork.HostOf(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The placeholder the fleet refresh lists for an address where no PC answered.</summary>
    internal static bool IsAddressPlaceholder(MachineInfo machine, string address) => machine.MachineKey == "ADDR_" + MachineRelay.Sanitize(address);

    /// <summary>
    /// What is known about the address: the PC that has it, the PC that answered when it was asked, or why none did.
    /// </summary>
    /// <param name="machines">The PCs of the last fleet refresh, with a placeholder for each address where none answered.</param>
    /// <param name="answer">
    /// What the address answered when Settings asked it: the PC, or a placeholder with the reason. The fleet refresh
    /// lists a PC that answers there at the address it already had, so only this names it.
    /// </param>
    internal static string Status(string address, IEnumerable<PeerRecord> knownPeers, IEnumerable<ManagedPcRecord> managedPcs,
        IReadOnlyCollection<MachineInfo> machines, MachineInfo? answer, Func<DateTime, string> lastSeen)
    {
        // A managed PC refuses requests made with the fleet code, and is reached at its own address anyway.
        if (managedPcs.FirstOrDefault(p => SameHost(p.Address, address)) is { } managed)
            return $"This is the address of {managed.Name}, a PC managed one way. It is found without this address, so you can remove it.";
        if (knownPeers.FirstOrDefault(p => SameHost(p.Address, address)) is { } peer)
            return $"{peer.Name} answers here  ·  last seen {lastSeen(peer.LastSeenUtc)}";
        // The newest refresh did not reach the address. It goes before what the address answered when Settings asked:
        // a PC that answers then is saved at this address (MachineRelay.RememberPeer), and the line above names it. Only
        // a later refresh moves it to another address, and a refresh that asks this address after that is the newer one.
        if (machines.FirstOrDefault(m => IsAddressPlaceholder(m, address)) is { } unanswered)
            return NoAnswer(unanswered.ConnectionProblem);
        if (answer != null && IsAddressPlaceholder(answer, address)) return NoAnswer(answer.ConnectionProblem);
        if (answer != null)
        {
            var listed = knownPeers.FirstOrDefault(p => MachineRelay.Sanitize(p.Name) == answer.MachineKey);
            return listed is { Address.Length: > 0 }
                ? $"{answer.MachineName} answers here. It is listed at {listed.Address}."
                : $"{answer.MachineName} answers here.";
        }
        return "Not checked yet.";
    }

    private static string NoAnswer(string problem)
        => string.IsNullOrWhiteSpace(problem) ? "No PC in this fleet answered." : $"No PC in this fleet answered. {problem.Trim().TrimEnd('.')}.";
}

/// <summary>
/// The Settings row for this PC's code. On a PC managed one way, the code is its access code: it belongs to this PC
/// only, and the fleet that manages it has it. Everywhere else it is the fleet code, the same on every PC of the fleet.
/// </summary>
internal static class CodeRowText
{
    /// <param name="managedBy">The fleet PC that manages this PC; empty on a fleet member.</param>
    internal static string Title(string managedBy) => managedBy.Length > 0 ? "Access code" : "Fleet code";

    internal static string Noun(string managedBy) => managedBy.Length > 0 ? "access code" : "fleet code";

    internal static string CopyName(string managedBy) => $"Copy {Noun(managedBy)}";

    internal static string ManagedStatus(string managedBy, string keyId)
        => $"This PC's own code (fingerprint {keyId}). Only {managedBy}'s fleet has it.";

    internal static string CopiedHeading(string managedBy) => managedBy.Length > 0 ? "Access code copied" : "Fleet code copied";

    /// <summary>An access code is not entered on another PC: that PC would get full control of this one, not a fleet.</summary>
    internal static string CopiedMessage(string managedBy) => managedBy.Length > 0
        ? $"{managedBy}'s fleet has this code already. Keep it private, like a password: anyone on your network with it can control this PC."
        : "On another PC, open Settings → Fleet → ⋯ → Enter fleet code, and paste it. Keep the code private, like a password: anyone on your network with it can control your PCs.";

    internal static string CopyFailed(string managedBy) => $"Could not copy the {Noun(managedBy)}";

    internal static string RemoveItem(string managedBy) => managedBy.Length > 0 ? "Remove access code…" : "Remove fleet code…";

    internal static string RemoveHeading(string managedBy) => $"Remove the {Noun(managedBy)}?";

    internal static string RemoveMessage(string managedBy) => managedBy.Length > 0
        ? $"{managedBy}'s fleet can no longer view or control this PC. To connect it again, pair it again."
        : "This PC and your other PCs cannot connect to each other until you set a fleet code again.";

    /// <summary>"New fleet code for this PC only": on a managed PC it replaces the access code, which ends the pairing.</summary>
    internal static string NewCodeMessage(string managedBy) => managedBy.Length > 0
        ? $"The new fleet code replaces this PC's access code, so {managedBy}'s fleet can no longer view or control this PC."
        : "Your other PCs keep the current code, and cannot connect to this PC until you enter the new code on them. " +
          "To give every PC a new code, use “Change the fleet code on every PC”.";

    internal static string ManagedCannotChange(string managedBy)
        => $"Only {managedBy}'s fleet changes this PC's access code. To stop it from managing this PC, click Disconnect.";

    /// <summary>
    /// Whether entering <paramref name="key"/> by hand makes this PC forget the PCs it manages one way, so it asks first:
    /// the PC has another code now, and manages PCs (see <see cref="FleetConfig.SetFleetKey"/>).
    /// </summary>
    internal static bool PasteForgetsManagedPcs(FleetConfig config, byte[] key)
        => config.FleetKey is { } current && !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(current, key)
           && config.ManagedPcs.Count > 0;

    internal const string PasteHeading = "Use the new fleet code?";

    /// <param name="managed">The names of the PCs this PC manages one way.</param>
    internal static string PasteMessage(IReadOnlyList<string> managed)
    {
        var one = managed.Count == 1;
        var names = one ? $"{managed[0]} one way" : $"these PCs one way: {string.Join(", ", managed)}";
        return $"This PC manages {names}. With another fleet code, this PC forgets {(one ? "it" : "them")}, so the fleet " +
               $"that has the new code does not get {(one ? "its access code" : "their access codes")}. If your fleet changed " +
               $"its code and this is the new one, {(one ? "it comes" : "they come")} back from the other PCs of the fleet.";
    }

    internal const string PasteConfirm = "Use new code";
}

/// <summary>Settings → AI assistants: "Allow AI assistants" and "Ask before changes".</summary>
internal static class AssistantSwitchForm
{
    internal const string ErrorHeading = "Could not change the AI assistant settings";

    /// <summary>
    /// What helps on that page when this process may not change the switches. <see cref="AssistantSwitches.NotAdministratorText"/>
    /// is for <c>--mcp</c> and <c>ai</c>, and points to the page itself.
    /// </summary>
    internal const string NotAdministratorText =
        "Only a program with administrator rights can change this setting. Start Runner Manager as administrator to change it.";

    /// <summary>
    /// Null when this process may change the switches, otherwise what to show. Asked before anything else is done for
    /// a switch, so that nothing on this PC changes for a switch that cannot be saved.
    /// </summary>
    internal static string? Problem() => AssistantSwitches.Store.CanWrite ? null : NotAdministratorText;

    /// <summary>
    /// Changes one switch or both where they are kept (<see cref="AssistantSwitches"/>), not through fleet.json: they are
    /// not in it, so a fleet.json that cannot be read does not block them, and one that cannot be written cannot report
    /// a failure after they changed. Null when it worked, otherwise what to show.
    /// </summary>
    internal static string? Set(bool? enabled = null, bool? askBeforeChanges = null)
    {
        if (Problem() is { } problem) return problem;
        try
        {
            AssistantSwitches.Set(enabled, askBeforeChanges);
            return null;
        }
        catch (UnauthorizedAccessException) { return NotAdministratorText; }
        catch (Exception ex) { return ex.Message; }
    }
}

/// <summary>Forgetting a PC, worded and done the same way on the dashboard and in Settings.</summary>
internal static class ForgetPc
{
    internal static string Heading(string name) => $"Forget {name}?";

    internal static string Message(string name, bool online) => online
        ? $"{name} is online and still has the fleet code, so it may appear again. To remove it permanently, use “Leave fleet” in its ⋯ menu on {name}."
        : $"{name} is removed from this list. If it is still in the fleet, it appears again when it is found.";

    /// <summary>The addresses added in Settings that point at one of <paramref name="addresses"/>, so the PC is not found there again.</summary>
    internal static List<string> ManualAddressesFor(IEnumerable<string> manualAddresses, IEnumerable<string> addresses)
    {
        var hosts = addresses.Select(FleetNetwork.HostOf).Where(h => h.Length > 0).ToList();
        return manualAddresses.Where(a => hosts.Contains(FleetNetwork.HostOf(a), StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Forgets the PC and the addresses it was added by.</summary>
    internal static void Forget(string name, params string[] addresses)
    {
        MachineRelay.ForgetPeer(name);
        var remove = ManualAddressesFor(FleetConfig.Current.ManualAddresses, addresses);
        if (remove.Count > 0) FleetConfig.Update(c => c.ManualAddresses.RemoveAll(a => remove.Contains(a, StringComparer.OrdinalIgnoreCase)));
    }
}
