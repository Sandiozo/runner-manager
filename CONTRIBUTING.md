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

## Project layout

| Path | Contents |
| --- | --- |
| `src/RunnerManager/MainWindow*` | Dashboard |
| `src/RunnerManager/RunnerService.cs` | Finding, installing and controlling runners |
| `src/RunnerManager/MachineRelay.cs`, `FleetNetwork.cs`, `FleetCrypto.cs`, `Pairing.cs` | Fleet agent, encrypted connection and pairing |
| `src/RunnerManager/AppUpdater.cs`, `Installer.cs`, `SetupWindow*` | Updates, setup and uninstall |
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
