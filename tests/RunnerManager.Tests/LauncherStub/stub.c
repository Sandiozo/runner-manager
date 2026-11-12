/*
 * LauncherTestStub.exe: stands in for RunnerManager.exe in LauncherTests. It is not the app and knows none of its
 * arguments: it writes down how it was started, then does what its control file says, for a few seconds at most.
 *
 * Output: <own folder>\<own name>.out, UTF-16LE, each field ended by a null character: the process id (decimal), the
 * command line, the current folder, then every string of the environment block, then one more null (so the file ends
 * like an environment block). Written under a temporary name and renamed, so a reader never sees half of it.
 *
 * Control: <own folder>\<own name>.control, optional ASCII lines, done one after another after the output is written:
 *   spawn NAME   start <own folder>\NAME (another copy of this stub) and do not wait for it
 *   sleep MS     wait MS milliseconds, at most 30,000
 *   exit CODE    exit with CODE (decimal, 0 to 4294967295) at the end; 0 without this line
 * Built without the C runtime and without /GS, like the launcher but with no hardening: it only runs in tests.
 *
 * Both paths come from the stub's own location, never from its arguments or environment, so the tests see the
 * arguments and the environment exactly as the launcher passed them.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <intrin.h>

#define SLEEP_MAX_MS 30000u
#define CONTROL_MAX_BYTES 4096u
#define PATH_CHARS 32768u

static void *Allocate(SIZE_T bytes)
{
    return HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, bytes);
}

static SIZE_T TextLength(const WCHAR *text)
{
    SIZE_T length = 0;
    while (text[length] != 0) length++;
    return length;
}

/* Appends count characters to buffer at *at. */
static void Append(WCHAR *buffer, SIZE_T *at, const WCHAR *text, SIZE_T count)
{
    __movsw((unsigned short *)(buffer + *at), (const unsigned short *)text, count);
    *at += count;
}

/* <own folder>\ followed by name and suffix, in a new buffer. The own path's file name is returned in *ownName
   (without ".exe") when ownName is not NULL. With asFile, a path like C:\... gets the \\?\ form, so that the stub,
   which is not long-path aware, can open files in a folder longer than 260 characters. */
static WCHAR *BesideSelf(const WCHAR *name, const WCHAR *suffix, WCHAR **ownName, BOOL asFile)
{
    WCHAR *own = Allocate(PATH_CHARS * sizeof(WCHAR));
    WCHAR *path = Allocate(PATH_CHARS * 2 * sizeof(WCHAR));
    if (own == NULL || path == NULL) ExitProcess(90);
    DWORD length = GetModuleFileNameW(NULL, own, PATH_CHARS);
    if (length == 0 || length >= PATH_CHARS) ExitProcess(91);
    SIZE_T folder = length;
    while (folder > 0 && own[folder - 1] != L'\\') folder--;
    SIZE_T at = 0;
    if (asFile && length > 2 && own[1] == L':' && own[2] == L'\\') Append(path, &at, L"\\\\?\\", 4);
    Append(path, &at, own, folder);
    if (ownName != NULL) {
        SIZE_T end = length;
        while (end > folder && own[end - 1] != L'.') end--;
        if (end > folder) end--;   /* drop ".exe" */
        else end = length;
        *ownName = Allocate((end - folder + 1) * sizeof(WCHAR));
        if (*ownName == NULL) ExitProcess(90);
        SIZE_T nameAt = 0;
        Append(*ownName, &nameAt, own + folder, end - folder);
    }
    Append(path, &at, name, TextLength(name));
    Append(path, &at, suffix, TextLength(suffix));
    return path;
}

static void WriteDecimal(WCHAR *buffer, SIZE_T *at, DWORD value)
{
    WCHAR digits[10];
    int count = 0;
    do {
        digits[count++] = (WCHAR)(L'0' + value % 10);
        value /= 10;
    } while (value != 0);
    while (count > 0) buffer[(*at)++] = digits[--count];
}

