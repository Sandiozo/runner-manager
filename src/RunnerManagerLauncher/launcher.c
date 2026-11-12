/*
 * RunnerManagerLauncher.exe: starts RunnerManager.exe from its own folder with an environment built from scratch.
 *
 * Why: Runner Manager's scheduled tasks start this program with administrator rights in the signed-in user's session.
 * Task Scheduler gives it the user's environment, and any program of that user can change it without administrator
 * rights (HKCU\Environment). .NET reads some variables before any of the app's code runs (DOTNET_ROOT,
 * CORECLR_PROFILER_PATH, DOTNET_GCPath, DOTNET_ADDITIONAL_DEPS and more), so handing that environment on would run
 * such a program's code with administrator rights. The launcher ignores the environment it was given and builds a new
 * one, which holds exactly:
 *   - the system variables from CreateEnvironmentBlock(NULL), less the names in IsLeftOutSystemName;
 *   - USERNAME, USERDOMAIN, USERPROFILE, HOMEDRIVE, HOMEPATH, APPDATA and LOCALAPPDATA, from the process token and
 *     Windows functions, never from the environment;
 *   - PSModulePath=<System32>\WindowsPowerShell\v1.0\Modules, so that Windows PowerShell started by the app does not
 *     load modules from the user's Documents folder (see AddModulePath);
 *   - DOTNET_EnableDiagnostics=0 and DOTNET_BUNDLE_EXTRACT_BASE_DIR=<launcher folder>\.net;
 *   - HTTP_PROXY, HTTPS_PROXY, ALL_PROXY, NO_PROXY, GH_TOKEN and GITHUB_TOKEN from the launcher's own environment, as
 *     data, when set, at most 8,192 characters and without control characters. ReadPassThroughValue is the only
 *     place that reads the launcher's own environment.
 * Right after those reads, ReplaceOwnEnvironment replaces the launcher's own environment with SystemDrive and
 * SystemRoot from Windows itself, because CreateEnvironmentBlock reads those two from its caller.
 *
 * The child's arguments are the launcher's command line after its own program name, unchanged. Its current folder is
 * the launcher's folder (System32 when that folder is too long for Windows to start a process in). The launcher waits
 * for the child and exits with the child's exit code. It shows no window, writes no file or registry value and opens no
 * network connection. It has the rights it was started with and gives the child the same: started without
 * administrator rights, it starts the app without them (CreateProcessW passes the launcher's own token on).
 *
 * Exit codes of the launcher itself. Task Scheduler shows them in hex. Runner Manager's own exit codes are 0, 1 and 2,
 * so a child's code is not mistaken for one of these.
 *   0x524C0001  its own path (or, for a very long folder, the System32 path) could not be read
 *   0x524C0002  out of memory
 *   0x524C0003  the DLL search could not be limited to System32
 *   0x524C0004  the system variables could not be read (the Windows or System32 folder, or CreateEnvironmentBlock)
 *   0x524C0005  the user's name, profile folder or AppData folders could not be read
 *   0x524C0006  the environment block could not be put together (a size overflowed, or a name came twice)
 *   0x524C0007  the child's command line would be longer than 32,767 characters
 *   0x524C0008  RunnerManager.exe is not in the launcher's folder
 *   0x524C0009  RunnerManager.exe could not be started
 *   0x524C000A  the child could not be resumed after it was created (it is ended)
 *   0x524C000B  waiting for the child or reading its exit code failed
 * In none of these cases is anything started with the environment the launcher was given.
 *
 * Built without the C runtime (see build.ps1): none of the launcher's own code runs before LauncherStart, and no
 * runtime code reads the environment on the launcher's behalf. Code under #ifdef LAUNCHER_TEST_BUILD is only in the
 * copy built for the tests (see TestSystemEntries), never in RunnerManagerLauncher.exe.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <intrin.h>
#include <userenv.h>
#include <initguid.h>      /* makes knownfolders.h define FOLDERID_RoamingAppData and FOLDERID_LocalAppData */
#include <knownfolders.h>
#include <shlobj.h>

#define EXIT_OWN_PATH            0x524C0001u
#define EXIT_OUT_OF_MEMORY       0x524C0002u
#define EXIT_DLL_SEARCH          0x524C0003u
#define EXIT_SYSTEM_ENVIRONMENT  0x524C0004u
#define EXIT_IDENTITY            0x524C0005u
#define EXIT_BLOCK               0x524C0006u
#define EXIT_COMMAND_LINE        0x524C0007u
#define EXIT_CHILD_MISSING       0x524C0008u
#define EXIT_CHILD_START         0x524C0009u
#define EXIT_CHILD_RESUME        0x524C000Au
#define EXIT_CHILD_WAIT          0x524C000Bu

#define CHILD_FILE_NAME          L"RunnerManager.exe"
#define EXTRACT_FOLDER_NAME      L".net"
/* Windows PowerShell's own modules, in the System32 folder. */
#define POWERSHELL_MODULES       L"\\WindowsPowerShell\\v1.0\\Modules"

/* The largest buffer GetModuleFileNameW is given: Windows paths are at most 32,767 characters. */
#define OWN_PATH_BUFFER_MAX      32768u
/* CreateProcessW takes at most 32,767 characters of command line, including the terminating null. */
#define COMMAND_LINE_MAX_CHARS   32767u
/* The longest current folder, with its trailing backslash, that CreateProcessW starts a process in (MAX_PATH - 2),
   long-path aware or not; a longer one fails with ERROR_DIRECTORY (measured on Windows 11 24H2). */
