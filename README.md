# Runner Widget

Small Windows control panel for local GitHub Actions self-hosted runners and Remote Desktop Commander.

## Current features

- Automatically discovers local `actions.runner.*` Windows services.
- Shows runner Running/Stopped and Busy/Idle state.
- Shows recent runner diagnostic activity from each runner's `_diag` folder.
- Start, stop, or restart individual runners.
- Open a runner's diagnostic log folder.
- Detect Remote Desktop Commander processes.
- Start, stop, or restart Remote Desktop Commander.
- Records status changes and user actions in a local activity feed.
- Refreshes automatically every 3 seconds.

## Build

Requires the .NET 8 SDK on Windows.

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build -c Release
```

The app requests administrator privileges because Windows service control requires elevation.
