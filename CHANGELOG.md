# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/), and versions
follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0] — first public version

### Added
- Background agent with tray icon, panel (Italian and English, light and dark themes, keyboard and screen-reader friendly),
  command line with `--json` output.
- Modules: Windows errors, monitoring, game sessions and lag analysis, in-game overlay, PC settings with BIOS and firmware,
  data protection, order and cleanup, reminders, tools, graphics driver installs, first setup, emulation, scheduler,
  start-up programs, updates, audio devices, security, space, network (cable and Wi-Fi), game library with a full-screen
  controller view.
- PC organisation (where everything goes) with assisted moves that can be undone, and four ways of keeping the Desktop
  (warnings only by default, clean Desktop, Hub by category, custom).
- Laptops recognised: no desktop-only advice. Known tools (DDU, Equalizer APO, MSI Afterburner, HWiNFO…) get a button only
  where they are installed.
- Activity centre for long actions; guided tour on first opening.
- Installer (Program Files, data in `%ProgramData%\OptLife`, keeps the data on update, asks on uninstall).
- Self-update from this repository's GitHub releases, checked against the published SHA-256; never during a game.

### Known limits
- Not yet signed with a code-signing certificate: Windows SmartScreen warns on first run (see the README).
- Processor temperature and in-game frames need MSI Afterburner / RTSS; graphics card readings need an NVIDIA card (or
  Afterburner for AMD). Without them the values show as "not available", with what would bring them.