#define CURRENT_FOLDER_MAX_CHARS 258u
#define PASS_THROUGH_MAX_CHARS   8192u
/* Names are compared with CompareStringOrdinal, which takes int lengths. No real name comes near this. */
#define NAME_MAX_CHARS           32767u
/* Entries the launcher makes itself: 7 for the user, 1 for Windows PowerShell, 2 for .NET, 6 passed through. */
#define OWN_ENTRY_COUNT          16u

/* One "NAME=value" string of the child's environment. */
typedef struct Entry {
    const WCHAR *text;    /* null-terminated; in the system block, or in owned */
    SIZE_T nameLength;    /* characters before the first '=' */
    SIZE_T textLength;    /* characters, without the terminating null */
    WCHAR *owned;         /* the launcher's own copy of text, freed at the end; NULL for a system entry */
} Entry;

typedef struct EntryList {
    Entry *items;
    SIZE_T count;
    SIZE_T capacity;
} EntryList;

/* The variables the child takes from the launcher's environment, under these names. */
static const WCHAR *const PassThroughNames[] = {
    L"HTTP_PROXY", L"HTTPS_PROXY", L"ALL_PROXY", L"NO_PROXY", L"GH_TOKEN", L"GITHUB_TOKEN"
};

/* The names the launcher sets itself. The system block's value of any of them is left out, so each is there once. */
static const WCHAR *const OwnNames[] = {
    L"USERNAME", L"USERDOMAIN", L"USERPROFILE", L"HOMEDRIVE", L"HOMEPATH", L"APPDATA", L"LOCALAPPDATA", L"PSModulePath",
    L"HTTP_PROXY", L"HTTPS_PROXY", L"ALL_PROXY", L"NO_PROXY", L"GH_TOKEN", L"GITHUB_TOKEN"
};

/*
 * Prefixes of names never taken from the system block: the .NET host and runtime read DOTNET_*, COMPlus_* (the older
 * spelling of DOTNET_*), CORECLR_* (profiler), COR_* (profiler; .NET Framework spelling) and COREHOST_* (host tracing)
 * before any app code runs. With __COMPAT_LAYER (below) Windows applies compatibility settings to the process.
 * Only an administrator can set a system variable, and a machine-wide value of these is rare; leaving them out anyway
 * is the safe side, and the app then starts the same way on every PC. DOTNET_EnableDiagnostics and
 * DOTNET_BUNDLE_EXTRACT_BASE_DIR come from the launcher instead.
 */
static const WCHAR *const LeftOutPrefixes[] = { L"DOTNET_", L"COMPlus_", L"CORECLR_", L"COR_", L"COREHOST_" };

#ifdef LAUNCHER_TEST_BUILD
/*
 * The test build only: bin\LauncherTestBuild.exe, which build.ps1 -TestStub compiles from this file with
 * LAUNCHER_TEST_BUILD defined, for LauncherTests. It is never embedded or shipped, and RunnerManagerLauncher.exe is
 * compiled without the define. These strings are added to the system variables as if CreateEnvironmentBlock had
 * returned them, so the tests can check the filter with names that a test cannot set machine-wide. Only
 * RM_LAUNCHER_TEST_BUILD=kept may reach the app.
 */
static const WCHAR TestSystemEntries[] =
    L"DOTNET_ROOT=C:\\RM_LAUNCHER_TEST_BUILD\\dotnet\0"
    L"dotnet_gcServer=1\0"
    L"COMPlus_EnableDiagnostics=1\0"
    L"CORECLR_PROFILER_PATH=C:\\RM_LAUNCHER_TEST_BUILD\\profiler.dll\0"
    L"COR_ENABLE_PROFILING=1\0"
    L"COREHOST_TRACE=1\0"
    L"__COMPAT_LAYER=RunAsInvoker\0"
    L"USERNAME=RM_LAUNCHER_TEST_BUILD\0"
    L"USERPROFILE=C:\\RM_LAUNCHER_TEST_BUILD\0"
    L"PSModulePath=C:\\RM_LAUNCHER_TEST_BUILD\\Modules\0"
    L"GH_TOKEN=RM_LAUNCHER_TEST_BUILD\0"
    L"=C:=C:\\RM_LAUNCHER_TEST_BUILD\0"
    L"RM_LAUNCHER_TEST_BUILD_WITHOUT_EQUALS_SIGN\0"
    L"RM_LAUNCHER_TEST_BUILD=kept\0";   /* and the null that ends the block */
#endif

/* ---- Memory and text. No C runtime, so no memset, memcpy or wcslen; these do the same with compiler intrinsics. */

static void ZeroBytes(void *destination, SIZE_T count)
{
    __stosb((unsigned char *)destination, 0, count);
}

static void CopyChars(WCHAR *destination, const WCHAR *source, SIZE_T count)
{
    __movsw((unsigned short *)destination, (const unsigned short *)source, count);
}

/* Zeroed memory for count items of size bytes, or NULL (also when count * size overflows). */
static void *Allocate(SIZE_T count, SIZE_T size)
{
    if (size != 0 && count > (SIZE_T)-1 / size) return NULL;
    return HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, count * size);
}

static void Free(void *block)
{
    if (block != NULL) HeapFree(GetProcessHeap(), 0, block);
}

