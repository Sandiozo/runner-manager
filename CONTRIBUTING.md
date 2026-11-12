# Contributing

Thank you for helping. Bug reports, ideas and pull requests are all welcome.

## Questions, bugs and ideas

Questions and general ideas go to [Discussions](https://github.com/Sandiozo/runner-manager/discussions).

For bugs and feature requests, use the [issue forms](https://github.com/Sandiozo/runner-manager/issues/new/choose). For a bug, include the Runner
Manager version (Settings → Updates), the Windows version, and the steps to reproduce it. The fleet agent log helps a
lot: open it from the PC's ⋯ menu → **View fleet agent log**, and remove anything private before you paste it.

Security problems go through the [security policy](SECURITY.md), not public issues.

## Building

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build -c Release
dotnet test
```

To try a build without replacing your installed copy, run `src/RunnerManager/bin/Release/net10.0-windows/RunnerManager.exe`.
It asks for administrator rights, because it controls Windows services.

Setup installs to `C:\Program Files\RunnerManager` on a PC without an install. The install folder of each Windows user is
recorded in `HKLM\SOFTWARE\RunnerManager\Installs` (one value per user SID), where only administrators can change it.
The parts that run as administrator take the folder from there, or from the folder the running installed copy is in,
never from the Settings → Apps entry alone (see `MachineInstall.cs`). An install without a record is recorded once,
from the user's own scheduled tasks that run with the highest rights. A build run from `bin` uses that recorded folder
for updates and for the fleet agent's task. Tests never read or write the real records: `TestSetup.cs` replaces the
record store, and the reader of scheduled tasks that the install code uses. A build with administrator rights ignores
`RUNNER_MANAGER_STATE_DIR`; tests set their data folder in code (`AppPaths.TestStateDir`).

### The start helper

`src/RunnerManagerLauncher/` holds the start helper, `RunnerManagerLauncher.exe`: a small C program that starts the
`RunnerManager.exe` beside it with a clean environment. The app embeds it and writes it into the install folder
(`Launcher.cs`), and its scheduled tasks and its own starts with administrator rights run it.

- `build.ps1` builds it with the Visual C++ x64 build tools (Visual Studio or Build Tools with **Desktop development
  with C++**), which it finds through vswhere. Nothing is downloaded.
- `dotnet build` runs `build.ps1 -IfToolsPresent` first and embeds `bin\RunnerManagerLauncher.exe` when it is there.
  Without the C++ tools, the build warns and goes on: that build has no start helper, its scheduled tasks start
  RunnerManager.exe directly, and the launcher tests fail and say why. The other tests run as usual.
- The release workflow builds the start helper without `-IfToolsPresent` and publishes with `-p:RequireLauncher=true`,
  which fails when the start helper is missing, so a release never goes out without it. It then checks that both exes
  probe through the start helper exactly as they do on their own.
- `RunnerManager.exe` looks for the .NET runtime only where it is installed for the whole PC
  (`AppHostDotNetSearch=Global`), not in a folder that `DOTNET_ROOT` names. Install the .NET 10 SDK for the PC.

## Project layout

| Path | Contents |
| --- | --- |
| `src/RunnerManager/MainWindow*` | Dashboard |
| `src/RunnerManager/RunnerService.cs` | Finding, installing and controlling runners |
| `src/RunnerManager/MachineRelay.cs`, `FleetNetwork.cs`, `FleetCrypto.cs`, `Pairing.cs` | Fleet agent, encrypted connection and pairing |
| `src/RunnerManager/AppUpdater.cs`, `Installer.cs`, `SetupWindow*` | Updates, setup and uninstall |
| `src/RunnerManager/MachineInstall.cs` | The install folder: the default, the record of each Windows user, shared installs |
| `src/RunnerManager/Launcher.cs`, `src/RunnerManagerLauncher/` | The start helper: the file in the install folder, and its source |
| `src/RunnerManager/RunnerActivity.cs` | Live job output and processes |
| `tests/RunnerManager.Tests/` | xUnit tests |

## Pull requests

- Keep each pull request to one change, and describe what it does and why.
- Add or update tests for behavior changes. `dotnet test` must pass; CI runs it on every pull request.
- Match the style of the surrounding code.
- User-facing text is neutral, professional and simple: short sentences, no contractions, and the same terms as the
  rest of the app ("PC", "fleet", "fleet agent").
- Keep compatibility with older fleet agents: other PCs may run an earlier version. Protocol changes need a new
  `MachineRelay.ProtocolVersion` and a fallback for older agents.

By contributing, you agree that your work is licensed under the [MIT License](LICENSE).
