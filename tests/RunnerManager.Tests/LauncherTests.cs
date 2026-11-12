using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RunnerManager.Tests;

/// <summary>
/// RunnerManagerLauncher.exe (src\RunnerManagerLauncher): the scheduled tasks' first stage. It ignores the environment
/// it was given and starts RunnerManager.exe from its own folder with one built from an allow-list. These tests run the
/// real launcher with a stub in place of the app (LauncherStub\stub.c), each in a temp folder of its own, without
/// administrator rights. The stub is never given the app's arguments and lives for seconds at most, because a running
/// Runner Manager on the same PC looks at processes named RunnerManager.exe.
/// </summary>
public class LauncherTests
{
    // The launcher's own exit codes (launcher.c).
    private const uint ExitCommandLine = 0x524C0007;
    private const uint ExitChildMissing = 0x524C0008;
    private const uint ExitChildStart = 0x524C0009;

    private static readonly string[] PassThroughNames = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "GH_TOKEN", "GITHUB_TOKEN"];
    private static readonly string[] IdentityNames = ["USERNAME", "USERDOMAIN", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA"];
    private static readonly string[] LeftOutPrefixes = ["DOTNET_", "CORECLR_", "COR_", "COMPlus_", "COREHOST_"];

    // ---- What the app gets.

    [Fact]
    public void HostileVariables_DoNotReachTheApp()
    {
        const string marker = "hostile-5c1e";
        string[] absent =
        [
            "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_STARTUP_HOOKS", "DOTNET_GCName", "DOTNET_GCPath", "DOTNET_ADDITIONAL_DEPS",
            "DOTNET_SHARED_STORE", "DOTNET_gcServer", "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH",
            "CORECLR_PROFILER_PATH_64", "COR_ENABLE_PROFILING", "COR_PROFILER", "COMPlus_x", "COMPlus_EnableDiagnostics",
            "COREHOST_TRACE", "COREHOST_TRACEFILE", "__COMPAT_LAYER", "GH_HOST", "GH_CONFIG_DIR", "GH_BROWSER", "GH_PATH",
            "GH_EDITOR", "GH_PAGER", "GH_ENTERPRISE_TOKEN", "GIT_SSH", "GIT_SSH_COMMAND", "GIT_CONFIG_GLOBAL", "GIT_EXEC_PATH",
            "RUNNER_MANAGER_STATE_DIR", "RUNNER_MANAGER_ANYTHING", $"RM_LAUNCHER_UNKNOWN_{Guid.NewGuid():N}",
        ];
        // Names the app does get, but never with the launcher's value.
        string[] systemValue = ["PATH", "PATHEXT", "ComSpec", "windir", "SystemRoot", "SystemDrive", "TEMP", "TMP", "ProgramFiles"];
        string[] ownValue = [.. IdentityNames, "PSModulePath", "DOTNET_EnableDiagnostics", "DOTNET_BUNDLE_EXTRACT_BASE_DIR"];
        var environment = absent.Concat(systemValue).Concat(ownValue).ToDictionary(name => name, name => $@"C:\{marker}\{name}");

        using var folder = new LauncherFolder();
        var child = folder.Launch(environment: environment).ChildOrFail();

        Assert.DoesNotContain(child.Block, entry => entry.Contains(marker, StringComparison.OrdinalIgnoreCase));
        foreach (var name in absent) Assert.Null(child.Value(name));
        var system = SystemBlock();
        foreach (var name in systemValue) Assert.Equal(system.Value(name), child.Value(name));
        AssertIdentity(child);
        Assert.Equal(ExpectedModulePath(system.Value("PSModulePath")), child.Value("PSModulePath"));
    }

    /// <summary>
    /// CreateEnvironmentBlock(NULL, FALSE) takes SystemRoot and SystemDrive from its caller and expands the system
    /// variables with them, so without the launcher's own fix a SystemRoot set for the user would move Path, ComSpec,
    /// TEMP, TMP and windir of the elevated app.
    /// </summary>
    [Fact]
    public void SystemRootAndSystemDrive_OfTheLauncher_DoNotChangeTheSystemVariables()
    {
        using var folder = new LauncherFolder();
        var child = folder.Launch(environment: new Dictionary<string, string> { ["SystemRoot"] = @"C:\x-root", ["SystemDrive"] = @"C:\x-drive" }).ChildOrFail();

        var system = SystemBlock();
        foreach (var name in system.Names.Where(name => !IdentityNames.Contains(name, StringComparer.OrdinalIgnoreCase) && name != "PSModulePath"))
            Assert.Equal(system.Value(name), child.Value(name));
        Assert.Equal(ExpectedModulePath(system.Value("PSModulePath")), child.Value("PSModulePath"));
        Assert.DoesNotContain(child.Block, entry => entry.Contains(@"C:\x-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheApp_GetsTheSystemVariables_TheUser_AndTheLaunchersOwn()
    {
        using var folder = new LauncherFolder();
        var child = folder.Launch().ChildOrFail();

        var machinePath = Environment.ExpandEnvironmentVariables(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine)!);
        Assert.Equal(machinePath, child.Value("Path"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows), child.Value("SystemRoot"), ignoreCase: true);
        // The system temp folder, not the user's.
        var machineTemp = Environment.ExpandEnvironmentVariables(Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.Machine)!);
        Assert.Equal(machineTemp, child.Value("TEMP"));
        Assert.Equal(SystemBlock().Value("TMP"), child.Value("TMP"));
        AssertIdentity(child);
        Assert.Equal("0", child.Value("DOTNET_EnableDiagnostics"));
        Assert.Equal(Path.Combine(folder.Folder, ".net"), child.Value("DOTNET_BUNDLE_EXTRACT_BASE_DIR"));
        Assert.Equal(folder.Folder, child.Folder);
    }

    [Fact]
    public void TheApp_GetsNothingElse()
    {
        using var folder = new LauncherFolder();
        var child = folder.Launch(environment: new Dictionary<string, string> { ["GH_TOKEN"] = "test-token-not-real" }).ChildOrFail();

        Assert.Equal(ExpectedNames([.. IdentityNames, "PSModulePath", "DOTNET_EnableDiagnostics", "DOTNET_BUNDLE_EXTRACT_BASE_DIR", "GH_TOKEN"]),
            child.Names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    // ---- PSModulePath: Windows PowerShell started by the app must not load modules from a folder the user can write.

    [Fact]
    public void PSModulePath_IsWindowsPowerShellsOwnModulesFolder_Once()
    {
        using var folder = new LauncherFolder();
        var child = folder.Launch(environment: new Dictionary<string, string> { ["PSModulePath"] = @"C:\elsewhere\Modules" }).ChildOrFail();

        Assert.Single(child.Names, name => name.Equals("PSModulePath", StringComparison.OrdinalIgnoreCase));
        var machine = SystemBlock().Value("PSModulePath");
        Assert.Equal(ExpectedModulePath(machine), child.Value("PSModulePath"));
        Assert.NotEqual(machine, child.Value("PSModulePath"), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What the launcher's PSModulePath is for. Windows PowerShell 5.1 started with exactly the app's environment (as the
    /// app starts it, with administrator rights) looks for modules only in folders that need administrator rights to
    /// change, and still finds its own. Given a user-level value too (HKCU\Environment\PSModulePath, which needs no
    /// administrator rights), PowerShell's own rule still adds no folder of the user's; that rule is asked directly, so
    /// the registry is not touched.
    /// </summary>
    [Fact]
    public void WindowsPowerShell_StartedWithTheAppsEnvironment_LoadsModulesOnlyFromAdministratorFolders()
    {
        using var folder = new LauncherFolder();
        var child = folder.Launch().ChildOrFail();
        var value = child.Value("PSModulePath")!;

        var output = RunWindowsPowerShell(child.Block,
            "$env:PSModulePath",
            "(Get-Command Get-AuthenticodeSignature).Module.Path",
            "$machine = [Environment]::ExpandEnvironmentVariables([Environment]::GetEnvironmentVariable('PSModulePath', 'Machine'))",
            ModulePathFromPowerShell(value, "$machine", PlantedModules));

        AssertAdministratorFoldersOnly(output[0]);
        Assert.StartsWith(PowerShellModules + @"\", output[1], StringComparison.OrdinalIgnoreCase);
        AssertAdministratorFoldersOnly(output[2]);
    }

    /// <summary>
    /// On a PC whose machine value of PSModulePath is the launcher's folder itself, the launcher must spell its value
    /// differently, or PowerShell takes it for the machine value and puts the user's modules folder first. No test can
    /// set the machine value; the test build acts as if it were that folder. PowerShell's own rule is asked what it makes
    /// of the result with that machine value, without and with a user-level value, and real PowerShell must still find
    /// its modules with it.
    /// </summary>
    [Fact]
    public void PSModulePath_DiffersFromAMachineValueThatIsTheFolderItself()
    {
        using var folder = new LauncherFolder(testBuild: true);
        var child = folder.Launch().ChildOrFail();
        var value = child.Value("PSModulePath")!;
        Assert.Equal($"{PowerShellModules};{PowerShellModules}", value);

        var machine = PowerShellText(PowerShellModules);
        var output = RunWindowsPowerShell(child.Block,
            "$env:PSModulePath",
            "(Get-Command Get-AuthenticodeSignature).Module.Path",
            ModulePathFromPowerShell(value, machine, null),
            ModulePathFromPowerShell(value, machine, PlantedModules));

        AssertAdministratorFoldersOnly(output[0]);
        Assert.StartsWith(PowerShellModules + @"\", output[1], StringComparison.OrdinalIgnoreCase);
        AssertAdministratorFoldersOnly(output[2]);
        AssertAdministratorFoldersOnly(output[3]);
    }

    /// <summary>
    /// The names the launcher leaves out of the system variables can only be set machine-wide, which a test must not do.
    /// The test build (LauncherTestBuild.exe, see TestSystemEntries in launcher.c) adds such names to the system
    /// variables before the same filter runs; only RM_LAUNCHER_TEST_BUILD=kept may come through.
    /// </summary>
    [Fact]
    public void SystemVariablesThatMustNotReachTheApp_AreLeftOut()
    {
        using var folder = new LauncherFolder(testBuild: true);
        var child = folder.Launch().ChildOrFail();

        Assert.Equal("kept", child.Value("RM_LAUNCHER_TEST_BUILD"));   // so the test build's entries were added
        Assert.DoesNotContain(child.Block, entry => entry.Contains("RM_LAUNCHER_TEST_BUILD", StringComparison.OrdinalIgnoreCase)
            && entry != "RM_LAUNCHER_TEST_BUILD=kept");
        foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_gcServer", "COMPlus_EnableDiagnostics", "CORECLR_PROFILER_PATH", "COR_ENABLE_PROFILING", "COREHOST_TRACE", "__COMPAT_LAYER", "GH_TOKEN" })
            Assert.Null(child.Value(name));
        Assert.Equal(ExpectedNames([.. IdentityNames, "PSModulePath", "DOTNET_EnableDiagnostics", "DOTNET_BUNDLE_EXTRACT_BASE_DIR", "RM_LAUNCHER_TEST_BUILD"]),
            child.Names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        AssertIdentity(child);
    }

    [Fact]
    public void TheLauncher_HasNoCodeOfTheTestBuild()
    {
        var marker = Encoding.Unicode.GetBytes("RM_LAUNCHER_TEST_BUILD");
        Assert.True(File.ReadAllBytes(TestBuildExe).AsSpan().IndexOf(marker) >= 0, "The test build does not hold its marker, so this test proves nothing.");
        Assert.True(File.ReadAllBytes(LauncherExe).AsSpan().IndexOf(marker) < 0, "RunnerManagerLauncher.exe was built with LAUNCHER_TEST_BUILD.");
    }

    [Fact]
    public void TheBlock_IsSorted_AndHasEachNameOnce_EvenFromAVeryLargeEnvironment()
    {
        var environment = Enumerable.Range(0, 2000).ToDictionary(i => $"RM_LAUNCHER_BULK_{i:D4}", i => new string('v', 200));
        foreach (var name in PassThroughNames) environment[name.ToLowerInvariant()] = "test-value";
        environment["Path"] = @"C:\elsewhere";

        using var folder = new LauncherFolder();
        var child = folder.Launch(environment: environment).ChildOrFail();

        var names = child.Names;
        for (var i = 1; i < names.Count; i++)
            Assert.True(string.Compare(names[i - 1], names[i], StringComparison.OrdinalIgnoreCase) < 0, $"'{names[i - 1]}' is before '{names[i]}'.");
        Assert.DoesNotContain(names, name => name.StartsWith("RM_LAUNCHER_BULK_", StringComparison.Ordinal));
        foreach (var name in PassThroughNames) Assert.Contains(name, names);   // upper case, once
    }

    [Theory]
    [InlineData("HTTP_PROXY", "HTTP_PROXY", "http://proxy.example:8080")]
    [InlineData("https_proxy", "HTTPS_PROXY", "http://user@proxy.example:3128")]
    [InlineData("All_Proxy", "ALL_PROXY", "socks5://proxy.example:1080")]
    [InlineData("no_proxy", "NO_PROXY", "localhost,127.0.0.1,.example.com")]
    [InlineData("GH_TOKEN", "GH_TOKEN", "test-token-not-real")]
    [InlineData("github_token", "GITHUB_TOKEN", "another-test-token = with spaces and ü")]
    public void PassThroughVariables_ReachTheApp_UnderTheirUpperCaseName(string setAs, string name, string value)
    {
        using var folder = new LauncherFolder();
        var child = folder.Launch(environment: new Dictionary<string, string> { [setAs] = value }).ChildOrFail();

        Assert.Contains($"{name}={value}", child.Block);
        Assert.Single(child.Block, entry => NameOf(entry).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PassThroughValues_Of8192Characters_ReachTheApp_AndLongerOnesDoNot()
    {
        var fits = new string('a', 8192);
        using var folder = new LauncherFolder();
        var child = folder.Launch(environment: new Dictionary<string, string>
        {
            ["HTTP_PROXY"] = fits,
            ["HTTPS_PROXY"] = fits + "a",
            ["GH_TOKEN"] = new string('t', 20_000),
        }).ChildOrFail();

        Assert.Equal(fits, child.Value("HTTP_PROXY"));
        Assert.Null(child.Value("HTTPS_PROXY"));
        Assert.Null(child.Value("GH_TOKEN"));
    }

    [Theory]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\u0001")]
    [InlineData("\u001B")]
    [InlineData("\u007F")]
    [InlineData("\u0085")]
    [InlineData("\u009F")]
    public void PassThroughValues_WithAControlCharacter_DoNotReachTheApp(string control)
    {
        using var folder = new LauncherFolder();
        var child = folder.Launch(environment: new Dictionary<string, string>
        {
            ["HTTP_PROXY"] = $"http://proxy.example{control}:8080",
            ["NO_PROXY"] = "localhost",
        }).ChildOrFail();

        Assert.Null(child.Value("HTTP_PROXY"));
        Assert.Equal("localhost", child.Value("NO_PROXY"));
    }

    // ---- Arguments, exit codes, folders.

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("one two  three")]
    [InlineData("\"with spaces\" \"and \\\"quotes\\\" inside\"")]
    [InlineData("C:\\folder\\ \"C:\\folder with spaces\\\\\" next")]
    [InlineData("\"ends with a backslash\\\\\"")]
    [InlineData("ünïcødé ąčę 名前 \"ā b\" 🚀")]
    [InlineData("tab\tseparated\targuments")]
    [InlineData("trailing spaces   ")]
    [InlineData("\"an unclosed quote")]
    [InlineData("a=b %PATH% !x! ^& | < >")]
    public void Arguments_ReachTheApp_Unchanged(string arguments)
    {
        using var folder = new LauncherFolder();
        var child = folder.Launch(arguments).ChildOrFail();

        Assert.Equal(arguments.Length == 0 ? $"\"{folder.Child}\"" : $"\"{folder.Child}\" {arguments}", child.CommandLine);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(42u)]
    [InlineData(259u)]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0xE0434352u)]
    [InlineData(0xFFFFFFFFu)]
    public void TheAppsExitCode_IsTheLaunchersExitCode(uint code)
    {
        using var folder = new LauncherFolder();
        folder.Control("RunnerManager", $"exit {code}");

        Assert.Equal(unchecked((int)code), folder.Launch().ExitCode);
    }

    [Fact]
    public void WithoutTheApp_TheLauncherExitsWithItsOwnCode_AndStartsNothing()
    {
        using var folder = new LauncherFolder(withChild: false);

        Assert.Equal(unchecked((int)ExitChildMissing), folder.Launch().ExitCode);
    }

    [Fact]
    public void AnAppThatCannotStart_GivesTheLaunchersOwnCode()
    {
        using var folder = new LauncherFolder(withChild: false);
        File.WriteAllText(folder.Child, "not a program");

        Assert.Equal(unchecked((int)ExitChildStart), folder.Launch().ExitCode);
    }

    [Fact]
    public void TheLauncher_WorksFromAFolderWithSpacesAndNonAsciiLetters()
    {
        using var folder = new LauncherFolder(@"Runner Manager äöü ąčę 名前");
        var run = folder.Launch("x");

        Assert.Equal(0, run.ExitCode);
        var child = run.ChildOrFail();
        Assert.Equal($"\"{folder.Child}\" x", child.CommandLine);
        Assert.Equal(folder.Folder, child.Folder);
        Assert.Equal(Path.Combine(folder.Folder, ".net"), child.Value("DOTNET_BUNDLE_EXTRACT_BASE_DIR"));
    }

    /// <summary>
    /// A path longer than 260 characters, started in its \\?\ form: the only form in which a program that is not
    /// long-path aware (as this test host) can start one, and it works whatever LongPathsEnabled says, so this needs no
    /// skip. The plain form needs a long-path-aware parent and LongPathsEnabled; see the report of the launcher's tests.
    /// </summary>
    [Fact]
    public void TheLauncher_WorksFromAPathLongerThan260Characters()
    {
        using var folder = new LauncherFolder(Path.Combine(new string('l', 100), new string('o', 100), new string('n', 60) + " g"));
        Assert.True(folder.Launcher.Length > 300);
        var run = folder.LaunchNative("RunnerManagerLauncher.exe x", @"\\?\" + folder.Launcher, Path.GetTempPath());

        Assert.Equal(0, run.ExitCode);
        var child = run.ChildOrFail();
        Assert.Equal($"\"\\\\?\\{folder.Child}\" x", child.CommandLine);
        Assert.Equal(@"\\?\" + Path.Combine(folder.Folder, ".net"), child.Value("DOTNET_BUNDLE_EXTRACT_BASE_DIR"));
        // Windows starts no process in a folder that long, so the app starts in System32.
        Assert.Equal(Environment.SystemDirectory, child.Folder, ignoreCase: true);
    }

    /// <summary>The longest current folder Windows starts a process in is 258 characters with its backslash.</summary>
    [Theory]
    [InlineData(258, true)]
    [InlineData(259, false)]
    public void TheAppStartsInTheLaunchersFolder_UpToTheLongestFolderWindowsAllows(int length, bool ownFolder)
    {
        // As the launcher sees it: \\?\ + folder + \.
        using var folder = new LauncherFolder(folderLength: length - 5);
        var run = folder.LaunchNative("L", @"\\?\" + folder.Launcher, Path.GetTempPath());

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(ownFolder ? @"\\?\" + folder.Folder : Environment.SystemDirectory, run.ChildOrFail().Folder, ignoreCase: true);
    }

    [Fact]
    public void ACommandLineOfExactly32767Characters_IsPassedOn_AndALongerOneStopsTheLaunch()
    {
        using var folder = new LauncherFolder();
        // The app's command line is "<child>" <tail> plus the terminating null; CreateProcessW takes 32,767 characters with
        // it. The launcher itself is started as "L <tail>", which stays short enough either way.
        var fits = new string('x', 32_763 - folder.Child.Length);

        var run = folder.LaunchNative($"L {fits}");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"\"{folder.Child}\" {fits}", run.ChildOrFail().CommandLine);

        File.Delete(folder.OutputOf("RunnerManager"));
        run = folder.LaunchNative($"L {fits}x");
        Assert.Equal(unchecked((int)ExitCommandLine), run.ExitCode);
        Assert.Null(run.Child);
    }

    // ---- The job: ending the launcher ends the app, not what the app started.

    [Fact]
    public void EndingTheLauncher_EndsTheApp_ButNotAProcessTheAppStarted()
    {
        using var folder = new LauncherFolder();
        folder.AddStub("LauncherGrandchild.exe");
        folder.Control("RunnerManager", "spawn LauncherGrandchild.exe\nsleep 20000");
        folder.Control("LauncherGrandchild", "sleep 20000");

        using var launcher = folder.Start();
        Process? child = null, grandchild = null;
        try
        {
            child = Process.GetProcessById(StubRun.WaitFor(folder.OutputOf("RunnerManager")).ProcessId);
            grandchild = Process.GetProcessById(StubRun.WaitFor(folder.OutputOf("LauncherGrandchild")).ProcessId);

            launcher.Kill();
            Assert.True(launcher.WaitForExit(10_000), "The launcher did not end.");
            Assert.True(child.WaitForExit(10_000), "The app kept running after the launcher was ended.");
            Thread.Sleep(500);
            Assert.False(grandchild.HasExited, "A process the app started was ended with the launcher.");
        }
        finally
        {
            foreach (var process in new[] { grandchild, child })
            {
                if (process == null) continue;
                try { if (!process.HasExited) process.Kill(); process.WaitForExit(5000); } catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
    }

    [Fact]
    public void WhenTheAppExits_TheLauncherExits_WhileAProcessTheAppStartedRunsOn()
    {
        using var folder = new LauncherFolder();
        folder.AddStub("LauncherGrandchild.exe");
        folder.Control("RunnerManager", "spawn LauncherGrandchild.exe\nexit 3");
        folder.Control("LauncherGrandchild", "sleep 5000");

        var watch = Stopwatch.StartNew();
        var run = folder.Launch();
        Assert.Equal(3, run.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"The launcher waited {watch.Elapsed} for a process the app started.");
        using var grandchild = Process.GetProcessById(StubRun.WaitFor(folder.OutputOf("LauncherGrandchild")).ProcessId);
        try { Assert.False(grandchild.HasExited); }
        finally
        {
            try { grandchild.Kill(); grandchild.WaitForExit(5000); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void TheRunningLauncher_HasControlFlowGuardOn()
    {
        using var folder = new LauncherFolder();
        folder.Control("RunnerManager", "sleep 1500");
        using var launcher = folder.Start();
        StubRun.WaitFor(folder.OutputOf("RunnerManager"));

        Assert.True(GetProcessMitigationPolicy(launcher.Handle, ProcessControlFlowGuardPolicy, out var flags, sizeof(uint)),
            $"GetProcessMitigationPolicy failed: {Marshal.GetLastPInvokeError()}");
        Assert.True((flags & 1) != 0, "Control Flow Guard is off in the launcher's process.");
        Assert.True(launcher.WaitForExit(30_000));
        Assert.Equal(0, launcher.ExitCode);
    }

    /// <summary>
    /// A broken file named like each DLL the launcher loads, put beside it, changes nothing: Windows looks for the exe's
    /// own imports and theirs only in System32 (/DEPENDENTLOADFLAG:0x800), and the launcher asks the same for what is
    /// loaded later (SetDefaultDllDirectories). Without the flag Windows takes USERENV.dll from the launcher's folder and
    /// stops the launcher with a load error before any of its code runs.
    /// </summary>
    [Fact]
    public void DllsPutBesideTheLauncher_AreNotLoaded()
    {
        List<string> loaded;
        using (var folder = new LauncherFolder())
        {
            folder.Control("RunnerManager", "sleep 3000");
            using var launcher = folder.Start();
            StubRun.WaitFor(folder.OutputOf("RunnerManager"));
            launcher.Refresh();
            loaded = launcher.Modules.Cast<ProcessModule>().Select(module => module.FileName)
                .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(launcher.WaitForExit(30_000));
            Assert.Equal(0, launcher.ExitCode);
        }
        var userenv = Assert.Single(loaded, path => Path.GetFileName(path).Equals("userenv.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Environment.SystemDirectory, Path.GetDirectoryName(userenv), ignoreCase: true);

        using var planted = new LauncherFolder();
        foreach (var path in loaded) File.WriteAllText(Path.Combine(planted.Folder, Path.GetFileName(path)), "not a DLL");
        var names = string.Join(", ", loaded.Select(Path.GetFileName));
        using var started = planted.Start();
        if (!started.WaitForExit(15_000))
        {
            try { started.Kill(); started.WaitForExit(5000); } catch (InvalidOperationException) { }
            Assert.Fail($"The launcher did not end within 15 seconds with these files beside it, so Windows probably loaded one: {names}");
        }
        Assert.True(started.ExitCode == 0, $"The launcher exited with 0x{started.ExitCode:X8} with these files beside it, so Windows loaded one: {names}");
        Assert.True(File.Exists(planted.OutputOf("RunnerManager")), "The app was not started.");
    }

    // ---- The exe and its source.

    [Fact]
    public void TheLauncher_ImportsOnlyTheseFunctions()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["KERNEL32.dll"] =
            [
                "AssignProcessToJobObject", "CloseHandle", "CompareStringOrdinal", "CreateJobObjectW", "CreateProcessW", "ExitProcess",
                "GetCommandLineW", "GetCurrentProcess", "GetEnvironmentVariableW", "GetExitCodeProcess", "GetLastError",
                "GetModuleFileNameW", "GetProcessHeap", "GetSystemDirectoryW", "GetSystemWindowsDirectoryW", "HeapAlloc", "HeapFree",
                "ResumeThread", "SetDefaultDllDirectories", "SetEnvironmentStringsW", "SetInformationJobObject", "TerminateProcess",
                "WaitForSingleObject",
            ],
            ["ADVAPI32.dll"] = ["GetTokenInformation", "LookupAccountSidW", "OpenProcessToken"],
            ["USERENV.dll"] = ["CreateEnvironmentBlock", "DestroyEnvironmentBlock", "GetUserProfileDirectoryW"],
            ["SHELL32.dll"] = ["SHGetKnownFolderPath"],
            ["ole32.dll"] = ["CoTaskMemFree"],
        };
        var imports = PeImports(LauncherExe);

        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), imports.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        foreach (var (dll, functions) in expected) Assert.Equal(functions.Order(StringComparer.Ordinal), imports[dll].Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheLauncher_IsAHardenedWindowsProgram_WithItsVersionAndManifest()
    {
        var bytes = File.ReadAllBytes(LauncherExe);
        using var pe = new PEReader(new MemoryStream(bytes));
        var headers = pe.PEHeaders;
        var header = headers.PEHeader!;

        Assert.Equal(Machine.Amd64, headers.CoffHeader.Machine);
        Assert.Equal(Subsystem.WindowsGui, header.Subsystem);   // no console window
        var characteristics = (ushort)header.DllCharacteristics;
        const ushort guardCf = 0x4000;
        foreach (var flag in new[] { (ushort)DllCharacteristics.DynamicBase, (ushort)DllCharacteristics.NxCompatible, (ushort)DllCharacteristics.HighEntropyVirtualAddressSpace, guardCf })
            Assert.True((characteristics & flag) == flag, $"DLL characteristic 0x{flag:X4} is missing.");
        Assert.Equal(0, header.DelayImportTableDirectory.Size);
        Assert.Equal(0, header.ThreadLocalStorageTableDirectory.Size);   // no TLS callbacks before the entry point

        // The load configuration: imports from System32 only (DependentLoadFlags), and Control Flow Guard.
        var config = pe.GetSectionData(header.LoadConfigTableDirectory.RelativeVirtualAddress).GetReader();
        var size = config.ReadUInt32();
        Assert.True(size >= 148);
        config.Offset = 78;
        Assert.Equal(0x0800, config.ReadUInt16());
        config.Offset = 112;
        Assert.NotEqual(0ul, config.ReadUInt64());   // GuardCFCheckFunctionPointer
        config.Offset = 144;
        Assert.True((config.ReadUInt32() & 0x100) != 0, "The image is not instrumented for Control Flow Guard.");

        var debug = pe.ReadDebugDirectory();
        Assert.DoesNotContain(debug, entry => entry.Type == DebugDirectoryEntryType.CodeView);   // no PDB path in the bytes
        Assert.Contains(debug, entry => entry.Type == DebugDirectoryEntryType.Reproducible);
        var cet = Assert.Single(debug, entry => (int)entry.Type == 20);   // IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS
        Assert.True((BitConverter.ToUInt32(bytes, cet.DataPointer) & 1) != 0, "The image is not marked CET compatible.");

        var version = FileVersionInfo.GetVersionInfo(LauncherExe);
        Assert.Equal("Runner Manager", version.ProductName);
        Assert.Equal("Starts Runner Manager with a clean environment", version.FileDescription);

        var text = Encoding.ASCII.GetString(bytes);
        Assert.Contains("<requestedExecutionLevel level=\"asInvoker\" uiAccess=\"false\" />", text);
        Assert.Contains(">true</longPathAware>", text);
        Assert.DoesNotContain("requireAdministrator", text);
    }

    [Fact]
    public void TheLauncherSource_ReadsTheEnvironmentOnlyInThePassThroughHelper()
    {
        var folder = Path.Combine(RepoRoot(), "src", "RunnerManagerLauncher");
        var raw = File.ReadAllText(Path.Combine(folder, "launcher.c"));
        var code = StripComments(raw, stripStrings: true);
        string[] banned =
        [
            @"GetEnvironmentStrings\w*", @"ExpandEnvironmentStrings\w*", @"_?w?getenv\w*", @"_w?dupenv\w*", @"SearchPath\w*", @"LoadLibrary\w*",
            @"GetProcAddress", @"ShellExecute\w*", @"WinExec", @"_?w?system", @"_w?spawn\w*", @"_w?exec\w*", @"CreateProcessAsUser\w*",
            @"CreateProcessWith\w*", @"CreateProcessA", @"SetEnvironmentVariable\w*", @"SetCurrentDirectory\w*", @"SetDllDirectory\w*",
            @"AddDllDirectory", @"Reg\w+", @"CreateFile\w*", @"WriteFile", @"MessageBox\w*", @"CreateWindow\w*",
        ];
        foreach (var file in new[] { "launcher.c", "loadcfg.c" })
        {
            var text = StripComments(File.ReadAllText(Path.Combine(folder, file)), stripStrings: true);
            foreach (var pattern in banned) Assert.DoesNotMatch(new Regex($@"\b{pattern}\s*\("), text);
        }

        // GetEnvironmentVariableW once, in ReadPassThroughValue, which is called once, with the pass-through names.
        var reads = Regex.Matches(code, @"\bGetEnvironmentVariable\w*\s*\(");
        var body = FunctionBody(code, "ReadPassThroughValue");
        Assert.True(reads.Count == 1 && reads[0].Index > body.Start && reads[0].Index < body.End,
            "The launcher's environment must be read only in ReadPassThroughValue.");
        Assert.Single(Regex.Matches(code, @"\bReadPassThroughValue\s*\(PassThroughNames\[i\], buffer\)"));
        Assert.Equal(2, Regex.Matches(code, @"\bReadPassThroughValue\s*\(").Count);   // the definition and that call
        var names = Regex.Match(StripComments(raw, stripStrings: false), @"PassThroughNames\[\]\s*=\s*\{([^}]*)\}").Groups[1].Value;
        Assert.Equal(PassThroughNames, Regex.Matches(names, "L\"([^\"]*)\"").Select(m => m.Groups[1].Value));

        // The launcher's own environment is replaced once, with SystemDrive and SystemRoot from the Windows folder.
        var replace = FunctionBody(code, "ReplaceOwnEnvironment");
        var sets = Regex.Matches(code, @"\bSetEnvironmentStrings\w*\s*\(");
        Assert.True(sets.Count == 1 && sets[0].Index > replace.Start && sets[0].Index < replace.End,
            "The launcher's environment must be replaced only in ReplaceOwnEnvironment.");
        Assert.Matches(@"\bGetSystemWindowsDirectoryW\(windows, size\)", code[replace.Start..replace.End]);

        // In Run, in this order: the pass-through values, the replacement, the system block, the user's identity.
        var run = FunctionBody(code, "Run");
        int At(string call)
        {
            var found = Regex.Matches(code[run.Start..run.End], $@"\b{call}\s*\(");
            Assert.True(found.Count == 1, $"Run calls {call} {found.Count} times.");
            return found[0].Index;
        }
        Assert.True(At("ReadPassThrough") < At("ReplaceOwnEnvironment"));
        Assert.True(At("ReplaceOwnEnvironment") < At("CreateEnvironmentBlock"));
        Assert.True(At("CreateEnvironmentBlock") < At("AddIdentity"));
        Assert.True(At("CreateEnvironmentBlock") < At("AddModulePath"));   // it compares with the system block's value

        // The system block without the launcher's environment, and the app started with the block built from it.
        Assert.Single(Regex.Matches(code, @"\bCreateEnvironmentBlock\s*\("));
        Assert.Matches(@"\bCreateEnvironmentBlock\(&systemBlock, NULL, FALSE\)", code);
        Assert.Single(Regex.Matches(code, @"\bCreateProcess\w*\s*\("));
        Assert.Matches(@"\bCreateProcessW\(childPath, commandLine, NULL, NULL, FALSE, CREATE_UNICODE_ENVIRONMENT \| CREATE_SUSPENDED,\s*block, folder, &startup, &child\)", code);
    }

    // ---- Helpers.

    private static void AssertIdentity(StubRun child)
    {
        Assert.Equal(Environment.UserName, child.Value("USERNAME"));
        Assert.Equal(Environment.UserDomainName, child.Value("USERDOMAIN"));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(profile, child.Value("USERPROFILE"));
        Assert.Equal(profile[..2], child.Value("HOMEDRIVE"));
        Assert.Equal(profile[2..], child.Value("HOMEPATH"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), child.Value("APPDATA"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), child.Value("LOCALAPPDATA"));
    }

    /// <summary>
    /// The names the app gets: the system variables less those the launcher leaves out or sets itself, then
    /// <paramref name="own"/>, sorted.
    /// </summary>
    private static IEnumerable<string> ExpectedNames(IEnumerable<string> own)
        => SystemBlock().Names
            .Where(name => !LeftOutPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Where(name => !name.Equals("__COMPAT_LAYER", StringComparison.OrdinalIgnoreCase))
            .Where(name => !IdentityNames.Contains(name, StringComparer.OrdinalIgnoreCase) && !PassThroughNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Where(name => !name.Equals("PSModulePath", StringComparison.OrdinalIgnoreCase))
            .Concat(own)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows PowerShell's own modules folder, in System32.</summary>
    private static string PowerShellModules => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\Modules");

    /// <summary>A folder in the user's profile, as a user-level PSModulePath could name it.</summary>
    private static string PlantedModules => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "planted-modules");

    /// <summary>
    /// The launcher's PSModulePath: <see cref="PowerShellModules"/>, or that folder twice when the machine value has as
    /// many characters, so that the two are never equal.
    /// </summary>
    private static string ExpectedModulePath(string? machineValue)
        => machineValue?.Length == PowerShellModules.Length ? $"{PowerShellModules};{PowerShellModules}" : PowerShellModules;

    /// <summary>
    /// Every folder in a PSModulePath is in the Windows folder or in Program Files (which need administrator rights to
    /// change), and none is in the user's profile.
    /// </summary>
    private static void AssertAdministratorFoldersOnly(string modulePath)
    {
        string[] allowed = [Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)];
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = modulePath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(folders);
        foreach (var folder in folders)
        {
            Assert.False(IsIn(folder, profile), $"PSModulePath has {folder}, in the user's profile: {modulePath}");
            Assert.True(allowed.Any(root => IsIn(folder, root)), $"PSModulePath has {folder}, outside the Windows and Program Files folders: {modulePath}");
        }

        static bool IsIn(string path, string root) => Path.GetFullPath(path).StartsWith(root.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A PowerShell string literal of <paramref name="text"/>.</summary>
    private static string PowerShellText(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>
    /// A PowerShell line that prints the PSModulePath Windows PowerShell 5.1 makes for itself when it is given
    /// <paramref name="value"/>, with the machine value <paramref name="machineExpression"/> and the user-level value
    /// <paramref name="user"/> (null: none). It runs PowerShell's own rule, the public ModuleIntrinsics.GetModulePath that
    /// PowerShell calls with the three values when it starts; null from it means it keeps the value it was given.
    /// </summary>
    private static string ModulePathFromPowerShell(string value, string machineExpression, string? user)
        => $"$path = [System.Management.Automation.ModuleIntrinsics]::GetModulePath({PowerShellText(value)}, {machineExpression}, " +
           $"{(user == null ? "[NullString]::Value" : PowerShellText(user))}); if ($path) {{ $path }} else {{ {PowerShellText(value)} }}";

    /// <summary>
    /// Runs 64-bit Windows PowerShell 5.1, as the app starts it, with exactly <paramref name="environment"/> and these
    /// lines, and returns the lines it wrote.
    /// </summary>
    private static string[] RunWindowsPowerShell(IEnumerable<string> environment, params string[] lines)
    {
        var script = string.Join("\n", ["$ProgressPreference = 'SilentlyContinue'", "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false", .. lines]);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var entry in environment) start.Environment[NameOf(entry)] = entry[(entry.IndexOf('=', 1) + 1)..];

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            process.Kill();
            Assert.Fail("Windows PowerShell did not end within 2 minutes.");
        }
        Assert.True(process.ExitCode == 0 && errors.Result.Trim().Length == 0, $"Windows PowerShell exited with {process.ExitCode}: {errors.Result}");
        return output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NameOf(string entry) => entry[..entry.IndexOf('=', 1)];

    private static string LauncherExe => Binary(Path.Combine(AppContext.BaseDirectory, "launcher"), "RunnerManagerLauncher.exe");

    private static string StubExe => Binary(Path.Combine(AppContext.BaseDirectory, "launcher"), "LauncherTestStub.exe");

    /// <summary>launcher.c built with LAUNCHER_TEST_BUILD. Not copied to the test output: read where build.ps1 puts it.</summary>
    private static string TestBuildExe => Binary(Path.Combine(RepoRoot(), "src", "RunnerManagerLauncher", "bin"), "LauncherTestBuild.exe");

    private static string Binary(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"{name} was not built. The test project builds it with src\\RunnerManagerLauncher\\build.ps1, which needs the Visual C++ " +
                "x64 build tools (Visual Studio or Build Tools with \"Desktop development with C++\"). Install them and build again; " +
                "the build output says whether they were found.");
        return path;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "RunnerManager.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("RunnerManager.sln not found above the test output.");
    }

    /// <summary>The variables Windows gives without a user and without this process's environment, as the launcher reads them.</summary>
    private static StubRun SystemBlock()
    {
        if (!CreateEnvironmentBlock(out var block, IntPtr.Zero, false)) throw new System.ComponentModel.Win32Exception();
        try
        {
            var entries = new List<string>();
            for (var at = block; ;)
            {
                var entry = Marshal.PtrToStringUni(at)!;
                if (entry.Length == 0) break;
                entries.Add(entry);
                at += (entry.Length + 1) * 2;
            }
            return new StubRun(0, "", "", entries);
        }
        finally { DestroyEnvironmentBlock(block); }
    }

    /// <summary>C source with its comments (and, if asked, string and character literals) blanked out, at the same offsets.</summary>
    private static string StripComments(string source, bool stripStrings)
    {
        var text = source.ToCharArray();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
                for (; i < text.Length && text[i] != '\n'; i++) text[i] = ' ';
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                for (; i < end; i++) if (text[i] != '\n') text[i] = ' ';
                i--;
            }
            else if (text[i] is '"' or '\'')
            {
                var quote = text[i];
                for (i++; i < text.Length && text[i] != quote; i++)
                {
                    if (text[i] == '\\') { if (stripStrings) text[i] = ' '; i++; }
                    if (stripStrings && i < text.Length) text[i] = ' ';
                }
            }
        }
        return new string(text);
    }

    /// <summary>Where the body of a C function's definition starts and ends (its braces).</summary>
    private static (int Start, int End) FunctionBody(string code, string name)
    {
        var definition = Regex.Match(code, $@"\b{name}\s*\([^)]*\)\s*\{{");
        Assert.True(definition.Success, $"{name} is not defined.");
        var start = definition.Index + definition.Length - 1;
        var depth = 0;
        for (var i = start; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return (start, i);
        }
        throw new InvalidOperationException($"{name} has no end.");
    }

    /// <summary>The DLLs a PE file imports, each with the names of its functions.</summary>
    private static Dictionary<string, List<string>> PeImports(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var imports = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var directory = pe.PEHeaders.PEHeader!.ImportTableDirectory;
        var descriptors = pe.GetSectionData(directory.RelativeVirtualAddress).GetReader();
        while (true)
        {
            var nameTable = descriptors.ReadInt32();
            descriptors.Offset += 8;   // time stamp, forwarder chain
            var dllName = descriptors.ReadInt32();
            descriptors.ReadInt32();   // import address table
            if (nameTable == 0 && dllName == 0) break;
            var functions = new List<string>();
            var thunks = pe.GetSectionData(nameTable).GetReader();
            for (var thunk = thunks.ReadUInt64(); thunk != 0; thunk = thunks.ReadUInt64())
            {
                Assert.True((thunk & 0x8000000000000000) == 0, "An import by ordinal cannot be checked by name.");
                functions.Add(ReadAsciiz(pe, (int)thunk + 2));   // after the two-byte hint
            }
            imports.Add(ReadAsciiz(pe, dllName), functions);
        }
        return imports;
    }

    private static string ReadAsciiz(PEReader pe, int rva)
    {
        var reader = pe.GetSectionData(rva).GetReader();
        var bytes = new List<byte>();
        for (var b = reader.ReadByte(); b != 0; b = reader.ReadByte()) bytes.Add(b);
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    [DllImport("userenv.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, bool inherit);

    [DllImport("userenv.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DestroyEnvironmentBlock(IntPtr block);

    private const int ProcessControlFlowGuardPolicy = 7;

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetProcessMitigationPolicy(IntPtr process, int policy, out uint flags, nint length);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, CountChars, CountCharsY, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>What the stub wrote: how the launcher started it.</summary>
    private sealed record StubRun(int ProcessId, string CommandLine, string Folder, List<string> Block)
    {
        public List<string> Names => Block.Select(NameOf).ToList();

        /// <summary>The value of a variable (any letter case), or null. Fails when the name is there twice.</summary>
        public string? Value(string name)
            => Block.Where(entry => NameOf(entry).Equals(name, StringComparison.OrdinalIgnoreCase)).Select(entry => entry[(entry.IndexOf('=', 1) + 1)..]).SingleOrDefault();

        public static StubRun Read(string path)
        {
            var text = Encoding.Unicode.GetString(File.ReadAllBytes(path));
            Assert.EndsWith("\0\0", text);
            var fields = text.Split('\0');
            return new StubRun(int.Parse(fields[0]), fields[1], fields[2], fields.Skip(3).TakeWhile(field => field.Length > 0).ToList());
        }

        /// <summary>Waits for a stub that is still running to write its output.</summary>
        public static StubRun WaitFor(string path)
        {
            for (var watch = Stopwatch.StartNew(); watch.Elapsed < TimeSpan.FromSeconds(20); Thread.Sleep(50))
                if (File.Exists(path)) return Read(path);
            throw new TimeoutException($"{path} was not written within 20 seconds.");
        }
    }

    private sealed record LaunchResult(int ExitCode, StubRun? Child)
    {
        public StubRun ChildOrFail()
        {
            Assert.True(Child != null, $"The app was not started (the launcher exited with 0x{ExitCode:X8}).");
            return Child!;
        }
    }

    /// <summary>
    /// A temp folder with a copy of the launcher and, as RunnerManager.exe, a copy of the stub. Deleted when disposed.
    /// </summary>
    private sealed class LauncherFolder : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"rm-launcher-{Guid.NewGuid():N}");

        /// <param name="subfolder">A folder in the temp folder to put the programs in.</param>
        /// <param name="folderLength">Instead: a folder of this many characters (padded with a subfolder).</param>
        /// <param name="withChild">False: no RunnerManager.exe next to the launcher.</param>
        /// <param name="testBuild">True: the test build (LauncherTestBuild.exe) as RunnerManagerLauncher.exe.</param>
        public LauncherFolder(string? subfolder = null, int? folderLength = null, bool withChild = true, bool testBuild = false)
        {
            // Before anything is created: these fail when the binaries were not built.
            var launcher = testBuild ? TestBuildExe : LauncherExe;
            var stub = StubExe;
            if (folderLength != null) subfolder = new string('p', folderLength.Value - _root.Length - 1);
            Folder = subfolder == null ? _root : Path.Combine(_root, subfolder);
            Assert.True(folderLength == null || Folder.Length == folderLength);
            try
            {
                Directory.CreateDirectory(Folder);
                File.Copy(launcher, Launcher);
                if (withChild) File.Copy(stub, Child);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public string Folder { get; }

        public string Launcher => Path.Combine(Folder, "RunnerManagerLauncher.exe");

        public string Child => Path.Combine(Folder, "RunnerManager.exe");

        public string OutputOf(string stub) => Path.Combine(Folder, stub + ".out");

        public void AddStub(string fileName) => File.Copy(StubExe, Path.Combine(Folder, fileName));

        public void Control(string stub, string text) => File.WriteAllText(Path.Combine(Folder, stub + ".control"), text, Encoding.ASCII);

        /// <summary>Starts the launcher with this test process's environment plus <paramref name="environment"/>.</summary>
        public Process Start(string arguments = "", IReadOnlyDictionary<string, string>? environment = null)
        {
            var start = new ProcessStartInfo(Launcher) { Arguments = arguments, UseShellExecute = false };
            // Proxy or token variables of the test process itself must not decide what the app gets.
            foreach (var name in PassThroughNames) start.Environment.Remove(name);
            foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
            return Process.Start(start)!;
        }

        public LaunchResult Launch(string arguments = "", IReadOnlyDictionary<string, string>? environment = null)
        {
            using var launcher = Start(arguments, environment);
            if (!launcher.WaitForExit(30_000))
            {
                launcher.Kill();
                throw new TimeoutException("The launcher did not end within 30 seconds.");
            }
            return Result(launcher.ExitCode);
        }

        /// <summary>
        /// Starts the launcher with CreateProcessW: a program path (the launcher's, unless given) and a command line of the
        /// test's own, in this test process's folder unless one is given.
        /// </summary>
        public LaunchResult LaunchNative(string commandLine, string? application = null, string? currentFolder = null)
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcessW(application ?? Launcher, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero,
                    currentFolder, ref startup, out var process))
                throw new System.ComponentModel.Win32Exception();
            try
            {
                Assert.Equal(0u, WaitForSingleObject(process.Process, 30_000));
                Assert.True(GetExitCodeProcess(process.Process, out var exitCode));
                return Result(unchecked((int)exitCode));
            }
            finally
            {
                CloseHandle(process.Thread);
                CloseHandle(process.Process);
            }
        }

        private LaunchResult Result(int exitCode)
        {
            var output = OutputOf("RunnerManager");
            return new LaunchResult(exitCode, File.Exists(output) ? StubRun.Read(output) : null);
        }

        public void Dispose()
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Delete(_root, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20) { Thread.Sleep(250); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            }
        }
    }
}