/* *total += amount, or FALSE when that overflows. */
static BOOL AddSize(SIZE_T *total, SIZE_T amount)
{
    if (amount > (SIZE_T)-1 - *total) return FALSE;
    *total += amount;
    return TRUE;
}

static SIZE_T TextLength(const WCHAR *text)
{
    SIZE_T length = 0;
    while (text[length] != 0) length++;
    return length;
}

/* A new null-terminated string: first, then second (secondLength characters). NULL when out of memory. */
static WCHAR *Join(const WCHAR *first, SIZE_T firstLength, const WCHAR *second, SIZE_T secondLength)
{
    SIZE_T total = firstLength;
    if (!AddSize(&total, secondLength) || !AddSize(&total, 1)) return NULL;
    WCHAR *joined = Allocate(total, sizeof(WCHAR));
    if (joined == NULL) return NULL;
    CopyChars(joined, first, firstLength);
    CopyChars(joined + firstLength, second, secondLength);
    return joined;   /* the terminating null is there: Allocate zeroes */
}

/*
 * The order of names in an environment block: Windows wants it "sorted alphabetically by name, case-insensitive,
 * Unicode order, without regard to locale", which is CompareStringOrdinal ignoring case. Lengths are at most
 * NAME_MAX_CHARS (checked where an entry is made), so the int casts hold and the call cannot fail. Should it fail
 * anyway, the names count as the same: a name is then left out, or the duplicate check stops the launch.
 */
static int CompareNames(const WCHAR *first, SIZE_T firstLength, const WCHAR *second, SIZE_T secondLength)
{
    int order = CompareStringOrdinal(first, (int)firstLength, second, (int)secondLength, TRUE);
    if (order == CSTR_LESS_THAN) return -1;
    if (order == CSTR_GREATER_THAN) return 1;
    return 0;
}

static BOOL IsName(const WCHAR *name, SIZE_T nameLength, const WCHAR *other)
{
    return CompareNames(name, nameLength, other, TextLength(other)) == 0;
}

static BOOL HasPrefix(const WCHAR *name, SIZE_T nameLength, const WCHAR *prefix)
{
    SIZE_T prefixLength = TextLength(prefix);
    return nameLength >= prefixLength && CompareNames(name, prefixLength, prefix, prefixLength) == 0;
}

/* C0 controls, DEL and C1 controls. */
static BOOL IsControlCharacter(WCHAR c)
{
    return c < 0x20 || (c >= 0x7F && c <= 0x9F);
}

/* ---- The entries of the child's environment. */

/* Adds NAME=value as a string of the launcher's own. */
static DWORD AddOwnEntry(EntryList *list, const WCHAR *name, const WCHAR *value, SIZE_T valueLength)
{
    SIZE_T nameLength = TextLength(name);
    SIZE_T textLength = nameLength;
    SIZE_T bufferLength;
    if (list->count >= list->capacity || nameLength > NAME_MAX_CHARS) return EXIT_BLOCK;
    if (!AddSize(&textLength, 1) || !AddSize(&textLength, valueLength)) return EXIT_BLOCK;
    bufferLength = textLength;
    if (!AddSize(&bufferLength, 1)) return EXIT_BLOCK;
    WCHAR *text = Allocate(bufferLength, sizeof(WCHAR));
    if (text == NULL) return EXIT_OUT_OF_MEMORY;
    CopyChars(text, name, nameLength);
    text[nameLength] = L'=';
    CopyChars(text + nameLength + 1, value, valueLength);
    Entry *entry = &list->items[list->count++];
    entry->text = text;
    entry->owned = text;
    entry->nameLength = nameLength;
    entry->textLength = textLength;
    return 0;
}

static BOOL IsLeftOutSystemName(const WCHAR *name, SIZE_T nameLength)
{
    for (SIZE_T i = 0; i < ARRAYSIZE(LeftOutPrefixes); i++) {
        if (HasPrefix(name, nameLength, LeftOutPrefixes[i])) return TRUE;
    }
    if (IsName(name, nameLength, L"__COMPAT_LAYER")) return TRUE;
    for (SIZE_T i = 0; i < ARRAYSIZE(OwnNames); i++) {
        if (IsName(name, nameLength, OwnNames[i])) return TRUE;
    }
    return FALSE;
}

/* Number of strings in an environment block (a list of null-terminated strings that ends with an empty one). */
static SIZE_T CountStrings(const WCHAR *block)
{
    SIZE_T count = 0;
    for (const WCHAR *p = block; *p != 0; p += TextLength(p) + 1) count++;
    return count;
}

/* The length of the value of name (any letter case) in an environment block, or (SIZE_T)-1 when the block has none. */
static SIZE_T ValueLength(const WCHAR *block, const WCHAR *name)
{
    SIZE_T nameLength = TextLength(name);
    for (const WCHAR *p = block; *p != 0; p += TextLength(p) + 1) {
        SIZE_T textLength = TextLength(p);
        if (textLength > nameLength && p[nameLength] == L'=' && IsName(p, nameLength, name)) return textLength - nameLength - 1;
    }
    return (SIZE_T)-1;
}

/*
 * Adds the system variables, except the names left out. Strings without a name are left out too: Windows keeps the
 * current folder of each drive as "=C:=C:\...", and CreateEnvironmentBlock makes none; a string without '=' is no
 * variable.
 */
