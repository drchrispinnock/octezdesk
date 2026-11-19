# OctezDesk for macOS

A SwiftUI app that sets up and monitors an Octez node, DAL node and baker on a Mac. It installs Octez from the [drchrispinnock/octez](https://github.com/drchrispinnock/homebrew-octez) Homebrew tap and runs it with `brew services`, so there's no virtual machine. Status: first version, not yet tested end to end.

Needs macOS 14 or later and [Homebrew](https://brew.sh). Runs on Apple silicon and Intel.

## Build

```
swift build                      # debug build
swift run OctezDesk              # run it (add --wizard to open the setup wizard even if a node is set up)
swift test                       # HomebrewPlatform tests; the shared tests are in ../common
scripts/build-app.sh 0.3.0       # tests, a universal release build, dist/OctezDesk.app and dist/OctezDesk-0.3.0.dmg
```

The app is signed ad hoc. Set `SIGN_IDENTITY` to a Developer ID to sign it properly; it isn't notarised yet. On another Mac, Gatekeeper will block the ad hoc build until it's allowed in System Settings › Privacy & Security.

## How it works

| | |
|---|---|
| Packages | `brew tap drchrispinnock/octez`, `brew trust`, then `brew install drchrispinnock/octez/octez-node octez-client` (and `octez-dal-node`, `octez-baker` when needed) |
| Services | launchd agents from `brew services start drchrispinnock/octez/<formula>` (labelled `sh.brew.octez-*` by Homebrew 7, `homebrew.mxcl.octez-*` before). They run as you and start whenever you log in. Stop uses `launchctl bootout`, so the service still starts at the next login. Deconfigure uses `brew services stop` |
| Settings | `$(brew --prefix)/etc/octez/octez-*.conf`. OctezDesk changes only `DATADIR` in the node's file, and writes the baker's in full, keeping a `.octezdesk-backup` of a file it didn't write |
| Data | `~/.tezos-node`, or another directory chosen in the wizard, plus `~/.tezos-dal-node` and `~/.tezos-client` (the wallet the baker uses) |
| Records | `~/Library/Application Support/OctezDesk/.octezdesk-{node,keys,dal}.json`, the same JSON as the Windows version |
| Logs | The dashboard follows Octez's daily logs (`<data dir>/daily_logs`, `~/.tezos-client/logs/octez-baker`). The service logs in `$(brew --prefix)/var/log/octez` get only warnings |
| Snapshot | Downloaded with `curl` and imported by a detached job (`~/Library/Application Support/OctezDesk/jobs`) that keeps going if OctezDesk quits |
| Sleep | ☰ › Keep this Mac awake installs a launchd agent that runs `caffeinate -i -s` |

**Existing installs are left alone.** If the data directory already has a node that OctezDesk didn't set up, the wizard uses it when it's for the network you chose (after backing up `config.json`) and refuses otherwise. Keys whose names are already in the wallet are reused, never overwritten.
