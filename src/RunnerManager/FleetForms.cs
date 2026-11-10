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
        // The newest refresh did not reach the address.
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

/// <summary>Forgetting a PC, worded and done the same way on the dashboard and in Settings.</summary>
internal static class ForgetPc
{
    internal static string Heading(string name) => $"Forget {name}?";

    internal static string Message(string name, bool online) => online
        ? $"{name} is online and still has the pairing code, so it may appear again. To remove it permanently, use “Leave fleet” in its ⋯ menu on {name}."
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
