# Changelog

Runner Manager updates itself from [Releases](https://github.com/Sandiozo/runner-manager/releases): open
**Settings → Updates**, or click **Update** on a PC's card.

## 2.4.0

This release fixes the problems found in an audit of the whole app. Pairing a new PC needs Runner Manager 2.4 on both
PCs, so update the PCs in your fleet first.

### Runners

- Deleting a runner with its files deletes only that runner's own files. Before, a runner installed in a folder that
  held other runners or files deleted all of them. Runners installed in a drive root can't be deleted with their files,
  and junctions and links are removed without touching what they point to.
- Renaming, repairing or converting a runner checks first that the name is free. If registering fails, the runner is
  registered again with its old name and labels instead of disappearing.
- The runner group, ephemeral, no-auto-update and no-default-labels settings are kept when a runner is edited,
  repaired, converted or moved.
- Moving a runner keeps its labels (it stops when they can't be read), never leaves both copies broken, keeps the old
  copy from starting again at the next restart, and doesn't stop a runner that just started a job.
- Deleting a leftover copy of a moved runner no longer unregisters the moved runner.
- A failed delete puts the runner's service and startup entries back. A failed install removes the registration and
  service it left behind.
- Converting a console runner to a service also turns off scheduled tasks and Run registry entries that start it.
  Startup scripts that also start other runners are left alone.
- Cleaning a work folder is no longer interrupted by auto-recovery. Restarting a runner that runs a job asks first.
- Console runners that update themselves show as restarting instead of offering Repair.
- Network folders and mapped drives are refused as runner folders. ARM64 PCs get the ARM64 runner and label.
- Organization runners: a clear message when your GitHub sign-in lacks the `admin:org` permission. `GH_HOST`,
  `GH_TOKEN` and `GITHUB_TOKEN` no longer override the account connected in Runner Manager.
- Fewer GitHub API requests on each refresh.

### Fleet

- Pairing is protected against a device in between that tries to make both screens show the same code.
- New: **Settings → Fleet → Change code…** gives the PCs you keep a new pairing code, for example after a laptop was
  lost. PCs you leave out can no longer reach the fleet.
- A renamed PC is recognized. Two PCs with the same name get a warning instead of receiving each other's commands.
- Waiting for a command on another PC no longer times out when several commands run at once or one waits behind a
  long install. A request whose answer was lost is not run twice.
- Updating or restarting a fleet agent waits until it has finished its work.
- The fleet agent retries when its network port is taken, and starts again within 5 minutes if it stops while you are
  signed in.
- A PC's address only changes after it answers there, so a stray network answer can't send commands elsewhere.
- Clock differences between PCs are reported as such. A damaged fleet.json keeps the pairing code and every setting
  that can still be read. Settings saved at the same moment by the dashboard and the agent are no longer lost.
- Remote Desktop Commander runs the version chosen at setup, uses Node.js from Program Files only, and stopping it no
  longer ends other programs.

### Updates

- Updates no longer need a GitHub sign-in, and the update banner shows on every PC.
- A failed update check says why instead of "up to date".
- If an update's fleet agent keeps failing to start, Runner Manager goes back to the previous version by itself.
- Runner Manager never replaces a newer installed version with an older one. Beta versions are offered the final
  release.

### AI assistants

- AI tools see progress while a runner is created or moved, so long calls no longer time out and get repeated. Asking
  again for the same runner reports that it is already being created.
- Approval windows appear one at a time in order, stay in front without taking your keyboard, have no default button,
  and count as declined after 10 minutes. Cancelling in the AI tool closes them.
- After you approve, the runner is checked again, and nothing changes if it started a job meanwhile.
- Runners with the same name in different repositories are no longer mixed up; approvals show the repository.
- `find_runner_for_repo` and `list_runners` show each runner's real labels and match renamed repositories.
- Malformed requests get a clear error instead of making the tools look unavailable.
- `RunnerManager.exe --mcp` checks that it is talking to your own fleet agent. Text from repositories (run titles,
  logs) is marked as data for the assistant.

### App

- On a PC with several Windows users, one user runs the fleet agent, and the others can no longer take it over. A
  standard user who approves with an administrator's password gets an explanation instead of an install in the other
  account.
- Exiting during an operation asks first. Uninstalling from Settings asks before closing Runner Manager, and
  **Remove data** keeps your turned-off startup scripts in Documents.
- Settings are saved safely; a damaged settings file keeps the values that can be read.
- The System theme follows Windows while Runner Manager runs, text is sharp on every monitor, and a window on a
  disconnected monitor comes back on screen.
- Less memory and CPU use while running in the notification area.

## 2.3.2

- Updating Runner Manager also replaces a fleet agent that was started by hand. Before, such an agent kept running the
  old version and the new one did not start.
- `RunnerManager.exe --mcp` answers every request it received, even when the AI tool closes the connection early.

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
