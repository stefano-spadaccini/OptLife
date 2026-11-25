# Contributing

Thanks for helping. A few rules keep OptLife what it is:

- **No AI and no telemetry inside the program.** OptLife works offline with fixed rules.
- **Modules stay independent.** A module sees only `Contracts.cs`; the core never names a module; no module references
  another. Adding a module = a folder in `src/` and one line in `Modules.props`. See [docs/DEVELOPING.md](docs/DEVELOPING.md).
- **Never delete, never touch drivers, BIOS or registry cleaning.** Removals go to the Recycle Bin and are never automatic.
- **Defaults valid for anyone.** No personal paths, names or choices in default settings or texts.
- **Every text in two languages:** `L.T("italiano", "english")` in code, `{local:T ITALIANO, ENGLISH}` in XAML.
- **Accessible:** keyboard-only use, 4.5:1 contrast, state never shown by colour only, names for screen readers.
- **Tests:** each module has its data source behind an interface; add tests for the rules you change. `dotnet test` must pass.

Workflow: open an issue first for anything large, then a pull request against `main` with a short description of what and why.
Code and identifiers in English.
