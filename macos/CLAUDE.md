# CLAUDE.md: macOS

Guidance for the macOS version, in `macos/`. The repo-wide notes are in `../CLAUDE.md`, and the shared Swift code is in `../common` (see its README).

## Commands

Swift 6 toolchain (Xcode or the command-line tools); language mode 5. Run these from `macos/`:

```
swift build
swift test                                   # HomebrewPlatform tests
(cd ../common && swift test)                 # shared tests: health rules, settings, provisioning on FakePlatform
swift run OctezDesk [--wizard]
OCTEZDESK_START_PAGE=node .build/debug/OctezDesk   # open one screen directly: node, baker, funds, dal, keys, dashboard
scripts/build-app.sh 0.3.0                   # dist/OctezDesk.app (universal) and dist/OctezDesk-0.3.0.dmg
```

`OCTEZDESK_START_PAGE` is for looking at screens, e.g. to screenshot them with `screencapture -l <window id>`. macOS doesn't let scripts send keystrokes here, so clicking through the app has to be done by hand.

**Running the app for real changes the machine.** Setup installs formulae, starts launchd agents and can download a large snapshot, and the dev Mac already has an Ushuaianet node in `~/.tezos-node` plus keys in `~/.tezos-client`. Ask before running setup, `brew services` or anything that writes there.

## Architecture

```
Sources/OctezDeskHomebrew   HomebrewPlatform (OctezPlatform for macOS), KeepAwake, HomebrewInstaller
Sources/OctezDesk           SwiftUI app: AppModel (navigation and wizard answers), wizard screens, SetupModel, DashboardModel
Tests/OctezDeskHomebrewTests
```

- **The shared logic lives in `../common`.** Provisioning steps, health rules, keys, the faucet and the records are there, so Linux gets them too. Put new logic there unless it's genuinely macOS-only. The health rules and the provisioning plan should behave the same on every platform.
- **`HomebrewPlatform`** finds `brew` (`/opt/homebrew` or `/usr/local`), and everything runs as the logged-in user:
  - always pass its `environment`, because GUI apps get a minimal `PATH`;
  - formulae are always named in full for brew (`qualifiedName`: the tap from the install receipt, else `drchrispinnock/octez`), because brew refuses a bare name that more than one tap has, such as a local dev tap;
  - the launchd label is `sh.brew.<formula>` on Homebrew 7 and `homebrew.mxcl.<formula>` before; `label(_:)` reads it from the plist in `opt/<formula>`;
  - `isInstalled` checks `$(brew --prefix)/opt/<formula>/bin`, so a formula from another tap (such as a local dev tap) counts as installed;
  - service state comes from the plist in `~/Library/LaunchAgents` (enabled) and `launchctl list` (running);
  - `restart` is bootout then bootstrap, not `kickstart -k`, so the node gets SIGTERM and shuts down cleanly.
- **Jobs** (the snapshot import) are a detached `sh` wrapper that records its PID, exit status and output in `~/Library/Application Support/OctezDesk/jobs`. Nothing kills them when the app quits.
- **The UI** is plain SwiftUI with `Form`. Long work runs in `Task`s; log lines go through `LogBuffer`, which batches them onto the main thread.

## Not done yet

- End-to-end testing of setup on a clean Mac (the tap install, `brew services`, the snapshot job, keys, the DAL node and the baker).
- Self-update (the Windows version uses Velopack; here Sparkle, or a Homebrew cask, would do).
- Notarisation and Developer ID signing.
- Listing a TCP remote signer's keys. HTTP(S) works, and TCP keys can be typed in.
- Unstaking from the keys screen. Register, stake and withdraw work.
- Reconfigure, and checking that the chosen ports are free.
- Running at boot as a dedicated user (`sudo brew services --sudo-service-user`).
