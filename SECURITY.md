# Security

## Reporting a problem

Please **do not open a public issue** for a security problem. Use GitHub's private reporting instead:
**Security → Report a vulnerability** on this repository. You will get an answer within a week.

Useful to include: the OptLife version (`optlife status`), the Windows version, what you did, what happened, and whether it
needs administrator rights or another user's session.

## What OptLife does with elevated rights

The background agent runs as administrator through a scheduled task at sign-in, so that the actions you confirm (switching
start-up entries for all users, changing DNS servers, queued program updates, restart into the BIOS) need no Windows prompt
each time. Rules the code keeps:

- Nothing runs elevated by itself: every action has a permission (*automatic*, *ask*, *never*), and actions that remove
  something can never be automatic.
- Programs started for you (scheduled jobs, tools, winget updates) run with your own user token, not as administrator,
  unless a tool is explicitly marked "administrator" in your settings.
- **Only administrators change what the agent does.** In an installed copy the data folder (`%ProgramData%\OptLife`: history
  and settings) can be changed only by the system and administrators; other users may read it. The installer sets these rules
  and the agent checks them at every start. The panel therefore always opens with administrator rights: from the Start menu it
  asks the running agent to open it (as the tray icon does), or Windows asks for the rights. The command line reads from any
  terminal and changes things only from an administrator one.
- A scheduled job or a tool marked "administrator" runs only if its file cannot be changed by someone who is not an
  administrator (its owner, its rules and its folder are checked); otherwise it is refused with the reason.
- Running as administrator, OptLife never moves or writes files through a link (symbolic link, junction) in folders a user
  could change, so it cannot be led into Windows' own folders.
- A portable copy (OptLife unzipped in a folder of your own) trusts the owner of that folder: its program files are theirs to
  change anyway. Use the installer on a PC shared with other people.
- Shared memory, mutexes and events created by the agent allow only the system, administrators and the signed-in user.
- The data folder holds your history and settings only; nothing is sent anywhere (see the README, "Network").
- The self-update downloads only from this repository's releases and runs the installer only if its SHA-256 matches the
  `SHA256SUMS.txt` published in the same release.

## Supported versions

Only the latest release gets security fixes.
