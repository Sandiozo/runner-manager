using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RunnerManager;

/// <summary>
/// Who owns the folders in a runner's work folder. Git refuses to work in a folder that another account owns ("dubious
/// ownership"), so a Windows service that runs as NETWORK SERVICE fails every job in folders that a console window's
/// user made.
/// </summary>
public static partial class RunnerService
{
    private static readonly SecurityIdentifier NetworkService = new(WellKnownSidType.NetworkServiceSid, null);
    private static readonly SecurityIdentifier LocalService = new(WellKnownSidType.LocalServiceSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>Accounts by the name a service gives (null when it is not known), and account names by SID.</summary>
    private static readonly ConcurrentDictionary<string, SecurityIdentifier?> ServiceAccounts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> AccountNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The account a service runs as, from Win32_Service.StartName, or null when it is not known. Service settings name
    /// the built-in accounts without their space ("NT AUTHORITY\NetworkService"), and "LocalSystem" has no domain.
    /// </summary>
    internal static SecurityIdentifier? ServiceAccountSid(string account)
    {
        var name = account.Trim();
        if (name.Length == 0) return null;
        if (name.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)) return LocalSystem;
        var user = name[(name.LastIndexOf('\\') + 1)..].Replace(" ", string.Empty);
        if (user.Equals("NetworkService", StringComparison.OrdinalIgnoreCase)) return NetworkService;
        if (user.Equals("LocalService", StringComparison.OrdinalIgnoreCase)) return LocalService;
        if (user.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) || user.Equals("System", StringComparison.OrdinalIgnoreCase)) return LocalSystem;
        return ServiceAccounts.GetOrAdd(name, n =>
        {
            try
            {
                var full = n.StartsWith(@".\", StringComparison.Ordinal) ? Environment.MachineName + n[1..] : n;
                return (SecurityIdentifier)new NTAccount(full).Translate(typeof(SecurityIdentifier));
            }
            catch { return null; }
        });
    }

    /// <summary>An account's name, such as "NT AUTHORITY\NETWORK SERVICE", or its SID when it has none.</summary>
    internal static string AccountName(SecurityIdentifier account)
        => AccountNames.GetOrAdd(account.Value, _ =>
        {
            try { return account.Translate(typeof(NTAccount)).Value; }
            catch { return account.Value; }
        });

    /// <summary>The name of the account a service runs as, for the runner's details.</summary>
    private static string ServiceAccountName(string account)
        => ServiceAccountSid(account) is { } sid ? AccountName(sid) : account.Trim();

    /// <summary>
    /// The first of these owners that is not the service's own account, or null. Git also accepts a folder of the
    /// Administrators group for an account in that group: LocalSystem is, and a user account may be, so only the
    /// built-in service accounts, which are not, are held to their own folders there.
    /// </summary>
    internal static SecurityIdentifier? ForeignOwner(SecurityIdentifier service, IEnumerable<SecurityIdentifier> owners)
        => owners.FirstOrDefault(owner => !owner.Equals(service)
            && !(owner.Equals(Administrators) && !service.Equals(NetworkService) && !service.Equals(LocalService)));

    /// <summary>What was read of a work folder: the owners of its folders, and the folder's own last change then.</summary>
    private sealed record OwnerReading(DateTime FolderWrittenUtc, DateTime ReadUtc, SecurityIdentifier[] Owners);
    private static readonly ConcurrentDictionary<string, OwnerReading> OwnerReadings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan OwnerReadingLifetime = TimeSpan.FromMinutes(10);
    /// <summary>A work folder has a few folders (one per repository, and the runner's own); more are not read.</summary>
    private const int MaxOwnerFolders = 50;

