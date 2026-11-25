# Developing OptLife

OptLife compares how a Windows PC should be with how it is, and reports or fixes the difference. It is a normal program
with fixed rules: no AI, no network calls of its own beyond those listed in the README, no telemetry.

## Build and test

Requirements: Windows 10/11 x64, the .NET 10 SDK.

```powershell
dotnet build
dotnet test
```

Install a build on your own PC (into `C:\Software\OptLife`, data in a `dati` folder beside it, never touched):

```powershell
powershell -File publish.ps1                 # -Target <folder> to choose
powershell -File register-agent.ps1 -Admin   # the scheduled task that starts the agent at sign-in
```

Build the installer (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php), `winget install JRSoftware.InnoSetup`):

```powershell
powershell -File installer\build-installer.ps1 -Version 0.1.0
```

## Layout

| Folder | What it is |
|---|---|
| `src/OptLife.Core` | the contract (`Contracts.cs`), SQLite history (`Storage/Store.cs`), configuration, `ModuleRunner` (isolates module errors, action permissions), `Scheduler`, PC organisation (`Layout/`), self-update (`SelfUpdate/`) |
| `src/OptLife.Agent` | `optlife-agent.exe`: background, single instance, tray icon. The only process that writes live values |
| `src/OptLife.Panel` | `optlife-panel.exe` (WPF): logic in `ViewModels/` without WPF types (tested); theme in `Themes/` |
| `src/OptLife.Cli` | `optlife.exe`; every reading command accepts `--json` |
| `src/OptLife.Modules.*` | the modules |
| `tests/OptLife.Tests` | xUnit; each module's data source sits behind an interface |
| `installer/` | Inno Setup script and its build script |
| `.github/workflows/` | build and test on every push; release on a `v*` tag |

## The founding rule: modules come and go without touching the rest

- The core (`OptLife.Core`) never names a module. Modules are found by file name (`OptLife.Modules.*.dll`).
- A module sees only `Contracts.cs` (`IModule`, `IModuleContext`). Never references between modules.
- No module has tables of its own: it writes samples, events, findings (problems) and actions. No migrations.
- Data of other modules is read from `IHistory` and may be missing.
- Settings are declared by the module (`SettingDefinition`): the panel's menu and the CLI are generated from them.
  No paths or thresholds in code.
- Adding a module = a folder in `src/` and one line in `Modules.props` (and the solution).

## Concepts

- **Findings** (`NewFinding`): a problem with a key, a severity, a title, details, advice, an optional fix (`FindingFix`)
  and an area. `SyncFindings` opens new ones and closes those that went away. A finding the person *solved* stays solved
  until a newer episode; an *ignored* one stays ignored.
- **Actions** (`ModuleAction`): each has a permission (`Auto` / `Ask` / `Never`) the person can change. `NeverAuto` for
  anything that removes something. `Choices` = a list to tick before running. Every action goes into the action log with
  its undo.
- **Views** (`ModuleView`): a module declares pages (table of events, checklist, buttons, gallery, form); the panel draws
  them without knowing anything else.
- **Live values** (`LiveBoard`): shared memory written by the agent only; the panel, the overlay and `optlife live` read it.
  The key of a live value is the name of its series in the history. A `game.*` value means a game is running: modules do
  no heavy work then.
- **Texts in two languages**: `L.T("italiano", "english")`; in XAML `{local:T ITALIANO, ENGLISH}`. No texts in
  `static readonly` fields or constants (they would be fixed before the language is chosen): use `=>` properties.
  Tests run in Italian; do not change the language in tests, they run in parallel.
- **Accessibility**: keyboard-only use, contrast 4.5:1, state never by colour only, names for screen readers.

## Safety rules the code keeps

- Removals go to the Recycle Bin (`SHFileOperation` with undo), never automatic.
- No registry cleaning, no driver installation, no BIOS changes, no download of BIOS, firmware, keys or games.
- The agent is administrator; what it starts for the person runs with the person's token (`CreateProcessWithTokenW`).
- An action that starts a program says "started", not "opened": it cannot know.
- Never call the NVIDIA library (`nvml.dll`) while the NVIDIA installer is running: it can crash the process.

## Data

Installed under Program Files: `%ProgramData%\OptLife`, changed only by the system and administrators (`Protection.Harden`,
checked by the agent at every start); a panel without administrator rights asks the agent for one (`RequestOpenPanel`), the CLI
opens the history read-only (`Store.ReadOnly`). Anywhere else (portable): a `dati` folder beside the program, trusted to its owner.
Running as administrator, never write or move through a link (`Protection.ThroughLink`); start the person's programs with
`Protection.StartAsUser`; run a file with full rights only if `Protection.WritableByOthers` says no one else can change it.
`OPTLIFE_DATA` overrides both (use it for tests and snapshots on a copy of the data).
Samples older than 30 days and events older than 180 are deleted by the agent.

## Snapshots of the panel

```powershell
optlife-panel.exe --snapshot file.png [--expert] [--page Monitor|History|Actions|Report|Modules]
```

Use `OPTLIFE_DATA` on a copy of the data: `--expert` writes the preference.

## Releases

1. Update `CHANGELOG.md`.
2. Tag: `git tag v0.1.0 && git push origin v0.1.0`.
3. The release workflow builds, tests, makes the installer and `SHA256SUMS.txt`, and opens a **draft** release.
4. Check the draft, then publish it. Installed copies see it within a day.
