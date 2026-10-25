# Changelog

Runner Manager updates itself from [Releases](https://github.com/Sandiozo/runner-manager/releases): open
**Settings → Updates**, or click **Update** on a PC's card.

## 2.3.1

- **AI assistants work when the AI tool runs them in a sandbox**, such as the Code tab of the Claude desktop app.
  `RunnerManager.exe --mcp` and `ai` now forward every request to this PC's fleet agent, which uses Runner Manager's
  real settings. Before, a sandbox could show them an old copy of the settings: access looked turned off, this PC's
  agent looked stopped, and other PCs reported a pairing code mismatch. The setup is unchanged.
- Opening Runner Manager while it runs in the notification area brings that window to the front again, instead of
  starting a second dashboard.
- The dashboard starts this PC's fleet agent again when it was ended.
- **Keep running when closed** is on for new installs: closing the window hides it to the notification area, and
  monitoring continues. Exit from the tray icon. Existing installs keep their setting.

## 2.3.0

- **AI assistants.** Runner Manager is now an MCP server, so Claude, Codex, VS Code with Copilot, Cursor, Windsurf,
  Gemini CLI, LM Studio and other MCP clients can list PCs and runners, read job status and logs, and create, start,
  stop, move and remove runners. Turn it on in **Settings → AI assistants**, which also has the setup for each tool.
- Changes ask for approval in a Runner Manager window. Moving and deleting runners always ask.
- Optional HTTP endpoint for AI tools that connect by URL, on this PC only and with a token.
- `RunnerManager.exe ai <tool>` runs the same tools from the command line and prints JSON.

## 2.2.5

- New download, **RunnerManagerSetup-standalone.exe**, that includes .NET, for PCs without the .NET 10 Desktop
  Runtime.
- Release notes list a checksum for each download.

## 2.2.4

- Moving, repairing or converting a runner of a renamed repository works. Runner Manager now registers runners with
  the repository's current name, because the runner's own `config.cmd` does not follow GitHub's redirect.

## 2.2.3

- **Install update** installs the update right away instead of opening Settings.

## 2.2.2

- Remote Desktop Commander starts in the data folder, so it no longer keeps an old install folder in use.
- Removing an old install folder retries briefly when a file is still in use.

## 2.2.1

- **Choose the install folder** in setup. A folder you pick gets its own `RunnerManager` subfolder. Running setup
  again with another folder moves the install.
- Uninstall removes only Runner Manager's own files outside the default folder.

## 2.2.0

**New**
- **Live job view**: a busy runner shows its job and every step with its state and duration, the latest output of the
  running step, and the processes of the job with their CPU and memory, on every PC.
- **Runner folder setting**: choose where new and moved runners are installed on each PC.
- **No UAC prompt**: Runner Manager opens from the Start menu with administrator rights through a scheduled task.
- **Redesigned Settings** with sidebar pages and compact rows.
- New app icon and logo.

**Changed**
- Runs on .NET 10. Updates install the .NET Desktop Runtime automatically when a release needs a newer one.
- Renamed from RunnerWidget to Runner Manager: exe, data folder, scheduled tasks and repository. Run the setup once on
  each PC that has version 2.1 or earlier; it moves the settings and tasks over.
- "Restart pending" appears only when Windows Update or a Windows component needs a restart.

**Security**
- The pairing code is no longer also saved unencrypted in `fleet.json`. If you used an earlier version, replace the
  pairing code (Settings → Fleet → ⋯ → Replace pairing code…) and pair your PCs again.
- Runner names, labels and URLs are checked before they reach `config.cmd`.
- Requests larger than 1 MB are refused before they are authenticated.

**Fixed**
- Settings could be overwritten with defaults when the dashboard and the fleet agent saved at the same time.
- Repairing a runner could remove its custom labels when GitHub did not answer.
- An update interrupted at the wrong moment could leave no working exe.
- With one PC, the sidebar could fail to refresh or show a PC twice.
- Workflow files with Windows line endings were not updated by "Check workflows".

## 2.1.0

- Setup wizard (`RunnerManagerSetup.exe`) for one PC or several, with optional Remote Desktop Commander.
- Direct, encrypted PC-to-PC fleet with 6-digit pairing, instead of a relay through GitHub.
- Add repository or organization runners, edit, move them between PCs, run console runners as services, and clean
  work folders.
- Auto-recovery, recent workflow runs, failure notifications and PC health.
