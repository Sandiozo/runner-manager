using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RunnerManager;

/// <summary>
/// Whether programs without administrator rights can change the install folder: its owner and its rights, those of
/// RunnerManager.exe and of the start helper in it, and those of the folders above it, for anyone except Administrators,
/// SYSTEM and TrustedInstaller. Program Files\RunnerManager is safe; a folder in the user's profile (the default of
/// earlier versions) is not, and there the start helper does not protect the start (see <see cref="Launcher"/>). Nothing
/// is moved: the user is told once per version, and Settings → Updates shows it. Read in the background, once per process.
/// </summary>
internal static class InstallFolderSafety
{
    internal const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>Administrators, SYSTEM and TrustedInstaller: who may change an install that only administrators can change.</summary>
    internal static bool IsTrusted(SecurityIdentifier sid)
        => sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
           || sid.Value.Equals(TrustedInstallerSid, StringComparison.OrdinalIgnoreCase);

    // Access rights as in an access mask. FileSystemRights has no names for the generic ones.
    private const int WriteData = 0x2, AppendData = 0x4, DeleteChild = 0x40, Delete = 0x10000, WriteDac = 0x40000, WriteOwner = 0x80000,
        GenericAll = 0x10000000, GenericWrite = 0x40000000;

    /// <summary>On the install folder: adding files or folders, removing or renaming what is in it or itself, changing its rights or owner.</summary>
    internal const int FolderRights = WriteData | AppendData | DeleteChild | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite;

    /// <summary>On RunnerManager.exe and the start helper: changing, replacing or removing them, changing their rights or owner.</summary>
    internal const int FileRights = WriteData | AppendData | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite;

    /// <summary>On a folder above it: renaming or removing what is in it (the way down to the install folder) or itself, changing its rights or owner.</summary>
    internal const int AncestorRights = DeleteChild | Delete | WriteDac | WriteOwner | GenericAll;

    /// <summary>One entry of an access list.</summary>
    internal sealed record Entry(SecurityIdentifier Sid, int Rights, bool Allow, bool InheritOnly);

    /// <summary>The owner and access list of a file or folder, and the rights that matter for it.</summary>
    internal sealed record Acl(string Path, SecurityIdentifier? Owner, IReadOnlyList<Entry> Entries, int Rights);

    /// <summary>
    /// Why programs without administrator rights can change what <paramref name="acl"/> describes, or null when they cannot:
    /// an owner that is not trusted (an owner can always change the rights), or an allowing entry of someone else that
    /// gives one of the rights that matter. Entries that only pass rights on to what is created inside, and the CREATOR
    /// OWNER and CREATOR GROUP placeholders, do not apply to the object itself. Denying entries are not counted, so a
    /// folder may be reported that a denying entry protects after all.
    /// </summary>
    internal static string? ProblemOf(Acl acl, Func<SecurityIdentifier, bool> trusted)
    {
        if (acl.Owner == null) return $"the owner of {acl.Path} could not be read";
        if (!trusted(acl.Owner)) return $"{Name(acl.Owner)} owns {acl.Path}";
        foreach (var entry in acl.Entries)
        {
            if (!entry.Allow || entry.InheritOnly || IsPlaceholder(entry.Sid) || trusted(entry.Sid)) continue;
            if ((entry.Rights & acl.Rights) != 0) return $"{Name(entry.Sid)} can change {acl.Path}";
        }
        return null;
    }

