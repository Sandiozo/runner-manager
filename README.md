# Runner Manager

Small Windows control panel for local GitHub Actions self-hosted runners and Remote Desktop Commander.

## Features

- Discovers installed `actions.runner.*` Windows services automatically.
- Also discovers configured runners under `C:\GitHub-Runners`, even when their service is missing.
- Shows Running/Stopped, Busy/Idle, recent `_diag` activity, and GitHub online/offline/busy state.
- Start, stop, restart, repair, or remove runners.
- Add a runner from a GitHub repository URL; the app downloads the latest official Windows x64 runner and installs it as a service.
- Preserves custom runner labels when repairing a runner.
- Uses GitHub CLI authentication and temporary GitHub registration/removal tokens; the app does not store GitHub credentials.
- Open runner diagnostic logs.
- Detect, start, stop, and restart Remote Desktop Commander.
- Shows multiple managed PCs in a Machines view with per-PC RDC and runner status/control.
- Machine runner rows expand independently to show repository, activity, and Windows service details.
- Activity feed and automatic refresh every 5 seconds.

## GitHub connection

GitHub CLI is required for Add/Repair/Remove and GitHub-side status. Use **Connect GitHub** in the app and complete GitHub's normal browser authentication flow.

## Build

Requires the .NET 8 SDK on Windows:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build -c Release
```

The app requests administrator privileges because Windows service installation and control require elevation.

- Shows current runner work, eligible queue state, and can repair workflow runner selectors.
