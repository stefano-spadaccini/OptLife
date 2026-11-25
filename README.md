# OptLife — take care of your Windows PC

*[Leggi in italiano](README.it.md)*

OptLife compares **how your PC should be** with **how it is**, and tells you about the difference — or fixes it when you
press the button. It runs quietly next to the clock, has a panel to look at, and a command line for scripts.

It is an ordinary program with fixed rules: **no AI inside, no account, no telemetry, no ads**. It works offline.

## What it does

| Area | What OptLife watches |
|---|---|
| Hardware and performance | memory, processor, graphics card, disks and network every 2 s; problems over the last 24 h |
| Games | each game session second by second (FPS, stutters, ping, memory paging); press F9 to mark a lag and find what caused it; a light overlay over the game |
| PC settings | screen at its top refresh rate, memory at its rated speed (XMP/EXPO), power plan, Game Mode, BIOS and firmware (UEFI, Secure Boot, TPM, virtualization, Resizable BAR, PCIe link), DNS, audio devices; it notices when an update changes something behind your back. On a laptop it leaves out desktop-only advice (High performance plan, graphics card limits) |
| Network | the connection in use: on a cable its speed (a gigabit card stuck at 100 Mb), damaged packets and drops; on Wi-Fi band, link speed and signal; the ping to the router |
| Start-up | what starts with Windows, what must and must not, and programs that put themselves back |
| Updates | programs (through winget), emulators, graphics and chipset drivers, BIOS (link to the maker's page only), Windows Update |
| Security | Defender, firewall, accounts, risky Windows features, open ports |
| Data and copies | that your backups exist and are recent, Git repositories with work not committed or pushed |
| Order and space | how to keep the Desktop (warnings only — the default —, clean Desktop, Hub by category, or your own rules), broken shortcuts, full Downloads, heavy rebuildable folders, temporary files, Recycle Bin; every move is previewed and can be undone |
| Library | your PC and emulated games in one place, with a full-screen view for the controller |
| Tools | a scheduler for your own jobs, buttons for your scripts, "close background programs", restart into the BIOS, and buttons for known programs found on the PC (DDU, Equalizer APO, MSI Afterburner, HWiNFO…) and to empty the NVIDIA shader cache |

Every module can be switched off, and every action has a permission: *automatic*, *ask* or *never*.

## What it does not do

- It never deletes: what it removes goes to the Recycle Bin, and only when you press the button.
- It never installs drivers, never touches the BIOS, never cleans the registry.
- It never downloads BIOS files, firmware, keys or games.
- It sends nothing about you anywhere. See [Network](#network) for every connection it makes.

## Why it runs as administrator

The background agent runs as administrator (a scheduled task at sign-in) so that it can, **only when you ask**:
switch start-up entries registered for all users, change DNS servers, update programs without a Windows prompt for each
one, read Windows features and security settings, and restart into the BIOS. Programs it starts for you (your jobs, your
tools without the "administrator" mark, updates) run **as you**, not as administrator.
Because the settings decide what the agent runs, only an administrator may change them: the panel opens with administrator
rights (Windows may ask), and the command line changes settings only from an administrator terminal (reading works anywhere).
A job or tool marked "administrator" runs only from a file that non-administrators cannot change. Details in [SECURITY.md](SECURITY.md).

## Network

OptLife connects only to:

- your router, a public address and the hosts you list (NAS, backup server), for the ping — the addresses are in the settings;
- during a game, the game's server or its data centre, when the game writes it in its log (latency measure);
- `api.github.com` and `github.com` for new versions of emulators and of OptLife itself (no account, no token);
- winget and Windows Update (Microsoft) when updates are checked;
- for game covers in the Library: Steam's public store and image servers, GameTDB and the libretro thumbnail and
  database repositories on GitHub (all can be switched off with the Library's "online" setting).

Nothing else, and never with data about you. Buttons that open a site (a driver page, an app's page) open it in your browser.

## Requirements

- Windows 10 (22H2) or Windows 11, 64-bit.
- Nothing else to install: .NET is included.
- Optional: an NVIDIA card (graphics card readings come from its driver), MSI Afterburner (processor temperature, also on
  Ryzen; graphics card readings on AMD cards) and RTSS (frames in games). Without them those values show as "not available",
  with what would bring them; OptLife never downloads them, and finds Afterburner by itself once it is open.

## Install

1. Download `OptLife-Setup-<version>.exe` and `SHA256SUMS.txt` from the [Releases](../../releases) page.
2. Check the file is the one published (in PowerShell, in the download folder):
   ```powershell
   Get-FileHash .\OptLife-Setup-*.exe -Algorithm SHA256
   ```
   The value must be the same as the line in `SHA256SUMS.txt`.
3. Run the installer. **Windows SmartScreen** may say *"Windows protected your PC"*: early versions are not yet signed
   with a code-signing certificate. Click **More info → Run anyway** only if the SHA-256 matches.
4. The installer puts OptLife in `Program Files`, adds it to the Start menu and starts the agent at every sign-in.
   The first time the panel opens, a short guided tour helps you choose the language and what you care about.

Your history and settings are in `%ProgramData%\OptLife`. Updating installs over the old version and keeps them.

## Update

OptLife checks for its own new versions once a day (Settings → OptLife updates: *notify* or *automatic*).
It downloads the installer from this repository's releases, checks it against the published SHA-256, and never updates during a game.

## Uninstall

Settings → Apps → OptLife → Uninstall (or *Uninstall OptLife* in the Start menu). It asks whether to keep your history and settings.

## Command line

```powershell
optlife status --json        # what is wrong now
optlife findings             # the open problems
optlife events --since 7d    # what happened
optlife report --days 7      # the week in words
```

Every reading command accepts `--json`.

## Build from source

See [docs/DEVELOPING.md](docs/DEVELOPING.md).

## Licence

[MIT](LICENSE). Contributions are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md). Security problems: [SECURITY.md](SECURITY.md).