static DWORD AddSystemEntries(EntryList *list, const WCHAR *block)
{
    for (const WCHAR *p = block; *p != 0; ) {
        SIZE_T textLength = TextLength(p);
        SIZE_T nameLength = 0;
        while (nameLength < textLength && p[nameLength] != L'=') nameLength++;
        if (nameLength > NAME_MAX_CHARS) return EXIT_BLOCK;   /* before any comparison, which takes an int length */
        if (nameLength > 0 && nameLength < textLength && !IsLeftOutSystemName(p, nameLength)) {
            if (list->count >= list->capacity) return EXIT_BLOCK;
            Entry *entry = &list->items[list->count++];
            entry->text = p;
            entry->owned = NULL;
            entry->nameLength = nameLength;
            entry->textLength = textLength;
        }
        p += textLength + 1;
    }
    return 0;
}

/*
 * The user's identity, from the token and from Windows, never from the environment: USERNAME and USERDOMAIN from the
 * token's account, USERPROFILE from the profile list, HOMEDRIVE and HOMEPATH split from USERPROFILE, APPDATA and
 * LOCALAPPDATA from the known folders. The token is passed to SHGetKnownFolderPath so that it resolves the folders for
 * that user's profile rather than with this process's environment.
 */
static DWORD AddIdentity(EntryList *list, HANDLE token)
{
    DWORD result = EXIT_IDENTITY;
    TOKEN_USER *user = NULL;
    WCHAR *name = NULL;
    WCHAR *domain = NULL;
    WCHAR *profile = NULL;
    PWSTR roaming = NULL;
    PWSTR local = NULL;
    DWORD size = 0;
    DWORD nameChars = 0;
    DWORD domainChars = 0;
    SIZE_T profileLength = 0;
    SID_NAME_USE use;

    /* Each of these Windows functions is called first without a buffer to learn the size it needs. */
    if (GetTokenInformation(token, TokenUser, NULL, 0, &size) || GetLastError() != ERROR_INSUFFICIENT_BUFFER) goto done;
    user = Allocate(size, 1);
    if (user == NULL) { result = EXIT_OUT_OF_MEMORY; goto done; }
    if (!GetTokenInformation(token, TokenUser, user, size, &size)) goto done;

    if (LookupAccountSidW(NULL, user->User.Sid, NULL, &nameChars, NULL, &domainChars, &use)
        || GetLastError() != ERROR_INSUFFICIENT_BUFFER) goto done;
    /* The sizes include the terminating null; one more character keeps a zero at the end whatever the call writes. */
    name = Allocate((SIZE_T)nameChars + 1, sizeof(WCHAR));
    domain = Allocate((SIZE_T)domainChars + 1, sizeof(WCHAR));
    if (name == NULL || domain == NULL) { result = EXIT_OUT_OF_MEMORY; goto done; }
    if (!LookupAccountSidW(NULL, user->User.Sid, name, &nameChars, domain, &domainChars, &use)) goto done;

    size = 0;
    if (GetUserProfileDirectoryW(token, NULL, &size) || GetLastError() != ERROR_INSUFFICIENT_BUFFER) goto done;
    profile = Allocate((SIZE_T)size + 1, sizeof(WCHAR));
    if (profile == NULL) { result = EXIT_OUT_OF_MEMORY; goto done; }
    if (!GetUserProfileDirectoryW(token, profile, &size)) goto done;

    /* KF_FLAG_DONT_VERIFY: the path only, without checking that the folder is there (as .NET's GetFolderPath does). */
    if (FAILED(SHGetKnownFolderPath(&FOLDERID_RoamingAppData, KF_FLAG_DONT_VERIFY, token, &roaming))) goto done;
    if (FAILED(SHGetKnownFolderPath(&FOLDERID_LocalAppData, KF_FLAG_DONT_VERIFY, token, &local))) goto done;

    profileLength = TextLength(profile);
    if ((result = AddOwnEntry(list, L"USERNAME", name, TextLength(name))) != 0) goto done;
    if ((result = AddOwnEntry(list, L"USERDOMAIN", domain, TextLength(domain))) != 0) goto done;
    if ((result = AddOwnEntry(list, L"USERPROFILE", profile, profileLength)) != 0) goto done;
    /* "C:\Users\Name" gives HOMEDRIVE=C: and HOMEPATH=\Users\Name. A profile not on a drive letter (a network path)
       has no such split, and the child then gets neither: better none than a wrong one. */
    if (profileLength >= 3 && profile[1] == L':' && profile[2] == L'\\'
        && ((profile[0] >= L'A' && profile[0] <= L'Z') || (profile[0] >= L'a' && profile[0] <= L'z'))) {
        if ((result = AddOwnEntry(list, L"HOMEDRIVE", profile, 2)) != 0) goto done;
        if ((result = AddOwnEntry(list, L"HOMEPATH", profile + 2, profileLength - 2)) != 0) goto done;
    }
    if ((result = AddOwnEntry(list, L"APPDATA", roaming, TextLength(roaming))) != 0) goto done;
    result = AddOwnEntry(list, L"LOCALAPPDATA", local, TextLength(local));

done:
    CoTaskMemFree(roaming);   /* to be freed whether SHGetKnownFolderPath succeeded or not; NULL is fine */
    CoTaskMemFree(local);
    Free(profile);
    Free(domain);
    Free(name);
    Free(user);
    return result;
}

