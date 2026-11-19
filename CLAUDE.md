# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

OctezDesk is a desktop GUI for running Octez (the Tezos node software). It has a setup wizard and a monitoring screen. Windows comes first, then Linux, then macOS. The Windows version works (0.2.x): node, baker, DAL node, and on test networks the faucet and staking.

## Layout

Each platform has its own directory and its own build. Only the specs and the icon are shared.

```
spec/      requirements and designs, for all platforms
assets/    icon.png, the master app icon each platform makes its icons from
windows/   C# / .NET 10: Core library, WPF app, tests, release script, release notes. See windows/CLAUDE.md
linux/     not started: will use the Debian or Ubuntu packages directly (shell installer or a .deb)
macos/     not started: will use the Homebrew packages directly (DMG)
```

The Windows code isn't shared with the other platforms: Linux and macOS will probably be written separately (Swift, and Swift with GTK). They should still behave the same way: the same wizard screens, settings recorded the same way, and the same health rules (`windows/src/OctezDesk.Core/Health`), so port them rather than inventing new ones.

Specs:
- `spec/OctezDesk.md`: the requirements.
- `spec/Windows.md`: the Windows design (WSL distro, provisioning, health rules), including its open questions.
- `spec/UX.png`: a hand-drawn wizard sketch, about 21 MB. Read it only if you need UI detail.

## Releases

Releases are GitHub Releases on this repo, tagged `vX.Y.Z`; so far they hold only the Windows build. Installed Windows copies update from these releases (Velopack, channel `win`), so before publishing Linux or macOS files there, check how Velopack picks a release, and that a release without Windows files can't break Windows updates.
