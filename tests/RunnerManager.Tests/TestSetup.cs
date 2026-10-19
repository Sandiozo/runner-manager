using System.IO;
using System.Runtime.CompilerServices;

namespace RunnerManager.Tests;

internal static class TestSetup
{
    /// <summary>Keeps tests away from the real settings in %LOCALAPPDATA%\RunnerManager.</summary>
    [ModuleInitializer]
    internal static void UseTemporaryStateDir()
        => Environment.SetEnvironmentVariable("RUNNER_MANAGER_STATE_DIR",
            Path.Combine(Path.GetTempPath(), $"runner-manager-tests-{Environment.ProcessId}"));
}