/*
 * PSModulePath: Windows PowerShell's own modules folder, <System32>\WindowsPowerShell\v1.0\Modules, which only
 * administrators can change. System32 comes from GetSystemDirectoryW, which reads no variable.
 * Why not the system value: the app starts Windows PowerShell 5.1 with administrator rights, and PowerShell puts the
 * user's own modules folder (<Documents>\WindowsPowerShell\Modules, which programs without administrator rights can
 * write) first when the value it is given equals the machine value, as the system block's value does. A module there
 * named like a command of the app's scripts would then run with administrator rights. A value that differs from the
 * machine value PowerShell keeps, adding only <Program Files>\WindowsPowerShell\Modules. A user-level value
 * (HKCU\Environment\PSModulePath) adds nothing either: PowerShell uses it only when the value it is given equals the
 * machine value, the user-level value itself, or the two joined, so here it can only bring in this value and the
 * machine value. (ModuleIntrinsics.GetModulePath of Windows PowerShell 5.1, read and measured on Windows 11 24H2.)
 * 32-bit Windows PowerShell (SysWOW64) does put the user's folder before this value when no user-level value is set;
 * the app starts the 64-bit one.
 * PowerShell compares with String.Equals(OrdinalIgnoreCase), which is false for strings of different lengths. So when
 * the machine value has as many characters as the folder (it may be the folder itself), the child gets the folder
 * twice, "<folder>;<folder>": the same modules, and never equal to the machine value.
 */
static DWORD AddModulePath(EntryList *list, const WCHAR *systemBlock)
{
    SIZE_T modulesLength = TextLength(POWERSHELL_MODULES);
    UINT size = GetSystemDirectoryW(NULL, 0);   /* the size it needs, with the terminating null */
    if (size == 0) return EXIT_SYSTEM_ENVIRONMENT;
    /* Room for the folder twice and a ';' between: size counts a null, so this is a character more than that. */
    WCHAR *value = Allocate(((SIZE_T)size + modulesLength) * 2, sizeof(WCHAR));
    if (value == NULL) return EXIT_OUT_OF_MEMORY;
    DWORD result = EXIT_SYSTEM_ENVIRONMENT;
    UINT length = GetSystemDirectoryW(value, size);
    if (length != 0 && length < size) {
        SIZE_T folderLength = length + modulesLength;
        SIZE_T valueLength = folderLength;
        SIZE_T machineLength = ValueLength(systemBlock, L"PSModulePath");
#ifdef LAUNCHER_TEST_BUILD
        machineLength = folderLength;   /* as on a PC whose machine value is the folder itself, which no test can set */
#endif
        CopyChars(value + length, POWERSHELL_MODULES, modulesLength);
        if (machineLength == folderLength) {
            value[folderLength] = L';';
            CopyChars(value + folderLength + 1, value, folderLength);
            valueLength = folderLength * 2 + 1;
        }
        result = AddOwnEntry(list, L"PSModulePath", value, valueLength);
    }
    Free(value);
    return result;
}

/*
 * The only place the launcher reads its own environment. It reads one of PassThroughNames by name and treats the
 * value as data: copied into the child's block, never expanded, parsed or used. GetEnvironmentVariableW finds the name
 * in any letter case; the child gets it once, under the upper-case name.
 * Returns the value's length, or 0 when the child must not get the variable: not set, empty, longer than
 * PASS_THROUGH_MAX_CHARS, or holding a control character. buffer holds PASS_THROUGH_MAX_CHARS + 1 characters.
 */
static SIZE_T ReadPassThroughValue(const WCHAR *name, WCHAR *buffer)
{
    /* A value that fits gives its length; a longer one gives the size it would need (more than the limit) and is not
       copied. */
    DWORD length = GetEnvironmentVariableW(name, buffer, PASS_THROUGH_MAX_CHARS + 1);
    if (length == 0 || length > PASS_THROUGH_MAX_CHARS) return 0;
    for (DWORD i = 0; i < length; i++) {
        if (IsControlCharacter(buffer[i])) return 0;
    }
    return length;
}

/* Copies the pass-through values the child gets into values[i] (NULL for one it does not get). Runs before
   ReplaceOwnEnvironment, which takes them out of the launcher's environment. */
static DWORD ReadPassThrough(WCHAR *values[])
{
    WCHAR *buffer = Allocate((SIZE_T)PASS_THROUGH_MAX_CHARS + 1, sizeof(WCHAR));
    if (buffer == NULL) return EXIT_OUT_OF_MEMORY;
    DWORD result = 0;
    for (SIZE_T i = 0; i < ARRAYSIZE(PassThroughNames) && result == 0; i++) {
        SIZE_T length = ReadPassThroughValue(PassThroughNames[i], buffer);
        if (length > 0 && (values[i] = Join(buffer, length, L"", 0)) == NULL) result = EXIT_OUT_OF_MEMORY;
    }
    Free(buffer);
    return result;
}

/* The values hold no null character (it is a control character), so TextLength is their length. */
static DWORD AddPassThrough(EntryList *list, WCHAR *const values[])
{
    for (SIZE_T i = 0; i < ARRAYSIZE(PassThroughNames); i++) {
        if (values[i] == NULL) continue;
        DWORD result = AddOwnEntry(list, PassThroughNames[i], values[i], TextLength(values[i]));
        if (result != 0) return result;
    }
    return 0;
}

