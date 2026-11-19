# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

OctezDesk is a desktop GUI for running Octez (the Tezos node software). It has a setup wizard and a monitoring screen. Windows comes first, then Linux, then macOS. The Windows version works (0.2.x): node, baker, DAL node, and on test networks the faucet and staking.

`TODO.md` is the to-do list. Tick items there when you finish them.

## Layout

Each platform has its own directory and its own build. The specs and the icon are shared by all three, and `common/` by macOS and Linux.

```
spec/      requirements and designs, for all platforms
assets/    icon.png, the master app icon each platform makes its icons from
windows/   C# / .NET 10: Core library, WPF app, tests, release script, release notes. See windows/CLAUDE.md
common/    Swift package OctezDeskCore, shared by macOS and Linux: provisioning, health, keys, faucet. See common/README.md
macos/     Swift / SwiftUI app using the drchrispinnock/octez Homebrew tap and brew services (DMG). See macos/CLAUDE.md
linux/     not started: Swift with GTK, using common/ and the tzinit Debian packages (shell installer or a .deb)
```

The Windows code isn't shared with the other platforms. `common/` is a Swift port of the Windows Core, minus WSL, and macOS and Linux both use it through the `OctezPlatform` protocol. All three should behave the same way: the same wizard screens, settings recorded the same way (`.octezdesk-*.json`, with the same JSON), and the same health rules. When you change a rule or a step on one platform, change it on the others, or note that they differ.

Specs:
- `spec/OctezDesk.md`: the requirements.
- `spec/Windows.md`: the Windows design (WSL distro, provisioning, health rules), including its open questions.
- `spec/UX.png`: a hand-drawn wizard sketch, about 21 MB. Read it only if you need UI detail.

## Releases

Releases are GitHub Releases on this repo, tagged `vX.Y.Z`; so far they hold only the Windows build. Installed Windows copies update from these releases (Velopack, channel `win`), so before publishing Linux or macOS files there, check how Velopack picks a release, and that a release without Windows files can't break Windows updates.