static void WriteOutput(const WCHAR *ownName)
{
    const WCHAR *commandLine = GetCommandLineW();
    DWORD folderChars = GetCurrentDirectoryW(0, NULL);
    WCHAR *folder = Allocate(((SIZE_T)folderChars + 1) * sizeof(WCHAR));
    if (folder == NULL || GetCurrentDirectoryW(folderChars, folder) == 0) ExitProcess(92);
    WCHAR *environment = GetEnvironmentStringsW();
    if (environment == NULL) ExitProcess(93);
    SIZE_T environmentLength = 0;
    while (environment[environmentLength] != 0) environmentLength += TextLength(environment + environmentLength) + 1;
    environmentLength++;   /* the null that ends the block */

    SIZE_T total = 16 + TextLength(commandLine) + 1 + TextLength(folder) + 1 + environmentLength;
    WCHAR *buffer = Allocate(total * sizeof(WCHAR));
    if (buffer == NULL) ExitProcess(90);
    SIZE_T at = 0;
    WriteDecimal(buffer, &at, GetCurrentProcessId());
    at++;
    Append(buffer, &at, commandLine, TextLength(commandLine));
    at++;
    Append(buffer, &at, folder, TextLength(folder));
    at++;
    Append(buffer, &at, environment, environmentLength);

    WCHAR *temporary = BesideSelf(ownName, L".out.tmp", NULL, TRUE);
    WCHAR *final = BesideSelf(ownName, L".out", NULL, TRUE);
    HANDLE file = CreateFileW(temporary, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) ExitProcess(94);
    const BYTE *bytes = (const BYTE *)buffer;
    SIZE_T left = at * sizeof(WCHAR);
    while (left > 0) {
        DWORD chunk = left > 0x10000 ? 0x10000 : (DWORD)left;
        DWORD written = 0;
        if (!WriteFile(file, bytes, chunk, &written, NULL) || written == 0) ExitProcess(95);
        bytes += written;
        left -= written;
    }
    CloseHandle(file);
    if (!MoveFileExW(temporary, final, MOVEFILE_REPLACE_EXISTING)) ExitProcess(96);
}

static BOOL StartsWith(const char *line, SIZE_T length, const char *word, SIZE_T *rest)
{
    SIZE_T i = 0;
    while (word[i] != 0) {
        if (i >= length || line[i] != word[i]) return FALSE;
        i++;
    }
    *rest = i;
    return TRUE;
}

static ULONGLONG ParseNumber(const char *text, SIZE_T length)
{
    ULONGLONG value = 0;
    for (SIZE_T i = 0; i < length && text[i] >= '0' && text[i] <= '9'; i++) {
        value = value * 10 + (ULONGLONG)(text[i] - '0');
        if (value > 0xFFFFFFFFull) return 0xFFFFFFFFull;
    }
    return value;
}

static void Spawn(const char *name, SIZE_T length)
{
    WCHAR wide[261];
    if (length > 260) return;
    for (SIZE_T i = 0; i < length; i++) wide[i] = (WCHAR)(unsigned char)name[i];
    wide[length] = 0;
    WCHAR *path = BesideSelf(wide, L"", NULL, FALSE);
    SIZE_T pathLength = TextLength(path);
    WCHAR *commandLine = Allocate((pathLength + 3) * sizeof(WCHAR));
    if (commandLine == NULL) ExitProcess(90);
    SIZE_T at = 0;
    commandLine[at++] = L'"';
    Append(commandLine, &at, path, pathLength);
    commandLine[at++] = L'"';
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    __stosb((unsigned char *)&startup, 0, sizeof startup);
    __stosb((unsigned char *)&process, 0, sizeof process);
    startup.cb = sizeof startup;
    if (!CreateProcessW(path, commandLine, NULL, NULL, FALSE, 0, NULL, NULL, &startup, &process)) ExitProcess(97);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
}

void StubStart(void)
{
    WCHAR *ownName = NULL;
    BesideSelf(L"", L"", &ownName, FALSE);
    WCHAR *controlPath = BesideSelf(ownName, L".control", NULL, TRUE);
    WriteOutput(ownName);

    DWORD exitCode = 0;
    HANDLE file = CreateFileW(controlPath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file != INVALID_HANDLE_VALUE) {
        char *text = Allocate(CONTROL_MAX_BYTES + 1);
        DWORD size = 0;
        if (text == NULL || !ReadFile(file, text, CONTROL_MAX_BYTES, &size, NULL)) ExitProcess(98);
        CloseHandle(file);
        for (SIZE_T start = 0; start < size; ) {
            SIZE_T end = start;
            while (end < size && text[end] != '\n') end++;
            SIZE_T length = end - start;
            while (length > 0 && (text[start + length - 1] == '\r' || text[start + length - 1] == ' ')) length--;
            const char *line = text + start;
            SIZE_T rest = 0;
            if (StartsWith(line, length, "spawn ", &rest)) Spawn(line + rest, length - rest);
            else if (StartsWith(line, length, "sleep ", &rest)) {
                ULONGLONG ms = ParseNumber(line + rest, length - rest);
                Sleep(ms > SLEEP_MAX_MS ? SLEEP_MAX_MS : (DWORD)ms);
            }
            else if (StartsWith(line, length, "exit ", &rest)) exitCode = (DWORD)ParseNumber(line + rest, length - rest);
            start = end + 1;
        }
    }
    ExitProcess(exitCode);
}