/*
 * Replaces the launcher's own environment with SystemDrive and SystemRoot alone, both from the Windows folder the
 * system started from (GetSystemWindowsDirectoryW reads no variable).
 * Why: CreateEnvironmentBlock(NULL, FALSE) is less separate from the caller than it looks. It takes SystemRoot and
 * SystemDrive from the calling process's environment and expands the system variables with them: with SystemRoot=C:\x
 * set for the launcher, the child would get Path=...;C:\x\system32;..., ComSpec=C:\x\system32\cmd.exe, TEMP=C:\x\TEMP
 * and windir=C:\x (measured on Windows 11 24H2; no other variable had an effect). After this call, every Windows
 * function the launcher calls sees these two variables and nothing it was given.
 */
static DWORD ReplaceOwnEnvironment(void)
{
    DWORD result = EXIT_SYSTEM_ENVIRONMENT;
    WCHAR *windows = NULL;
    WCHAR *block = NULL;
    SIZE_T driveName = TextLength(L"SystemDrive=");
    SIZE_T rootName = TextLength(L"SystemRoot=");
    SIZE_T at = 0;
    UINT length;
    UINT size = GetSystemWindowsDirectoryW(NULL, 0);   /* the size it needs, with the terminating null */
    if (size == 0) goto done;
    windows = Allocate((SIZE_T)size + 1, sizeof(WCHAR));
    if (windows == NULL) { result = EXIT_OUT_OF_MEMORY; goto done; }
    length = GetSystemWindowsDirectoryW(windows, size);
    if (length < 3 || length >= size || windows[1] != L':' || windows[2] != L'\\') goto done;

    /* "SystemDrive=C:", "SystemRoot=C:\Windows", sorted, then the null that ends the block. At most 32,767 + 30
       characters: no overflow. */
    block = Allocate(driveName + 2 + 1 + rootName + length + 1 + 1, sizeof(WCHAR));
    if (block == NULL) { result = EXIT_OUT_OF_MEMORY; goto done; }
    CopyChars(block + at, L"SystemDrive=", driveName);
    at += driveName;
    CopyChars(block + at, windows, 2);
    at += 2 + 1;
    CopyChars(block + at, L"SystemRoot=", rootName);
    at += rootName;
    CopyChars(block + at, windows, length);
    if (SetEnvironmentStringsW(block)) result = 0;

done:
    Free(block);
    Free(windows);
    return result;
}

/* Insertion sort by name. The list is a few dozen entries (system variables and the launcher's own). */
static void SortEntries(Entry *items, SIZE_T count)
{
    for (SIZE_T i = 1; i < count; i++) {
        Entry moving = items[i];
        SIZE_T j = i;
        while (j > 0 && CompareNames(items[j - 1].text, items[j - 1].nameLength, moving.text, moving.nameLength) > 0) {
            items[j] = items[j - 1];
            j--;
        }
        items[j] = moving;
    }
}

/* The sorted entries as one block: each string with its null, then one more null. */
static DWORD WriteBlock(const EntryList *list, WCHAR **block)
{
    SIZE_T total = 1;
    if (list->count == 0) total = 2;   /* an empty block is two nulls; the launcher always has entries */
    for (SIZE_T i = 0; i < list->count; i++) {
        /* After sorting, a name that is the same as the one before it would be a duplicate. Each source's names are
           unique and the system block's copies of the launcher's names are left out, so this is a bug: stop. */
        if (i > 0 && CompareNames(list->items[i - 1].text, list->items[i - 1].nameLength,
                                  list->items[i].text, list->items[i].nameLength) == 0) return EXIT_BLOCK;
        if (!AddSize(&total, list->items[i].textLength) || !AddSize(&total, 1)) return EXIT_BLOCK;
    }
    WCHAR *out = Allocate(total, sizeof(WCHAR));
    if (out == NULL) return EXIT_OUT_OF_MEMORY;
    SIZE_T at = 0;
    for (SIZE_T i = 0; i < list->count; i++) {
        CopyChars(out + at, list->items[i].text, list->items[i].textLength);
        at += list->items[i].textLength + 1;   /* the null is already there; at + 1 <= total, checked above */
    }
    *block = out;
    return 0;
}

/* ---- The launcher's own path and command line. */

/* The launcher's full path. GetModuleFileNameW silently cuts a path that does not fit (it then returns the buffer
   size), so the buffer grows until the path fits: paths longer than 260 characters work. */
static DWORD ReadOwnPath(WCHAR **path, SIZE_T *length)
{
    for (DWORD capacity = 512; ; capacity *= 2) {
        WCHAR *buffer = Allocate(capacity, sizeof(WCHAR));
        if (buffer == NULL) return EXIT_OUT_OF_MEMORY;
        DWORD got = GetModuleFileNameW(NULL, buffer, capacity);
        if (got > 0 && got < capacity) {
            *path = buffer;
            *length = got;
            return 0;
        }
        Free(buffer);
        if (got == 0 || capacity >= OWN_PATH_BUFFER_MAX) return EXIT_OWN_PATH;
    }
}

/*
 * The arguments: the launcher's own command line after its program name and the spaces or tabs that follow it, by the
 * rule CreateProcess uses for the program name: a quoted name ends at the next quote, an unquoted one at the first space
 * or tab. Quotes and backslashes in the rest are not touched, so the child parses the same arguments the launcher was
 * given. Leading spaces of the tail are dropped (the child's command line puts exactly one space there).
 */
