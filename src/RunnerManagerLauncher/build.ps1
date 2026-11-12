<#
.SYNOPSIS
    Builds RunnerManagerLauncher.exe with the Visual C++ tools; with -TestStub also the stub the launcher tests use.

.DESCRIPTION
    Finds the newest Visual Studio or Build Tools with the x64 C++ tools through vswhere, takes its build environment
    from vcvars64.bat, and compiles and links into src\RunnerManagerLauncher\bin. Nothing is downloaded. Outputs newer
    than all of their sources and this script are left as they are (-Force builds anyway).
    Works in Windows PowerShell 5.1 and in PowerShell 7.

.PARAMETER TestStub
    Also builds what only the launcher tests use: bin\LauncherTestStub.exe from tests\RunnerManager.Tests\LauncherStub\stub.c,
    and bin\LauncherTestBuild.exe, the launcher compiled from the same sources with LAUNCHER_TEST_BUILD defined (see
    launcher.c). RunnerManagerLauncher.exe is built without that define, whatever the switches.

.PARAMETER IfToolsPresent
    Without the C++ tools: warn, remove outputs that are out of date, and exit with 0 instead of failing. The test
    project uses this so that the app's other tests still build on a PC without the tools; the launcher tests then
    fail and say why.

.PARAMETER Force
    Builds even when the outputs are up to date.
#>
[CmdletBinding()]
param(
    [switch]$TestStub,
    [switch]$IfToolsPresent,
    [switch]$Force
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$repo = [System.IO.Path]::GetFullPath((Join-Path $here '..\..'))
$bin = Join-Path $here 'bin'
$obj = Join-Path $here 'obj'

$launcher = @{
    Name    = 'RunnerManagerLauncher'
    Exe     = Join-Path $bin 'RunnerManagerLauncher.exe'
    Inputs  = @('launcher.c', 'loadcfg.c', 'launcher.rc', 'launcher.manifest' | ForEach-Object { Join-Path $here $_ })
}
$stub = @{
    Name    = 'LauncherTestStub'
    Exe     = Join-Path $bin 'LauncherTestStub.exe'
    Inputs  = @(Join-Path $repo 'tests\RunnerManager.Tests\LauncherStub\stub.c')
}
# Never embedded or shipped: the app embeds bin\RunnerManagerLauncher.exe by that name.
$testBuild = @{
    Name    = 'LauncherTestBuild'
    Exe     = Join-Path $bin 'LauncherTestBuild.exe'
    Inputs  = $launcher.Inputs
}
$targets = @($launcher)
if ($TestStub) { $targets += $stub, $testBuild }

function Test-UpToDate($target) {
    if ($Force -or -not (Test-Path -LiteralPath $target.Exe)) { return $false }
    $built = (Get-Item -LiteralPath $target.Exe).LastWriteTimeUtc
    foreach ($file in @($target.Inputs) + $PSCommandPath) {
        if ((Get-Item -LiteralPath $file).LastWriteTimeUtc -gt $built) { return $false }
    }
    return $true
}

$stale = @($targets | Where-Object { -not (Test-UpToDate $_) })
if ($stale.Count -eq 0) {
    Write-Host 'RunnerManagerLauncher: up to date.'
    exit 0
}

# ---- The C++ tools.

function Find-VcVars {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { return $null }
    $installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $installation) { return $null }
    $vcvars = Join-Path (@($installation)[0]) 'VC\Auxiliary\Build\vcvars64.bat'
    if (Test-Path -LiteralPath $vcvars) { return $vcvars }
    return $null
}

# Runs a program by its full path with the given environment, and returns its exit code and output.
function Invoke-Tool([string]$file, [string]$arguments, $environment, [string]$folder = $here) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.WorkingDirectory = $folder
    $start.FileName = $file
    $start.Arguments = $arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($environment) {
        $start.EnvironmentVariables.Clear()
        foreach ($name in $environment.Keys) { $start.EnvironmentVariables[$name] = $environment[$name] }
    }
    $process = [System.Diagnostics.Process]::Start($start)
    $errors = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    return @{ ExitCode = $process.ExitCode; Output = ($output + $errors.Result).Trim() }
}

function Quote([string]$path) { '"' + $path + '"' }

$vcvars = Find-VcVars
if (-not $vcvars) {
    $message = 'RunnerManagerLauncher: no Visual C++ x64 build tools found (vswhere found no Visual Studio or Build Tools ' +
        'with the component Microsoft.VisualStudio.Component.VC.Tools.x86.x64). Install the "Desktop development with C++" ' +
        'workload or the Build Tools, then build again.'
    if ($IfToolsPresent) {
        # An old build must not be tested as if it were this one.
        foreach ($target in $stale) { Remove-Item -LiteralPath $target.Exe -Force -ErrorAction SilentlyContinue }
        Write-Warning "$message The launcher tests fail until then."
        exit 0
    }
    [Console]::Error.WriteLine($message)
    exit 1
}

$cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
$vcRun = Invoke-Tool $cmd ('/d /s /c "' + (Quote $vcvars) + ' >nul 2>&1 && set"') $null
if ($vcRun.ExitCode -ne 0) {
    [Console]::Error.WriteLine("RunnerManagerLauncher: $vcvars failed with exit code $($vcRun.ExitCode).")
    exit 1
}
$vc = @{}
foreach ($line in ($vcRun.Output -split "`r?`n")) {
    $equals = $line.IndexOf('=')
    if ($equals -gt 0) { $vc[$line.Substring(0, $equals)] = $line.Substring($equals + 1) }
}
# Options in these variables would go into every compile or link without showing here.
foreach ($name in 'CL', '_CL_', 'LINK', '_LINK_') { $vc.Remove($name) }
foreach ($name in 'VCToolsInstallDir', 'WindowsSdkVerBinPath', 'INCLUDE', 'LIB') {
    if (-not $vc.ContainsKey($name)) {
        [Console]::Error.WriteLine("RunnerManagerLauncher: $vcvars did not set $name; the C++ tools or the Windows SDK are incomplete.")
        exit 1
    }
}
$cl = Join-Path $vc['VCToolsInstallDir'] 'bin\Hostx64\x64\cl.exe'
$link = Join-Path $vc['VCToolsInstallDir'] 'bin\Hostx64\x64\link.exe'
$rc = Join-Path $vc['WindowsSdkVerBinPath'] 'x64\rc.exe'

function Invoke-Step([string]$what, [string]$file, [string]$arguments) {
    $run = Invoke-Tool $file $arguments $vc
    if ($run.ExitCode -ne 0) {
        [Console]::Error.WriteLine("RunnerManagerLauncher: $what failed (exit code $($run.ExitCode)):")
        [Console]::Error.WriteLine($run.Output)
        exit 1
    }
}

# ---- Compile and link.

# C17, no C runtime (/Zl: no default library in the objects; /NODEFAULTLIB when linking), all warnings as errors.
# /Brepro (compiler and linker): no time stamps, so the same sources and tools give the same bytes.
$compile = '/nologo /c /std:c17 /W4 /WX /O1 /Oi /Gy /Gw /Zl /Brepro /utf-8 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /DWINVER=0x0A00'
$linkCommon = '/nologo /NODEFAULTLIB /SUBSYSTEM:WINDOWS /MACHINE:X64 /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA ' +
    '/LARGEADDRESSAWARE /Brepro /MANIFEST:NO /OPT:REF /OPT:ICF /INCREMENTAL:NO /RELEASE'

New-Item -ItemType Directory -Force -Path $bin, $obj | Out-Null

# The launcher's sources into $target.Exe, with the objects in $objFolder. $defines is empty for RunnerManagerLauncher.exe.
function Build-Launcher($target, [string]$objFolder, [string]$defines) {
    New-Item -ItemType Directory -Force -Path $objFolder | Out-Null
    $objects = @()
    foreach ($source in 'launcher.c', 'loadcfg.c') {
        $object = Join-Path $objFolder ([System.IO.Path]::ChangeExtension($source, '.obj'))
        Invoke-Step "compiling $source" $cl "$compile$defines /GS /guard:cf /Fo$(Quote $object) $(Quote (Join-Path $here $source))"
        $objects += $object
    }
    $res = Join-Path $objFolder 'launcher.res'
    # Every tool runs in this folder (Invoke-Tool), so launcher.rc finds launcher.manifest.
    Invoke-Step 'compiling launcher.rc' $rc "/nologo /fo $(Quote $res) launcher.rc"
    # /DEPENDENTLOADFLAG:0x800 (LOAD_LIBRARY_SEARCH_SYSTEM32): the imported DLLs come from System32 only.
    # /GUARD:CF and /CETCOMPAT: Control Flow Guard and shadow stacks. loadcfg.c holds the load configuration.
    $linkArgs = "$linkCommon /ENTRY:LauncherStart /GUARD:CF /CETCOMPAT /DEPENDENTLOADFLAG:0x800 /OUT:$(Quote $target.Exe) " +
        (($objects + $res | ForEach-Object { Quote $_ }) -join ' ') + ' kernel32.lib advapi32.lib userenv.lib shell32.lib ole32.lib'
    Invoke-Step "linking $($target.Name).exe" $link $linkArgs
}

if ($stale -contains $launcher) {
    Build-Launcher $launcher $obj ''
    $hash = (Get-FileHash -LiteralPath $launcher.Exe -Algorithm SHA256).Hash
    Write-Host "RunnerManagerLauncher: built $($launcher.Exe) ($((Get-Item -LiteralPath $launcher.Exe).Length) bytes, SHA-256 $hash)."
}

if ($stale -contains $stub) {
    $object = Join-Path $obj 'stub.obj'
    Invoke-Step 'compiling stub.c' $cl "$compile /GS- /Fo$(Quote $object) $(Quote $stub.Inputs[0])"
    Invoke-Step 'linking LauncherTestStub.exe' $link "$linkCommon /ENTRY:StubStart /OUT:$(Quote $stub.Exe) $(Quote $object) kernel32.lib"
    Write-Host "RunnerManagerLauncher: built $($stub.Exe)."
}

if ($stale -contains $testBuild) {
    # Objects of its own, so they are never linked into RunnerManagerLauncher.exe.
    Build-Launcher $testBuild (Join-Path $obj 'test') ' /DLAUNCHER_TEST_BUILD'
    Write-Host "RunnerManagerLauncher: built $($testBuild.Exe)."
}
exit 0
