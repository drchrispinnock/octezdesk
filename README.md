# OctezDesk

A desktop app for running Octez, the Tezos node software: a setup wizard, then a dashboard that shows how the node, DAL node and baker are doing.

| Platform | Directory | Status |
|----------|-----------|--------|
| Windows  | [`windows/`](windows/) | Available: download `OctezDesk.Win-win-Setup.exe` from [Releases](https://github.com/drchrispinnock/octezdesk/releases). Runs the node in WSL. |
| Linux    | [`linux/`](linux/) | Planned |
| macOS    | [`macos/`](macos/) | In progress: a SwiftUI app using the [drchrispinnock/octez](https://github.com/drchrispinnock/homebrew-octez) Homebrew tap |

[`spec/`](spec/) has the requirements and designs, [`common/`](common/) the Swift code shared by macOS and Linux, and [`assets/`](assets/) the app icon.