static const WCHAR *CommandTail(const WCHAR *commandLine)
{
    const WCHAR *p = commandLine;
    if (*p == L'"') {
        p++;
        while (*p != 0 && *p != L'"') p++;
        if (*p == L'"') p++;
    } else {
        while (*p != 0 && *p != L' ' && *p != L'\t') p++;
    }
    while (*p == L' ' || *p == L'\t') p++;
    return p;
}

/*
 * The child's current folder: the launcher's folder, with its trailing backslash (so a drive's root folder is "C:\",
 * not "C:"). Windows cannot start a process in a folder longer than CURRENT_FOLDER_MAX_CHARS; from such a folder the
 * child starts in System32 instead, which is where Task Scheduler starts a task that names no folder, and which only
 * administrators can change. GetSystemDirectoryW reads no environment variable.
 */
static DWORD ChildFolder(const WCHAR *ownPath, SIZE_T folderLength, WCHAR **folder)
{
    if (folderLength <= CURRENT_FOLDER_MAX_CHARS) {
        *folder = Join(ownPath, folderLength, L"", 0);
        return *folder != NULL ? 0 : EXIT_OUT_OF_MEMORY;
    }
    UINT size = GetSystemDirectoryW(NULL, 0);   /* the size it needs, with the terminating null */
    if (size == 0) return EXIT_OWN_PATH;
    WCHAR *system = Allocate((SIZE_T)size + 1, sizeof(WCHAR));
    if (system == NULL) return EXIT_OUT_OF_MEMORY;
    UINT length = GetSystemDirectoryW(system, size);
    if (length == 0 || length >= size) {
        Free(system);
        return EXIT_OWN_PATH;
    }
    *folder = system;
    return 0;
}

/* "<child path>" <tail>, or just "<child path>" when there is no tail. A path cannot hold a quote, so quoting it is
   safe. Longer than CreateProcessW takes: stop rather than cut arguments off. */
static DWORD BuildCommandLine(const WCHAR *childPath, SIZE_T childLength, const WCHAR *tail, WCHAR **commandLine)
{
    SIZE_T tailLength = TextLength(tail);
    SIZE_T total = childLength;
    if (!AddSize(&total, 3)) return EXIT_COMMAND_LINE;   /* two quotes and the terminating null */
    if (tailLength > 0 && (!AddSize(&total, 1) || !AddSize(&total, tailLength))) return EXIT_COMMAND_LINE;
    if (total > COMMAND_LINE_MAX_CHARS) return EXIT_COMMAND_LINE;
    WCHAR *out = Allocate(total, sizeof(WCHAR));
    if (out == NULL) return EXIT_OUT_OF_MEMORY;
    SIZE_T at = 0;
    out[at++] = L'"';
    CopyChars(out + at, childPath, childLength);
    at += childLength;
    out[at++] = L'"';
    if (tailLength > 0) {
        out[at++] = L' ';
        CopyChars(out + at, tail, tailLength);
    }
    *commandLine = out;   /* writable, as CreateProcessW wants */
    return 0;
}

/* ---- Starting the child. */

/*
 * Puts the suspended child in a job that ends it when the job's last handle closes: when the launcher ends, also when
 * it is ended (Task Scheduler ending the task ends the app too). SILENT_BREAKAWAY_OK keeps the app's own child processes
 * out of this job, so they are not ended with it. Returns NULL when a job cannot be made or the child not put in it
 * (for example inside a job that does not allow that): the child then runs without one, as the launch matters more.
 */
static HANDLE PutInJob(HANDLE process)
{
    /* On the heap, as every buffer of the launcher: on the stack, /GS would guard it with a cookie, and that needs the
       C runtime (the link then fails, see loadcfg.c). */
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION *limits = Allocate(1, sizeof *limits);
    HANDLE job = limits != NULL ? CreateJobObjectW(NULL, NULL) : NULL;
    if (job != NULL) {
        limits->BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, limits, sizeof *limits)
            || !AssignProcessToJobObject(job, process)) {
            CloseHandle(job);   /* the child is not in it, so this does not end the child */
            job = NULL;
        }
    }
    Free(limits);
    return job;
}