    private static bool IsPlaceholder(SecurityIdentifier sid)
        => sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid) || sid.IsWellKnown(WellKnownSidType.CreatorGroupSid)
           || sid.Value.Equals("S-1-3-4", StringComparison.OrdinalIgnoreCase); // OWNER RIGHTS: the owner, checked above

    private static string Name(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch (SystemException) { return sid.Value; }
    }

    /// <summary>
    /// What is checked for an install in <paramref name="folder"/>: the folder, RunnerManager.exe and the start helper when
    /// they are there, and every folder above it (the root without <see cref="Delete"/>, which no one can do to it).
    /// </summary>
    internal static List<(string Path, bool IsFolder, int Rights)> Objects(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var objects = new List<(string, bool, int)> { (full, true, FolderRights) };
        foreach (var file in new[] { Path.Combine(full, AppPaths.ExeName), Launcher.PathIn(full) })
            if (File.Exists(file)) objects.Add((file, false, FileRights));
        for (var parent = Path.GetDirectoryName(full); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            objects.Add((parent, true, Path.GetDirectoryName(parent) == null ? AncestorRights & ~Delete : AncestorRights));
        return objects;
    }

    /// <summary>Reads the owner and access list of a file or folder.</summary>
    internal static Acl Read(string path, bool isFolder, int rights)
    {
        FileSystemSecurity security = isFolder
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var entries = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(r => r.IdentityReference is SecurityIdentifier)
            .Select(r => new Entry((SecurityIdentifier)r.IdentityReference, (int)r.FileSystemRights, r.AccessControlType == AccessControlType.Allow,
                r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)))
            .ToList();
        return new Acl(path, owner, entries, rights);
    }

    /// <summary>
    /// Why programs without administrator rights can change the install in <paramref name="folder"/> (see
    /// <see cref="ProblemOf"/>), or null when they cannot. Null too when the rights could not be read: then nothing is said.
    /// </summary>
    internal static string? Check(string folder, Func<SecurityIdentifier, bool>? trusted = null)
    {
        trusted ??= IsTrusted;
        try
        {
            foreach (var (path, isFolder, rights) in Objects(folder))
                if (ProblemOf(Read(path, isFolder, rights), trusted) is { } problem) return problem;
            return null;
        }
        catch (Exception ex)
        {
            MachineRelay.AppendAgentLog($"Could not read the rights of the install folder {folder}: {ex.Message}");
            return null;
        }
    }

    private static Task<string?>? _current;

    /// <summary>
    /// The result of <see cref="Check"/> for the installed copy that runs, read once in the background; null for any other
    /// copy (a build folder, a downloaded setup).
    /// </summary>
    internal static Task<string?> CurrentAsync()
        => _current ??= AppUpdater.IsRunningInstalledCopy ? Task.Run(() => Check(AppUpdater.InstallDir)) : Task.FromResult<string?>(null);

    internal const string ToldCheck = "install-folder-unsafe";

    /// <summary>The Activity line, once per version, for an install that programs without administrator rights can change.</summary>
    internal static string UnsafeActivityText
        => $"Runner Manager is installed in a folder that programs without administrator rights can change. {MoveText}";

    /// <summary>The note in Settings → Updates beside the install folder.</summary>
    internal static string UnsafeNoteText
        => $"Programs without administrator rights can change this folder. {MoveText}";

    /// <summary>How to move the install to Program Files, in the same words as the README and the changelog.</summary>
    private static string MoveText => $"To move it, run the setup again, click Change… and choose {MachineInstall.ProgramFilesDir}.";

    /// <summary>The Activity line on every start of a PC that runs without the start helper.</summary>
    internal const string WithoutStartHelperActivityText =
        "Runner Manager works, but its start is not protected: it runs without its start helper. Antivirus software may have removed or blocked " +
        Launcher.FileName + ". Runner Manager tries again after the next update, or when you run the setup again.";

    /// <summary>The note in Settings → Updates on a PC that runs without the start helper.</summary>
    internal const string WithoutStartHelperNoteText =
        "Runner Manager runs without its start helper here, so its start is not protected. Antivirus software may have removed or blocked " +
        Launcher.FileName + ". Runner Manager tries again after the next update, or when you run the setup again.";

    /// <summary>Whether the installed copy that runs runs without the start helper (see <see cref="Launcher.RunsWithout"/>).</summary>
    internal static bool RunsWithoutStartHelper => AppUpdater.IsRunningInstalledCopy && Launcher.RunsWithout(AppUpdater.InstallDir);
}
