# Security policy

## Supported versions

Only the latest release receives security fixes. Runner Manager updates itself from
[Releases](https://github.com/Sandiozo/runner-manager/releases), so updating is the fix.

## Reporting a vulnerability

Do not open a public issue for a security problem. Report it privately instead:

1. Open the [Security tab](https://github.com/Sandiozo/runner-manager/security) of this repository.
2. Click **Report a vulnerability**, and describe the problem, the affected version and how to reproduce it.

You get a reply within a week. Please give a reasonable time to release a fix before you disclose the problem.

## What is in scope

Runner Manager runs with administrator rights and listens on the network, so these areas matter most:

- **Fleet connection** (TCP 47821, UDP 47822): requests between PCs are encrypted and authenticated with AES-256-GCM,
  bound to the target PC and a timestamp, and checked against replays.
- **Pairing** (TCP 47823, UDP 47824, only while a PC is being added): ECDH P-256, confirmed with a 6-digit code. The
  new PC commits to its key before it sees the other one, so a device in between cannot make the codes match. The new
  PC uses the code it is sent only after its user confirms that the 6-digit codes match, and the PC that adds it saves
  it only after that confirmation (both PCs on 2.9 or later).
- **PCs managed one way**: such a PC gets an access code of its own instead of the fleet code. The fleet's PCs use that
  code to reach it, but they never accept it themselves, so the managed PC cannot see or control them, even with a
  modified Runner Manager. Removing it, or changing the fleet code, gives it a new access code that a PC with the old
  one does not know.
- **Other PCs' status**: what a PC reports about its runners is checked before it is shown or used: repository and
  organization names must be GitHub names, and the number of runners taken from one PC is limited.
- **Stored secrets**: the fleet code, and the access codes of PCs managed one way, are encrypted with Windows DPAPI for
  the current user.
- **Runner configuration**: runner names, labels and URLs are checked before they reach `config.cmd`.
- **Updates**: releases are verified with their SHA-256 checksum before they replace the installed copy, and an older
  version is never installed over a newer one.
- **AI assistants** (off by default): tool calls go to the fleet agent on the same PC. The assistant and the agent prove to
  each other with Windows DPAPI that they run as the same Windows user, and changes (including stopping a stuck job's
  processes) ask for approval in a Runner Manager window. Connect by URL listens on 127.0.0.1 only and needs a bearer token.
  The two switches, **Allow AI assistants** and **Ask before changes**, are stored in `HKLM\SOFTWARE\RunnerManager`,
  where only an administrator can change them; they count for every Windows user of the PC.
- **Job view**: the command lines of a job's processes are shown to the fleet's PCs and to AI assistants with passwords
  and tokens left out. A secret in a form that is not recognised is a problem to report.

Out of scope: problems that need administrator access to the PC already, and the security of GitHub Actions runners
themselves.