    /// <summary>
    /// The owners of the folders in a work folder, not of the work folder itself: "Clean work folder" keeps that one, and
    /// git does not look at it. The runner list is read every few seconds, so this is read again only when a folder was
    /// added or removed in it, and at least every 10 minutes. Empty when the folder is missing or cannot be read.
    /// </summary>
    internal static SecurityIdentifier[] ReadWorkFolderOwners(string workFolder)
    {
        try
        {
            // A folder's last write time changes when an entry in it is added, removed or renamed.
            var written = Directory.GetLastWriteTimeUtc(workFolder);
            if (OwnerReadings.TryGetValue(workFolder, out var known) && known.FolderWrittenUtc == written
                                                                       && DateTime.UtcNow - known.ReadUtc < OwnerReadingLifetime)
                return known.Owners;
            var owners = !Directory.Exists(workFolder) ? Array.Empty<SecurityIdentifier>()
                : new DirectoryInfo(workFolder).EnumerateDirectories()
                    .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint)).Take(MaxOwnerFolders)
                    .Select(OwnerOf).OfType<SecurityIdentifier>().Distinct().ToArray();
            OwnerReadings[workFolder] = new OwnerReading(written, DateTime.UtcNow, owners);
            return owners;
        }
        // The runner list must not fail because an owner cannot be read.
        catch { return Array.Empty<SecurityIdentifier>(); }
    }

    private static SecurityIdentifier? OwnerOf(FileSystemInfo entry)
    {
        try
        {
            FileSystemSecurity security = entry is DirectoryInfo folder
                ? folder.GetAccessControl(AccessControlSections.Owner)
                : ((FileInfo)entry).GetAccessControl(AccessControlSections.Owner);
            return security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch { return null; }
    }

    private static void ForgetWorkFolderOwners(string workFolder) => OwnerReadings.TryRemove(workFolder, out _);

    /// <summary>
    /// The account that owns folders in a runner's work folder although its service runs as another account, or empty.
    /// A work folder outside the runner folder is left out, as "Clean work folder" leaves it.
    /// </summary>
    private static string ForeignWorkFolderOwner(string root, string workFolder, string serviceAccount)
        => IsInsideRunnerFolder(root, workFolder) && ServiceAccountSid(serviceAccount) is { } service
           && ForeignOwner(service, ReadWorkFolderOwners(workFolder)) is { } owner
            ? AccountName(owner) : string.Empty;

    private static bool IsInsideRunnerFolder(string root, string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder).TrimEnd('\\');
            return full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>
    /// The work folder to give to NETWORK SERVICE before config.cmd installs the runner's service, which runs as that
    /// account: when folders in it belong to another account. Null when there is nothing to change.
    /// </summary>
    internal static string? WorkFolderToGive(string root, string workFolder)
    {
        var folder = ResolveWorkFolder(root, workFolder);
        if (!IsInsideRunnerFolder(root, folder)) return null;
        ForgetWorkFolderOwners(folder);
        return ForeignOwner(NetworkService, ReadWorkFolderOwners(folder)) == null ? null : folder;
    }

    /// <summary>
    /// Why the work folder is left to its owners when config.cmd installs the runner's service, or null when it is given
    /// to the service's account. It is given only when the service will run the runner. A console window that runs it,
    /// or a startup entry that is turned on and opens it in one at sign-in, runs its jobs as the signed-in user, who
    /// must keep owning the folders, or git refuses them there: an edit of a runner with a service whose console copy is
    /// down for the moment leaves it a console runner. A conversion, a repair, and an edit of a runner whose console
    /// window ran close that window and turn its startup entries off first, so for them the folder is given.
    /// </summary>
    /// <param name="processes">The runner processes now.</param>
    /// <param name="startupEntries">The startup entries now (<see cref="ReadStartupEntriesNowAsync"/>).</param>
    internal static string? WorkFolderKeptReason(string root, List<ProcessInfo> processes, List<string> startupEntries, IEnumerable<string> allRoots)
    {
        if (ListenerInSession(root, processes))
            return "The work folder was left as it is: a console window runs this runner, and the jobs there need the folders to belong to its user.";
        return ConsoleStartsAt(root, startupEntries, allRoots) is RunnerStart.SignIn or RunnerStart.SignInShared
            ? "The work folder was left as it is: a startup entry opens this runner in a console window when you sign in, and the jobs there need the folders to belong to the signed-in user."
            : null;
    }

    /// <summary>
    /// Gives the work folder to NETWORK SERVICE when folders in it belong to another account, such as the user of the
    /// console window the runner ran in before: its checkouts and caches are kept, and git accepts them in the service.
    /// Owners are changed in place; nothing is deleted, and links are not followed. A failure does not stop the
    /// registration: it is logged, and the runner's details say what is left. Not for a runner that a console window
    /// still runs or opens at sign-in (<see cref="WorkFolderKeptReason"/>), read now: the runner list may be old, and a
    /// conversion turned the startup entries off a moment ago.
    /// </summary>
    private static async Task GiveWorkFolderToServiceAsync(string root, string workFolder, Action<string> log)
    {
        if (WorkFolderToGive(root, workFolder) is not { } folder) return;
        var processes = await Task.Run(() => ReadProcesses());
        var entries = await ReadStartupEntriesNowAsync();
        var others = await Task.Run(() => OtherRunnerRoots(root));
        if (WorkFolderKeptReason(root, processes, entries, others.Append(root)) is { } kept)
        {
            log(kept);
            return;
        }
        var owner = ForeignOwner(NetworkService, ReadWorkFolderOwners(folder))!;
        log($"Folders in the work folder belong to {AccountName(owner)}, and the Windows service runs as {AccountName(NetworkService)}. " +
            "Giving the work folder to the service's account, so that git accepts it…");
        var (changed, failed) = await Task.Run(() =>
        {
            using var privilege = new RestorePrivilege();
            return GiveToAccount(folder, NetworkService, SetOwner);
        });
        ForgetWorkFolderOwners(folder);
        log(failed == 0
            ? $"The work folder belongs to the service's account now ({changed} items changed)."
            : $"{failed} item(s) in the work folder could not be given to the service's account. If its jobs fail with “dubious ownership”, use “Clean work folder”.");
    }

    /// <summary>
    /// Gives a folder and everything in it to an account. A link (a junction or symbolic link) is neither changed nor
    /// followed, so a folder it points to, such as a shared tool cache, stays as it is. Returns how many items were
    /// changed, and how many could not be.
    /// </summary>
    /// <param name="give">Gives one item to the account; returns false when it already belonged to it.</param>
    internal static (int Changed, int Failed) GiveToAccount(string folder, SecurityIdentifier account, Func<FileSystemInfo, SecurityIdentifier, bool> give)
    {
        int changed = 0, failed = 0;
        var top = new DirectoryInfo(folder);
        if (!top.Exists) return (0, 0);
        if (top.Attributes.HasFlag(FileAttributes.ReparsePoint)) return (0, 1);
        var folders = new Stack<DirectoryInfo>();
        folders.Push(top);
        while (folders.Count > 0)
        {
            var current = folders.Pop();
            Give(current);
            List<FileSystemInfo> entries;
            try { entries = current.EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; continue; }
            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (entry is DirectoryInfo directory) folders.Push(directory);
                else Give(entry);
            }
        }
        return (changed, failed);

        void Give(FileSystemInfo entry)
        {
            try { if (give(entry, account)) changed++; }
            // Not allowed (also a missing privilege), not an owner Windows takes, or gone meanwhile.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed++;
            }
        }
    }

    private static bool SetOwner(FileSystemInfo entry, SecurityIdentifier account)
    {
        if (entry is DirectoryInfo folder)
        {
            var security = folder.GetAccessControl(AccessControlSections.Owner);
            if (account.Equals(security.GetOwner(typeof(SecurityIdentifier)))) return false;
            security.SetOwner(account);
            folder.SetAccessControl(security);
            return true;
        }
        var file = (FileInfo)entry;
        var fileSecurity = file.GetAccessControl(AccessControlSections.Owner);
        if (account.Equals(fileSecurity.GetOwner(typeof(SecurityIdentifier)))) return false;
        fileSecurity.SetOwner(account);
        file.SetAccessControl(fileSecurity);
        return true;
    }

    /// <summary>
    /// Turns on this process's restore privilege while it is held: making another account a file's owner needs it.
    /// Administrators have it, turned off. It is turned off again when disposed, unless it was on before.
    /// </summary>
    private sealed class RestorePrivilege : IDisposable
    {
        private const uint AdjustPrivileges = 0x20, Query = 0x8;
        private const int Enabled = 0x2;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TokenPrivilege
        {
            public int Count;
            public long Luid;
            public int Attributes;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege state, int length,
            out TokenPrivilege previous, out int returned);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private readonly IntPtr _token;
        private TokenPrivilege _previous;
        private readonly bool _changed;

        public RestorePrivilege()
        {
            using var self = Process.GetCurrentProcess();
            if (!OpenProcessToken(self.Handle, AdjustPrivileges | Query, out _token)) return;
            var state = new TokenPrivilege { Count = 1, Attributes = Enabled };
            if (!LookupPrivilegeValue(null, "SeRestorePrivilege", out state.Luid)) return;
            // Succeeds also when the privilege is not held; the last error then says so (ERROR_NOT_ALL_ASSIGNED).
            _changed = AdjustTokenPrivileges(_token, false, ref state, Marshal.SizeOf<TokenPrivilege>(), out _previous, out _)
                       && Marshal.GetLastWin32Error() == 0;
        }

        public void Dispose()
        {
            // The previous state lists the privilege only when this changed it.
            if (_changed && _previous.Count == 1)
                AdjustTokenPrivileges(_token, false, ref _previous, Marshal.SizeOf<TokenPrivilege>(), out _, out _);
            if (_token != IntPtr.Zero) CloseHandle(_token);
        }
    }
}
