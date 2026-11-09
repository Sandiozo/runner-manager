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
        if (host.Length == 0 || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_'))
            return "A PC name or address can only contain letters, digits, '-', '_' and '.'.";
        if (host.Equals(thisPcName, StringComparison.OrdinalIgnoreCase) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("127.", StringComparison.Ordinal) || localAddresses.Contains(host, StringComparer.OrdinalIgnoreCase))
            return "This is the address of this PC. Enter the address of another PC.";
        return null;
    }

    internal static bool SameHost(string a, string b)
        => FleetNetwork.HostOf(a).Equals(FleetNetwork.HostOf(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the last fleet refresh found at the address: the paired PC that answered there, or why none did
    /// (<paramref name="problem"/>, from the placeholder the refresh listed for it).
    /// </summary>
    internal static string Status(string address, IEnumerable<PeerRecord> knownPeers, string? problem, Func<DateTime, string> lastSeen)
    {
        if (knownPeers.FirstOrDefault(p => SameHost(p.Address, address)) is { } peer)
            return $"{peer.Name} answers here  ·  last seen {lastSeen(peer.LastSeenUtc)}";
        return string.IsNullOrWhiteSpace(problem) ? "Not checked yet." : $"No PC in this fleet answered. {problem.Trim().TrimEnd('.')}.";
    }
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