static DWORD Run(void)
{
    DWORD result;
    WCHAR *ownPath = NULL;
    WCHAR *folder = NULL;
    WCHAR *childPath = NULL;
    WCHAR *extractFolder = NULL;
    WCHAR *commandLine = NULL;
    WCHAR *block = NULL;
    void *systemBlock = NULL;
    HANDLE token = NULL;
    HANDLE job = NULL;
    SIZE_T ownLength = 0;
    SIZE_T folderLength = 0;
    SIZE_T capacity = 0;
    DWORD exitCode = 0;
    WCHAR **passThrough = NULL;   /* one value per PassThroughNames entry, on the heap (see PutInJob) */
    EntryList list;
    STARTUPINFOW startup;
    PROCESS_INFORMATION child;
    ZeroBytes(&list, sizeof list);
    ZeroBytes(&startup, sizeof startup);
    ZeroBytes(&child, sizeof child);

    /* DLLs loaded while the launcher runs (shell32 loads some on demand) come from System32 only, never from the
       launcher's folder. The DLLs in the import table are limited the same way by /DEPENDENTLOADFLAG:0x800. */
    if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)) { result = EXIT_DLL_SEARCH; goto done; }

    if ((result = ReadOwnPath(&ownPath, &ownLength)) != 0) goto done;
    folderLength = ownLength;
    while (folderLength > 0 && ownPath[folderLength - 1] != L'\\') folderLength--;
    if (folderLength == 0) { result = EXIT_OWN_PATH; goto done; }
    if ((result = ChildFolder(ownPath, folderLength, &folder)) != 0) goto done;
    childPath = Join(ownPath, folderLength, CHILD_FILE_NAME, TextLength(CHILD_FILE_NAME));
    extractFolder = Join(ownPath, folderLength, EXTRACT_FOLDER_NAME, TextLength(EXTRACT_FOLDER_NAME));
    if (childPath == NULL || extractFolder == NULL) { result = EXIT_OUT_OF_MEMORY; goto done; }

    /* Before any environment work, so a command line that is too long fails the same way every time. */
    result = BuildCommandLine(childPath, folderLength + TextLength(CHILD_FILE_NAME), CommandTail(GetCommandLineW()),
                              &commandLine);
    if (result != 0) goto done;

    /* The pass-through values first: the next step takes them out of the launcher's environment. */
    passThrough = Allocate(ARRAYSIZE(PassThroughNames), sizeof(WCHAR *));
    if (passThrough == NULL) { result = EXIT_OUT_OF_MEMORY; goto done; }
    if ((result = ReadPassThrough(passThrough)) != 0) goto done;
    if ((result = ReplaceOwnEnvironment()) != 0) goto done;

    /* System variables only: no token, and FALSE so that no other variable of this process is copied. The two it does
       read, SystemRoot and SystemDrive, are now Windows' own (ReplaceOwnEnvironment). */
    if (!CreateEnvironmentBlock(&systemBlock, NULL, FALSE)) { systemBlock = NULL; result = EXIT_SYSTEM_ENVIRONMENT; goto done; }

    capacity = CountStrings(systemBlock);
#ifdef LAUNCHER_TEST_BUILD
    capacity += CountStrings(TestSystemEntries);
#endif
    if (!AddSize(&capacity, OWN_ENTRY_COUNT)) { result = EXIT_BLOCK; goto done; }
    list.items = Allocate(capacity, sizeof(Entry));
    if (list.items == NULL) { result = EXIT_OUT_OF_MEMORY; goto done; }
    list.capacity = capacity;

    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_IMPERSONATE | TOKEN_DUPLICATE, &token)) {
        token = NULL;
        result = EXIT_IDENTITY;
        goto done;
    }
    if ((result = AddIdentity(&list, token)) != 0) goto done;
    if ((result = AddModulePath(&list, systemBlock)) != 0) goto done;
    /* The elevated app must not offer the .NET diagnostics pipe, profiler attach or debugger hooks. */
    if ((result = AddOwnEntry(&list, L"DOTNET_EnableDiagnostics", L"0", 1)) != 0) goto done;
    /* A self-contained single-file build then never unpacks its native DLLs into a temp folder and loads them there. */
    if ((result = AddOwnEntry(&list, L"DOTNET_BUNDLE_EXTRACT_BASE_DIR", extractFolder, TextLength(extractFolder))) != 0) goto done;
    if ((result = AddPassThrough(&list, passThrough)) != 0) goto done;
    if ((result = AddSystemEntries(&list, systemBlock)) != 0) goto done;
#ifdef LAUNCHER_TEST_BUILD
    if ((result = AddSystemEntries(&list, TestSystemEntries)) != 0) goto done;
#endif
    SortEntries(list.items, list.count);
    if ((result = WriteBlock(&list, &block)) != 0) goto done;

    /* lpApplicationName is the full path, so nothing is searched for. No handles are inherited. Suspended until it is
       in the job, so it cannot start a process of its own outside the job first. */
    startup.cb = sizeof startup;
    if (!CreateProcessW(childPath, commandLine, NULL, NULL, FALSE, CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
                        block, folder, &startup, &child)) {
        DWORD error = GetLastError();
        result = (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) ? EXIT_CHILD_MISSING : EXIT_CHILD_START;
        ZeroBytes(&child, sizeof child);
        goto done;
    }

    job = PutInJob(child.hProcess);
    if (ResumeThread(child.hThread) == (DWORD)-1) {
        TerminateProcess(child.hProcess, EXIT_CHILD_RESUME);
        result = EXIT_CHILD_RESUME;
        goto done;
    }
    if (WaitForSingleObject(child.hProcess, INFINITE) != WAIT_OBJECT_0 || !GetExitCodeProcess(child.hProcess, &exitCode)) {
        result = EXIT_CHILD_WAIT;
        goto done;
    }
    result = exitCode;

done:
    /* The child has ended here, except after a failed wait: then closing the job ends it, as the launcher ends. */
    if (job != NULL) CloseHandle(job);
    if (child.hThread != NULL) CloseHandle(child.hThread);
    if (child.hProcess != NULL) CloseHandle(child.hProcess);
    if (token != NULL) CloseHandle(token);
    if (systemBlock != NULL) DestroyEnvironmentBlock(systemBlock);
    for (SIZE_T i = 0; i < list.count; i++) Free(list.items[i].owned);
    for (SIZE_T i = 0; passThrough != NULL && i < ARRAYSIZE(PassThroughNames); i++) Free(passThrough[i]);
    Free(passThrough);
    Free(list.items);
    Free(block);
    Free(commandLine);
    Free(extractFolder);
    Free(childPath);
    Free(folder);
    Free(ownPath);
    return result;
}

/* The entry point (/ENTRY:LauncherStart). ExitProcess, not return: returning would end only this thread. */
void LauncherStart(void)
{
    ExitProcess(Run());
}
