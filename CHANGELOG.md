# Changelog

Runner Manager updates itself from [Releases](https://github.com/Sandiozo/runner-manager/releases): open
**Settings → Updates**, or click **Update** on a PC's card.

## 2.10.0

New installs go to Program Files. There, Runner Manager's program files cannot be changed without administrator rights.
A runner that runs twice on a PC, as its Windows service and in a console window, is now shown as **Runs twice**, and
Runner Manager no longer starts a runner's service next to its console window.

### After updating

- An install in `%LOCALAPPDATA%\Programs\RunnerManager` or in a folder chosen in setup stays where it is, and updates
  keep it there. To move it to Program Files, run the setup again, click **Change…** and choose `C:\Program Files`.
- On its first start with administrator rights, also in setup, Runner Manager records the folder that its own
  scheduled tasks start it from, and the fleet agent's log says so. When it finds no such task, or cannot read the
  tasks, it records nothing, and the log says that too, once for each version (see **Security**).
- The setup of AI tools does not change. Copy it again from **Settings → AI assistants** only after a move to another
  folder.
- A runner that has a Windows service and also opens in a console window when you sign in is no longer started as a
  service from Runner Manager, also not by auto-recovery. To use its service, turn off **Start when you sign in** on
  the runner first (see **Runners and jobs**).
- A new file, `RunnerManagerLauncher.exe`, is in the install folder: the start helper (see **Security**). While Runner
  Manager runs, it also shows in Task Manager, next to each RunnerManager.exe that it started. A self-contained
  Runner Manager unpacks its files into a `.net` folder there. The UAC prompt for Runner Manager now names
  `RunnerManagerLauncher.exe`.
- The scheduled tasks move to the start helper by themselves after the update, once the installed Runner Manager with
  administrator rights has seen the start helper start it: the launch task and the startup task when the dashboard or
  the fleet agent runs and the task does not run the dashboard at that moment, and the fleet agent's task only once the
  update has proven itself. The fleet agent then restarts once, when it has no work. A dashboard that its launch task
  or startup task started as RunnerManager.exe itself starts again through the start helper, once, before it opens its
  window, so that its task can move too.
- Links and the GitHub sign-in page open in your browser without administrator rights: Runner Manager gives them to
  Explorer, which opens them for the signed-in Windows user. Runner Manager opens only https addresses without a comma.
- GitHub CLI that is installed for one Windows user only, in a folder that only that user's PATH names, is no longer
  found by Runner Manager, because it starts with the PC's PATH. Install GitHub CLI for all users instead: the
  installer from https://cli.github.com/ does that.

### Security

- The folder Runner Manager is installed in is now recorded for each Windows user where only an administrator can
  change it. The parts that run with administrator rights, such as updates and the fleet agent's task, take the folder
  from there or from the folder the installed copy runs in. The Settings → Apps entry, which another program of the
  user can change, only confirms that the running copy is installed, keeps setup from removing a copy in the folder
  that it names, and makes setup show that folder as one it could not confirm (see **Setup and updates**). An install
  of an earlier version is recorded once, from the scheduled tasks of the Windows user that run with administrator
  rights. A program without administrator rights cannot register such a task.
- Runner Manager starts, repairs, moves over from an earlier version or deletes a scheduled task under one of its names
  only when it is the Windows user's own task that runs with administrator rights. Any program of the user can create a
  task under such a name that runs without administrator rights. Such a task no longer makes Runner Manager register
  and start the fleet agent or register the startup task: Runner Manager leaves it as it is, and the fleet agent's log
  says so. Only the launch task, which opens the dashboard without a UAC prompt, is registered again when it is not the
  user's own task with administrator rights.
- Runner Manager goes back to the previous version after an update only when a file beside RunnerManager.exe,
  `RunnerManager.exe.pending`, names that update. The starts of the new fleet agent are counted in that file, and only
  those counts decide. In Program Files, only administrators can change that file. Before, the update state in the
  user's data folder was enough, and any program of the user can change it, so such a program could make Runner
  Manager go back to the previous version. The file is deleted when the update is no longer watched. When an update
  was undone, the file records that, and only this record, not the update state, keeps Runner Manager from updating
  the fleet agent to that version again by itself. A program of the user can still end the watch of an update through
  the update state, so that an update whose fleet agent does not work is not undone. Versions before 2.10.0 write no
  such file, so for 24 hours after an update from one of them, or after a setup that replaced one of them, the update
  state alone is still enough.
- Updates are prepared in a `RunnerManager.update-<number>` folder in the install folder instead of in the user's data
  folder. In Program Files, the new files cannot be changed between the checksum check and the copy without
  administrator rights. Old update folders in the install folder are removed only when they hold nothing but files and
  are not a link themselves. The `update-<number>` folders that earlier versions left in the data folder are removed
  only by a start without administrator rights, so a link that another program puts there reaches nothing that the
  user could not delete anyway. A missing or half-written RunnerManager.exe is still put back, and rollback still works.
- The signature check of the .NET runtime download, the scripts that setup and uninstall run, and the check whether a
  scheduled task runs take folder, file and task names from the environment instead of from the script text, so a name
  with quotes cannot run commands. PowerShell is always started from the Windows folder.
- The other Windows tools that Runner Manager runs, such as sc.exe, schtasks.exe, netsh.exe, shutdown.exe, cmd.exe
  (also for config.cmd) and explorer.exe, are now started by their full path in the Windows folder too. By their bare
  name, Windows looks for them in Runner Manager's folder and the current folder first. When GitHub CLI is not in
  `C:\Program Files\GitHub CLI`, Runner Manager looks for gh.exe in the PATH folders itself, leaving out its own
  folder, the current folder and folders without a full path, and starts it by the full path it found.
- The release build no longer loads the assemblies that `DOTNET_STARTUP_HOOKS` names, which any program of the user
  can set.
- Uninstall and a move to another folder remove the whole folder only in Program Files. From any other folder, also
  the default of earlier versions, they remove only Runner Manager's own files and its own update folders that hold
  nothing but files.
- **Uninstall and delete settings** no longer goes into a link (a junction or symbolic link) that it finds in the data
  folder: it removes the link itself. A data folder that is a link itself is not emptied. The startup scripts that
  Runner Manager had turned off are no longer moved to Documents. They stay in the `disabled-startup` folder of the
  data folder, and the result of the uninstall says where they are.
- The console window in which Runner Manager runs gh for the GitHub sign-in no longer runs the AutoRun commands of
  cmd.exe, which any program of the user can set in the registry.
- Runner Manager now starts through a start helper, `RunnerManagerLauncher.exe`, beside RunnerManager.exe. Its
  scheduled tasks (the launch task, the startup task and the fleet agent's task) run the start helper, and so do the
  UAC prompt, the start after setup, **Restart now** after an update and **Settings → Updates → Uninstall…**. The
  uninstall entry in Windows **Settings → Apps** still starts RunnerManager.exe itself. The start helper ignores the
  environment it is started with and starts RunnerManager.exe with a new one: the PC's variables, the user's name and
  folders as Windows gives them, and only the proxy and GitHub token variables (`HTTP_PROXY`, `HTTPS_PROXY`,
  `ALL_PROXY`, `NO_PROXY`, `GH_TOKEN`, `GITHUB_TOKEN`) from the environment it was given. Variables that any program of
  the user can set for the user, such as `DOTNET_*`, `CORECLR_*` and `PATH`, no longer reach Runner Manager with
  administrator rights through its tasks and its own starts. This protects an install that programs without
  administrator rights cannot change, such as one in Program Files. Setup writes the start helper and checks that it
  starts Runner Manager before it registers the tasks. Only the installed Runner Manager with administrator rights
  looks after it: it puts it back when it is missing and checks it in the same way before a task or a start depends on
  it. An earlier copy that still runs after an update leaves it alone. When antivirus software removes or blocks it, it
  cannot be written, or it does not start Runner Manager, Runner Manager runs without it and its tasks start
  RunnerManager.exe as before, until the next update or setup. The fleet agent's log says so, and so do Activity on
  every start and **Settings → Updates**. When the UAC prompt cannot start the start helper, or the launch task started
  it but no dashboard appeared, Runner Manager asks with UAC for RunnerManager.exe itself.
- The framework-dependent RunnerManager.exe looks for the .NET runtime only where it is installed for the whole PC, no
  longer in a folder that `DOTNET_ROOT` names.
- Runner Manager with administrator rights no longer takes its data folder from `RUNNER_MANAGER_STATE_DIR`, which any
  program of the user can set.
- Still open: an install in the user's profile, such as `%LOCALAPPDATA%\Programs\RunnerManager`, can be changed by any
  program of the user, so the start helper does not protect it until it is moved to Program Files. Runner Manager says
  so once for each version in Activity, and in **Settings → Updates**. The settings files in the user's data folder can
  also be changed by any program of the user. Setup itself always runs with the environment it was started with. A PC
  that runs without the start helper starts Runner Manager as before. After the update from 2.9.0, the first start of
  the dashboard, and the fleet agent until the update has proven itself, still start as before. A dashboard that a task
  started as RunnerManager.exe itself runs with the environment it was given until it has started again through the
  start helper, before it opens its window. When antivirus software removes or blocks the start helper while Runner
  Manager is closed, the fleet agent does not start until someone opens Runner Manager and accepts the UAC prompt; then
  Runner Manager puts the start helper back or runs without it. To avoid that, exclude `RunnerManagerLauncher.exe` in
  the install folder from the antivirus software's checks.

### Fleet and pairing

- Fix: **Add a PC** said nothing when a PC joined the fleet but this PC could not save it. It now says so and how to
  add the PC again, and a PC that confirms the 6-digit codes is told that it was not added.
- **Add a PC** warns about a name whose letters only look like the letters of another PC's name or of this PC's name,
  such as a Cyrillic А in place of a Latin A, or full-width letters, also when the name is in small letters. It also warns
  about a name that mixes Latin, Greek or Cyrillic letters with each other, or with letters of another alphabet that
  has letters like Latin ones, such as Armenian or Cherokee. A name without such a mix is not warned about, unless it
  looks like the name of another PC.
- Fix: while this PC joined or left the fleet, **Change fleet code** and the address check in Settings still read the
  other PCs, and could add back a PC that leaving had just removed. **Change fleet code** now says to try again when it
  is done, and the address check in Settings does not read the other PCs then, so it shows what the last refresh
  found. A PC that is not in a fleet does not save other PCs.

### Runners and jobs

- Fix: a runner could run twice on a PC, as its Windows service and in a console window that a startup entry opens when
  you sign in, and look healthy. Only one copy gets jobs, and in the service they failed because git refused the work
  folder of the console window's user. Such a runner now shows **Runs twice**, and its details say why jobs fail or wait
  and what to do. It counts under **Needs attention**, gives one alert when it begins (with **Stuck and waiting jobs**
  turned on), and the other PCs and AI assistants see it too.
- A runner that two console windows run, for example one in each of two Windows sessions on a PC, now shows **Two
  console windows**. Both use one registration, so only one of them gets jobs. Its details say to close one of the
  console windows or to sign out of the other Windows session. It counts under **Needs attention**, gives one alert
  (with **Stuck and waiting jobs** turned on), and the other PCs and AI assistants see it too. Only console windows that
  have both run for 2 minutes count, so a runner that restarts is not reported. It stays shown for up to 5 minutes
  after one of the console windows was closed. Start, Stop, Restart and auto-recovery work as before.
- Start and Restart of a runner's service are refused while a console window runs the runner, or while a startup entry
  that is turned on opens it in one when you sign in, also when no console window runs now. This holds for the
  dashboard, commands from other PCs, AI assistants, auto-recovery, and the restart after an edit or **Clean work
  folder**. After an edit, a service that config.cmd started is stopped again for such a runner. **Stop** still works.
  The runner's **Start when you sign in** switch turns its startup entries off and on.
- A startup entry that only names files in a runner's work folder or log folder (`_work`, `_diag`), such as a
  scheduled task that deletes old logs, no longer counts as one that opens the runner. Such a task does not make a
  runner show **Runs twice**, and Runner Manager no longer turns it off with the runner's startup entries.
- The **Windows service** row in a runner's details is short now. Pointing at it shows the service's name and the
  account it runs as, with a note when a console window runs the runner. Screen readers read this too. When a
  runner's Windows service runs it and folders in its work folder belong to another account, which git refuses, the
  details say so, and a running runner counts under **Needs attention** as **Work folder owner**. A stuck job, a job
  that runs long, or jobs that wait for an idle runner are shown before it, with **Stop the job's processes**, and it
  gives one alert, not one each time such a job concern ends. A runner that a startup entry opens in a console window
  is not warned about: its folders belong to the signed-in user, as they should.
- **Run as Windows service**, **Repair Windows service** and an edit give the work folder to the service's account,
  NETWORK SERVICE, before config.cmd installs the service, when folders in it belong to another account and the service
  runs the runner from then on. Nothing is deleted, and links in it are not followed. An edit leaves the work folder as
  it is while a console window runs the runner, or a startup entry that is turned on opens it in one when you sign in,
  and the log says so.
- Fix: an edit of a runner whose Windows service is disabled, as for a runner that runs in a console window instead,
  made the service start with Windows, so that after the next restart of the PC the runner could run twice. The service
  is now disabled again after the edit, also when a failed edit registers the runner again with its old name and
  labels. While the console window runs such a runner, an edit is refused, and nothing is changed. The old copy of a
  moved runner is handled as before.
- Fix: when a move to another PC failed, the runner's Windows service on its PC was set to start with Windows and
  started, also when it was disabled because the runner runs in a console window, so that the runner could run twice.
  A runner that was stopped before the move was started too. The runner is now set up as it was before the move: a
  disabled service stays disabled and is not started, and a service that started manually or with Windows does so
  again and is started, unless a console window ran the runner or it was stopped in Runner Manager. A stopped runner
  stays stopped. A runner in a console window is no longer left looking like the old copy of a moved runner, and its
  console window, which the move closed, opens again at the next sign-in. Its disabled service also stays disabled
  after another failed move when an earlier move failed with 2.9.0. This needs 2.10.0 on the runner's PC.
- When a move fails and the runner's PC runs a version before 2.10.0, a runner that a console window ran or whose
  Windows service was disabled stays stopped there, and its service is not started. Runner Manager now says so, and to
  update Runner Manager on that PC and then start the runner there as before.
- A move to another PC still sets the runner up there as a Windows service, also when a console window ran it on the
  old PC or its Windows service was disabled there. The service starts with Windows, or manually when the runner was
  set not to start automatically. The Move window now says so. To keep such a runner in a console window, turn off its
  service on the new PC and add the startup entry there.
- Fix: when the processes on a PC could not be read, the live output of a step that printed nothing for 5 minutes was
  no longer shown. It is shown now when Runner Manager saw earlier, while it could read the processes, that it is the
  running job's output. Otherwise the output says that the processes could not be read.

### GitHub

- Fix: a GitHub answer that could not be read paused its request again when it arrived after **Refresh** or
  **Connect GitHub** had cleared the pauses.

### Setup and updates

- On a PC without Runner Manager, setup installs it to `C:\Program Files\RunnerManager`. On a PC with an install that
  setup can confirm, setup shows the folder it is in, and **Default** or **Current folder** goes back to that folder.
- When setup finds an install only from the Settings → Apps entry or in the default folder of earlier versions, which
  any program of the user can change, it starts with Program Files selected. **Current folder** selects the folder it
  found, and setup says that it could not confirm the install there. When another folder is selected, setup says that
  it leaves the folder it found as it is. Setup never offers a folder that it cannot install to, such as a network
  folder.
- For a folder outside Program Files, also one that setup offers, setup says to check that only administrators can
  change the files in it.
- A folder in Program Files or Program Files (x86), such as `C:\Program Files (x86)\RunnerManager`, gets its rights
  from there, so setup shows no such note for it.
- The first page says what setup does with a copy that is already in the chosen folder, also after **Change…**.
- Several Windows users on one PC can share one install outside the user profiles, such as in Program Files: when a
  second user runs the setup, it offers such an install folder that Runner Manager recorded for another user. An
  install in the default folder of earlier versions is in its user's profile, so setup offers Program Files to a
  second user who has no install. Setup never puts an older version in place of one that another user runs, as far as
  their records and the scheduled tasks that setup can read show, and keeps the installed version instead. Uninstall
  and a move to another folder keep the program files while another user still uses them, and also when setup cannot
  read the scheduled tasks that show who uses them. The record and the scheduled tasks of a Windows user who has no
  profile on this PC any more do not count, and a folder in Task Scheduler is not taken for a task. An account that
  was removed without its profile still counts until the profile is deleted.
- Setup and uninstall close only the dashboards in the Windows session that runs them.
- Fix: when a move to another folder failed, the startup task and the fleet agent's task could still start the new
  copy, which setup then removed. Setup now points them at the old copy again and restarts the fleet agent.
- Fix: an update could cut off work that started while it waited for the fleet agent's work to end, such as an AI
  assistant's change or a runner that another PC creates behind a long queue. When the fleet agent installs an update
  itself (**Update** on the card of another PC), AI assistants' changes on that PC are refused while it waits to
  restart, with a message that says to try again in a few minutes, and a request that arrives in the seconds while it
  restarts is refused with a message. With **Settings → Updates** or **Update** on this PC's card, the restart also
  waits for work that starts while it waits, but work that starts just before the fleet agent is stopped can still be
  cut off.
- Fix: an update switched the fleet agent's task to the new version before the fleet agent's work was done, which
  could stop that work. The task is now switched when the fleet agent restarts. When the switch fails, the PC's card
  shows the error as the result of the update.
- When the fleet agent cannot restart to finish an update, for example because PowerShell cannot be started, it keeps
  running the version it has, takes commands again, and the PC's card says why.
- Fix: a dashboard that stayed open after an update was installed, such as one that the fleet agent installed in the
  background, took its own version for the installed one. It did not offer **Restart now**, and it ended the watch of
  the new update, so Runner Manager did not go back to the previous version when the new fleet agent did not work. An
  open dashboard now reads the version from the installed file, offers **Restart now** and keeps watching the update.
  Still open: when a PC updates from 2.9.0 or older while its dashboard is open, that dashboard still ends the watch of
  this one update.

### Dashboard and windows

- Fix: a click on a PC card header, a runner header or a row could scroll the card list. The list now stays where it
  is while the mouse button is down or a pen or a touch is on the screen. When Tab or a screen reader moves the focus,
  the list still scrolls to it.
- The job, the repository and the current job in a runner's details are now buttons, so Tab and screen readers reach
  them. A job or a run without an address on GitHub, there and in the queue and the recent runs, is plain text and not a
  button, and so is **Repository unknown**.
- Fix: screen readers left an underscore out of a runner's or a PC's name on some buttons, so that **More actions** for
  `GPT_CP` was read as "More actions for GPTCP". This was so for the action buttons of PCs and runners, **Add runner**,
  **Forget** and **Remove** in Settings and the PC choices in **Add runner**. **Move to** in **Move runner** and the PCs
  in **Change fleet code** also showed the name without its underscore. Such names are now read and shown as they are.
- Screen readers now read the rows of a PC's runners, of the queue and the recent runs, of a job's steps and
  processes, and the PCs to join in **Set up a fleet** by what they show, such as a runner's name and state, instead of
  the name of a class.

## 2.9.0

A large release with many fixes from a review of 2.8.2, most of them for security and for the fleet.

### After updating

- Restart AI tools that were open during the update. Their old sign-in to the fleet agent is refused on purpose, and
  the tool's error says so.
- A **Stuck** or **Running long** alert that was open during the update may show once more.
- PCs whose names have letters outside A-Z, such as Ö or Ā, can reach each other when both run 2.9.0. An older fleet
  agent refuses such a name, and the message says to update it.
- **Allow AI assistants** and **Ask before changes** now count for every Windows user of the PC, and only an
  administrator can change them (see **Security**).

### Security

- The sign-in between an AI tool and the fleet agent on this PC no longer accepts programs of other Windows accounts.
  An AI tool also no longer takes an answer from another program that listens on the fleet agent's port while the
  agent is stopped, and shows no text from a program that has not signed in.
- The connection between an AI tool and the fleet agent on this PC uses no proxy, and a sign-in that is not used
  within 2 minutes is forgotten.
- **Allow AI assistants** and **Ask before changes** are now stored where only an administrator can change them, so
  another program of the user cannot turn AI assistants on or approvals off. They are moved over once, and each change
  is written to the fleet agent's log. Uninstall removes them when it also removes the settings.
- Joining a fleet: a device on the network can no longer answer as one of your PCs and take a joining PC into its own
  fleet. The joining PC now uses the code it was sent only after its user confirms that the 6-digit codes match (see
  **Fleet and pairing**).
- Pairing between two updated PCs also protects the host's name, the **Manage it one way** choice and the joining
  PC's name, so a device on the path cannot change them. A joining PC can also no longer prepare several keys for one
  request, which made the 6-digit code easier to match.
- A PC in the fleet, or a PC managed one way, can no longer make the dashboard send other requests to GitHub with
  your GitHub sign-in through the repository names it reports. Such names are checked before gh runs, and again when
  the PC's status arrives: a name that is not a GitHub name is left out, and at most 200 runners and 100 repositories
  are taken from one PC. Problems in another PC's status are logged once per PC and on one line, so a PC cannot fill
  the fleet agent's log or write lines of its own into it.
- A PC that joins another fleet forgets the PCs it manages one way, so the new fleet does not get their access codes.
  This also holds for a fleet code entered by hand in **Settings → Fleet** that differs from the PC's code: Settings
  asks first, and when the code is the new code of the same fleet, the managed PCs come back from the other PCs. A PC
  without a code that joins a fleet also forgets the PCs and addresses of the fleet before.
- On a PC that is managed one way, **Add a PC** no longer opens. It would have handed out that PC's access code as if
  it were a fleet code.
- A new fleet code from **Change the fleet code on every PC** that is not used within 10 minutes expires, so a former
  member cannot switch a PC to it later.
- A PC that becomes managed one way forgets its old fleet: its PCs, and the codes of the PCs that fleet manages.
- Process arguments that other PCs and AI assistants see hide more passwords and tokens: the value after -pw, -p and
  -P of programs that take a password there (mysql, docker, podman, helm, az, psexec, the PuTTY tools and more), also
  when the program is named with a path or started through cmd, --pass, --passphrase, user:password in URLs, tokens of
  GitHub, GitLab, Slack, npm, PyPI and others, Cookie and API key headers, and the passwords and keys given to
  redis-cli, the LDAP tools, ssh-keygen, Chocolatey, sshpass, smbclient and rabbitmqctl. A port, mkdir -p and Unity's
  -projectPath stay visible.
- Approvals and the action log show an AI tool's name on one line, with letters, digits and a few marks only, and
  without characters that are not shown, so a name cannot look like Runner Manager's own text. The action log keeps
  one line per entry, and assistant.log starts again at 1 MB.

### Fleet and pairing

- **Joining a fleet:** after the other PC allows, this PC asks **Do the codes match?**. Cancel, Back or closing the
  window discards the code. After **The codes match** the window shows **Finishing…** for at most 30 seconds and
  cannot be left meanwhile.
- **Add a PC** adds a PC only once its user has confirmed the codes there, and waits up to 3 minutes for it. Until then
  it shows who has not confirmed, with the 6-digit code, and asks before it closes. A PC that does not confirm, or that
  the host cannot save, is not added and does not keep the code. This works when both PCs run 2.9.0; older PCs pair as
  before.
- **Add a PC** asks for a choice on every request: no option is pre-selected, and Allow does not get the focus, so a
  stray Enter cannot hand over the fleet code. Esc denies the request. The window stays open while a request is on
  screen and says that it closes by itself after 10 minutes without one.
- **Add a PC** warns before Allow when a PC asks to join under the name of a PC that is already in the fleet, managed
  by it or allowed a moment ago, also when it says that it is that PC: a machine ID proves nothing. The warning says
  what allowing it replaces, and that a fleet member allowed one way stops being a fleet member. It also warns when the
  PC says it is a saved PC under a new name, and names every PC it would replace. Names that differ only in how a
  letter is written (Ā as one character or as A with a mark, full-width letters) count as one name. A PC with the name
  or the machine ID of this PC, or with characters in its name that cannot be seen, cannot be allowed. When what is
  known about the PC changes while the request is on screen, Allow first shows the new warning.
- **Add a PC** saves the PC's machine ID, so a renamed PC, or a PC that changes between fleet member and managed one
  way, is known as the same PC.
- The dashboard, the pairing windows, Settings and the fleet agent's messages call the fleet's long code the fleet
  code, and the code of a PC managed one way its access code. "Pairing code" is no longer used. In **Settings →
  Fleet**, the item that gives only this PC a new code is now **New fleet code for this PC only**, and **Change fleet
  code** is **Change the fleet code on every PC**. **Copy fleet code instead** says that the code gives full access.
  On a PC managed one way the row is **Access code**.
- The list of PCs to join keeps its rows between searches, keeps a PC through two missed answers, and lists what to
  check when no PC is found after about 10 seconds. Enter submits the address and the fleet code.
- Fix: a PC no longer shows offline because its clock is a little behind, or because its status takes long to build on
  a busy PC. A PC whose status can no longer be built no longer shows "Updated just now" with old runner states.
- Fix: requests to other PCs no longer go through a proxy set up in Windows, which made every PC "Not reachable", and
  a PC that is off is reported sooner: connecting to it takes at most 4 seconds.
- Fix: the fleet agent listens again when Windows stops its listener, keeps running commands when a read of this PC's
  runners hangs, no longer runs a command twice after a crash, and no longer drops lines from its log. Unexpected
  errors are written to the log, and a fleet agent that fails at start is started again.
- Fix: settings that can still be read from a damaged fleet.json are saved, so the PC no longer leaves the fleet
  without a word.
- Fix: pairing a PC one way no longer removes a different fleet PC with the same name. A PC that was a fleet member and
  is paired again one way, or the other way round, is no longer known both ways. A managed PC renamed to another
  managed PC's name is shown with a problem instead of taking that PC's place.
- Fix: while this PC leaves or joins the fleet, the dashboard does not read the fleet, so a PC that was just removed
  does not come back.
- **Settings → Fleet:** **Forget** asks first and also removes the address the PC was added by. **Add a PC by
  address** is now **Look for a paired PC at an address**: it says that it does not pair a PC, refuses IPv6 addresses,
  URLs, invalid ports and this PC's own address, accepts PC names in any alphabet, and shows which PC answers at each
  address.
- **Settings → Fleet** says when a fleet code is set but this PC has not joined yet, because its fleet agent is not
  installed. It offers **Finish joining** then, and **Leave fleet** is disabled.

### Runners and jobs

- **Add runner** can add the runner on another PC of the fleet: choose the PC in the window, or click **Add runner**
  on that PC's card. The other PC reports no progress until it is done, and this PC's view keeps updating meanwhile.
  The other PC registers the runner with its own GitHub sign-in, so this PC needs none for it; without one, the
  workflow check is skipped and the log says so. While this PC is chosen and is not connected to GitHub, the window
  says so. Adding or moving a runner to another PC waits as long as the download and registration may take.
- Fix: auto-recovery tries a runner again an hour after it gave up, and reports giving up only once, until the runner
  is seen running. It leaves a runner alone while it starts or stops, and restarts it only when it is stopped at two
  checks in a row, so a Start, Stop or Restart on this PC no longer counts as a stop. It no longer starts a stopped
  runner again after the runner was edited.
- A runner service that stays starting or stopping for more than 2 minutes now counts as stopped: auto-recovery tries
  it 3 times and then reports it once. Auto-recovery is disabled, with the reason, when the fleet agent is not
  installed.
- Fix: stuck detection no longer misses a step that has printed nothing for more than 30 minutes, no longer shows
  **Stuck** after the PC wakes from sleep, and one failed GitHub read no longer raises **Running long**. A runner is
  also no longer reported because of the job of another runner with the same ID, or of a job it gave up when it was
  restarted. A job that runs far too long is still reported as running long when GitHub cannot tell how long it
  usually takes.
- Fix: health checks use the runners' real labels from GitHub, so runners on ARM64 PCs and runners without default
  labels are matched, and jobs without self-hosted in runs-on are checked too. "No runner in the fleet has the labels"
  is no longer raised for Linux, macOS or GitHub-hosted jobs, or for jobs that a runner outside the fleet can take.
- Fix: **Stop the job's processes** no longer stops an older process, such as explorer.exe, whose parent had the
  same process ID as the job. Job checks read the process list once for all busy runners.
- The live job output of an idle runner no longer shows a page left over from an earlier job, and it shows the newest
  output when the list of processes on the PC cannot be read, if that output is at most 5 minutes old.
- A runner download that stops getting data now fails after 60 seconds, or after 45 minutes in all, with a clear
  message. Before, it could block the fleet agent's commands for good.
- Fix: the fleet agent refuses **Start**, **Stop** and **Restart** for a runner that runs in a console window, instead
  of starting it a second time or reporting a stop that did not happen.
- Fix: deleting a runner on this PC no longer stops after the runner was unregistered and before its files are
  deleted.
- Fix: editing a stopped runner leaves it stopped, and a renamed runner keeps its stopped mark. Editing a runner that
  stopped by itself does not mark it as stopped on purpose.
- **Add runner** and **Edit runner** check every field before they close. Each field shows its problem, **Create
  runner** stays disabled until the form can be used, and a new name longer than 64 characters is refused. The name
  and folder default to free ones, and owner/repo, github.com/owner/repo and git@github.com:owner/repo.git are
  accepted.
- Creating a runner checks that the name is free before the download. A failed workflow check after creating a runner
  is a warning: the runner was created.
- **Edit runner** fills in the labels of runners on other PCs and enables **Save changes** only when the name or labels
  changed. It can remove every custom label of a runner on this PC, and says that it cannot for a runner on another
  PC, instead of keeping the labels silently. A runner that was set up without default labels keeps at least one
  label, because it could not take jobs without any.
- Fix: **Start with Windows** in a runner's menu shows its check mark, and sets the delayed start that GitHub's setup
  gives runner services. A service turned on again after a failed move also starts delayed.
- When a move fails after the runner's registration was replaced, the error says that the old copy must be registered
  again.

### GitHub

- Fix: a second GitHub account with a problem no longer shows GitHub as not connected when the active account works.
- While GitHub is rate-limited, cannot be reached or sends an answer that cannot be read, Runner Manager waits before
  it asks again (until the rate limit resets, or 30 to 45 seconds) instead of starting gh every few seconds. Actions
  you start are never held back.
- When GitHub cannot be read, the dashboard says why and when it tries again: in the job panel, in an open runner and
  in the status bar. **Refresh** and a finished **Connect GitHub** ask GitHub again at once.
- Fix: a PC without a network connection no longer shows GitHub as not connected. It keeps the last sign-in and says
  "No answer from GitHub" until GitHub answers again.
- A repository with many queued runs shows the longest-waiting ones, and a run with more than 100 jobs is read further
  when the refresh has requests left.
- After **Connect GitHub** or **Reconnect**, the dashboard notices the finished sign-in within seconds and says
  "Waiting for sign-in" meanwhile.
- Other PCs are read from their fleet agents, so the dashboard no longer says that they need GitHub on this PC.

### AI assistants

- Turning on **Allow AI assistants** asks first when it would create a fleet code, install the fleet agent or open
  its firewall ports, and says that it starts a fleet with only this PC.
- list_runners, find_runner_for_repo and job_status say when a runner's PC is offline, with its last known state.
  find_runner_for_repo and create_runner say when every runner for a repository is on an offline PC.
- create_runner without a PC picks a managed PC only when no other PC can take the runner, and the approval names it
  as the managed PC.
- control_runner refuses a runner that runs in a console window. stop_job_processes says which processes it stopped.
- When GitHub cannot be reached, the tools say so with gh's reason instead of "Could not find" the repository.
- A call that waits for an approval reports that it waits every 20 seconds, so an AI tool with a short timeout does not
  give up. A call made before the fleet agent was running names the AI tool in its approval and can be cancelled.
- `RunnerManager.exe ai` writes UTF-8 when another program reads its output. The fleet agent log says once, not every
  30 seconds, that the AI assistant port is taken.
- An AI tool's name keeps its letters in scripts written from left to right, for example Cyrillic, Greek, Chinese and
  Hindi. When the fleet agent did not answer a sign-in in time, an AI tool is told so at once for the next 30 seconds
  instead of waiting again.
- **Settings → AI assistants** says why when **Allow AI assistants** or **Ask before changes** cannot be saved, and
  shows the setting that is in effect. Without administrator rights it says to start Runner Manager as administrator,
  before it creates a fleet code, installs the fleet agent or opens firewall ports.

### Setup and updates

- The fleet agent and the dashboard run at normal priority, so a build that uses every core no longer slows them down.
  Tasks that older versions registered are updated automatically.
- Setup keeps your desktop shortcut and **Start with Windows** choices when you install again. After a failure, Back
  returns to the options so you can try again, and Esc closes setup. Setup no longer stops AI tools that use Runner
  Manager, unless you uninstall or install to another folder.
- An update is marked as working only after its fleet agent answers on this PC. An update whose agent stays up without
  answering is kept, not rolled back later, and rollback messages say what happened.
- An update restarts the fleet agent once its commands and its AI assistant calls are done. It restarts the agent
  anyway when no command has finished for 70 minutes, or 3 hours after the update at the latest; work that still runs
  then is cut off.
- Updates leave RunnerManager.exe missing for a shorter time, and put it back when an update was cut off. Otherwise a
  missing exe is not replaced, and the dashboard asks you to run setup again.
- Downloads of updates stop when no data arrives for 60 seconds, and the .NET runtime download also has an overall
  limit, with a clear message. Updates remove only Runner Manager's own old files.
- When a dashboard does not respond, starting Runner Manager again offers to end it.
- After **Later**, the update banner says "Restart Runner Manager to finish the update" and restarts the dashboard.
- Runner Manager no longer changes its own scheduled tasks while they run, and leaves a task alone when Windows cannot
  say whether it runs. A path with spaces but without quotes, for example in an AI tool's configuration, is no longer
  taken for arguments.
- Uninstall waits until its message is closed, so RunnerManager.exe is always removed. Setup ignores Back and Next
  while it installs, shows "Step 2 of 3", and its options have names for screen readers. On a PC that is already in a
  fleet, setup's last page says that the PC stays in it, and no longer tells you to click "Add a PC" on another PC.

### Dashboard and windows

- A **Needs attention** tile counts PCs that are offline, runners that are stopped, and runners whose job looks stuck,
  runs long or waits although the runner is idle. A runner that was stopped with **Stop** is not counted. Click the
  tile to show only those; click it again to show everything. It is hidden when nothing needs attention, and it also
  works when a screen reader presses it.
- Danger dialogs (Delete runner, Leave fleet, Uninstall and the like) start on **Cancel**, so a repeated Enter no
  longer deletes anything. Long dialogs scroll, and their text can be selected and copied with Ctrl+C.
- Fix: actions on other PCs show their result. A command that could not be sent shows a dialog, and a failed start or
  stop ends the runner's pending state at once and shows the reason as an alert. Results stay in the status bar for 30
  seconds.
- An action started while another operation runs on this PC, also from Settings, says which one is still running, in
  one message.
- The dashboard shows the fleet at once, without waiting for the GitHub CLI, and keeps refreshing while an operation
  runs on this PC. Reading other PCs no longer makes the dashboard stutter, and a PC whose status has empty values is
  shown instead of being left out.
- The window fits a scaled screen, opens where and as large as it was, and comes back from the notification area
  maximized when it was. A window that was maximized on a screen that is gone lands on a screen when it is restored.
- Runners on other PCs show their labels and can be found by label. Search opens collapsed cards with matches, and
  says when the selected PC has none but other PCs do.
- A PC's state is not shown by color alone, and a collapsed card shows how many of its runners run and are busy, with
  a badge when one needs attention. The reason a PC cannot be reached has its own line, and cut-off text shows in full
  as a tooltip.
- The Output tab keeps a job's last output after the job ends, marked "Job finished", until the runner's next job or
  until the runner leaves the list, and says when updates are paused because text is selected.
- Card headers, runner rows and links work with Tab, Enter and Space, and Tab starts at the PCs. PC headers, runner
  headers and the rows of the queue and of recent runs are buttons for screen readers, and buttons, Settings pages,
  the pairing windows and setup have names for them, and the PCs and addresses in **Settings → Fleet** are read by
  what they show. A click in an open runner's details no longer closes it.
- Forgetting a PC from its card asks first, as Settings does, and also removes an IPv6 address the PC was added by.
- Faint text and status pills reach a contrast of 4.5:1, and text in buttons, menus and segments shows its style's
  color again, for example red Danger buttons.
- Fix: Ctrl shortcuts no longer fire with AltGr, which opened search or settings while typing @ on some keyboards.
  Ctrl with a number pad digit selects a PC.
- The activity and output panel can no longer squeeze the PC cards to nothing, and runner details and the sidebar
  fit a small window.
- The first load says that it is loading, a PC without runners offers **Add runner**, and **Connect GitHub** is a
  button. Texts count correctly ("1 runner"), and times follow the Windows region settings.
- The log viewer no longer shows old text after a failed refresh. Esc clears the filter and then closes it, and Ctrl+F
  goes to the filter.
- Settings: **Theme**, **Refresh every** and **Stuck after** apply at once, Enter saves the inline forms, and Copy
  in **AI assistants** shows "Copied".

## 2.8.2

- Fix: a busy PC, or one reached over the internet, kept dropping offline although its runners worked. Its fleet agent
  built its status while the request waited, which on a busy PC took up to 6 seconds, the time a dashboard waits. The
  agent now answers right away with its latest status and builds the next one in the background.
- A PC that misses one answer is no longer shown offline at once: only when it hasn't answered for 25 seconds, or
  refuses the request.

## 2.8.1

- Fix: a PC that joined the fleet over Tailscale (or from another network) did not appear on the PC that added it,
  because discovery only searches the local network. The PC that adds it now remembers where it joined from.
- **Add a PC** no longer picks **Join the fleet** for you: choose **Join the fleet** or **Manage it one way** before
  you can click Allow.

## 2.8.0

- **Manage a PC one way.** When a PC asks to join, choose **Manage it one way**: it gets a pairing code of its own
  instead of the fleet's. Your PCs and AI assistants can view and control it like your other PCs, but it can't see or
  control them. Use it for runners on a friend's PC, for example. The fleet's PCs share managed PCs with each other, so
  any of them can manage it.
- The card of a managed PC shows **Managed**, and its ⋯ menu has **Stop managing this PC…**, which also gives the PC a
  new code of its own. On the managed PC, **Settings → Fleet → Disconnect…** ends it there.
- **Change fleet code** also gives managed PCs new codes, so a PC that was left out can't reach them either.
- AI assistants: list_pcs says which PCs are managed.
- Fleet protocol 15: managed PCs, and the requests that share and release them.

## 2.7.1

- Click **Queue** or **Recent runs** in a runner's details to collapse or expand the list. The headings show how
  many jobs are running and waiting, and how many recent runs there are. Runner Manager remembers the choice for all
  runners.
- Fix: **Stop the job's processes** could also stop the job's console host, which ended the job itself. It now leaves
  the console host alone.

## 2.7.0

- **Stuck jobs.** A busy runner shows **Stuck** when its job has used no CPU, started no process and printed no
  output for 15 minutes, and **Running long** when it has run three times longer than the same job usually takes (at
  least 20 minutes; 2 hours for a job without earlier successful runs). You get a notification once per job, and
  the runner's details say what is wrong.
- **Stop the job's processes** in a runner's ⋯ menu, and on the note of a stuck job: stops everything the running job
  started, such as a Unity compiler that no longer does anything. The runner keeps running; the step fails and the job
  goes on or ends. It works for runners on other PCs that run 2.7.
- **Idle runners with waiting jobs.** A notification when a runner is idle while a job it can take has been queued for
  5 minutes, and when a queued job has no runner that can take it, or only stopped ones.
- **Queue in the runner's details:** the jobs this runner can take, running on a runner with the same labels or
  waiting, and for how long. It replaces the queue line, and now also works for runners on other PCs.
- **Settings → Notifications → Stuck and waiting jobs** turns these alerts on or off, and **Stuck after** sets the
  quiet time: 10, 15, 30 minutes or 1 hour.
- **AI assistants** see the same: list_runners and job_status say when a runner needs attention, job_status shows the
  queue, recent_alerts lists current problems and the alerts Runner Manager showed, and stop_job_processes stops a
  stuck job's processes (with the same approval as other changes).
- Fleet protocol 14: agents report when each job started and since when it has made no progress.

## 2.6.1

- **Fix: the fleet agent could stop starting after an update** ("Could not start the fleet agent: Element not found").
  When the agent, the dashboard and setup registered the same scheduled task at the same moment, Windows could lose
  track of it, so it never ran again, and the dashboard asked for UAC each time it started. Runner Manager now
  registers its tasks one at a time, checks that Windows can run each one, and repairs a task that Windows cannot
  start when the dashboard or the agent starts.

## 2.6.0

- **Remote Desktop Commander is no longer part of Runner Manager.** Its panel, tile, settings page, setup step and
  log are gone, and the fleet agent no longer starts or restarts it. A copy that is running keeps running until it
  stops or the PC restarts. Remove it, or start it yourself, as you prefer.
- **Collapse a PC's card** by clicking its name. Runner Manager remembers which cards are collapsed.
- **Runner details are more compact:** the job and recent runs are on the left, and the runner's facts (repository,
  service, version, work folder, labels and so on) are in a column on the right.
- **The live output of an open runner is in a tab next to Activity**, so you can switch between them, select and
  copy the output, and see up to 40 lines of it.
- The PC's status pill shows "Updating…" or "Restarting PC…" while Runner Manager updates or restarts it.
- The exe lists Sandiozo as its publisher, with a description and copyright, and the standalone setup is no longer
  compressed. Antivirus programs flag compressed self-extracting exes more often.

## 2.5.0

- **Start with Windows** in a runner's ⋯ menu: turn it off and the runner stays stopped after a restart until you
  start it. For a runner that runs in a console window, the option is **Start when you sign in**, and turning it off
  moves its startup entries aside, so turning it on again puts them back. The runner's details show how it starts.
  It works for runners on other PCs once they run 2.5.
- The setting is kept when a runner is renamed, repaired, converted to a Windows service or moved to another PC.
- A console runner that is not running is shown as a stopped console runner, with **Run as Windows service…**, instead
  of as a runner without a service.
- AI assistants see how each runner starts after a restart.
- A runner that has a Windows service but runs from a console window in your session instead is shown as a console
  runner. Before, it showed as stopped with a Start button, and starting its service, by hand or through
  auto-recovery, ran the runner twice. Auto-recovery now leaves its service alone, and moving it closes the console.
- The **Run as service** button of a console runner has the normal size and style, and the buttons in a runner's row
  no longer grow when a job line is shown. The note explains that a console runner can use your desktop, which builds
  that open windows, such as Unity, need, and converting asks whether its jobs need one.

## 2.4.1

- AI assistants see each runner's custom labels, such as `unity`, also for runners on this PC. In 2.4.0 the fleet agent
  left them out.
- Clicking a link's text in a runner row no longer shows an error.

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
