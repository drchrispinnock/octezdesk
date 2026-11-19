# OctezDeskCore

The Swift package shared by the macOS and Linux versions of OctezDesk. It uses Foundation only, so it builds with swift-corelibs-foundation on Linux. It's a port of the Windows version's `OctezDesk.Core` (C#), without the WSL parts.

```
swift build
swift test
```

| Directory | Contents |
|-----------|----------|
| `Platform/` | `OctezPlatform`, the seam between the shared code and a platform: packages, services, settings files, files, running Octez tools, long jobs, following logs |
| `Process/` | `ProcessRunner`: runs a program without a shell, captures or streams its output, and terminates it on cancellation |
| `Model/` | Networks, wizard settings, the `.octezdesk-*.json` records (the same JSON as on Windows), settings-file parsing, `config.json` reading |
| `Health/` | `HealthEvaluator` (the red/yellow/green rules) and the node RPC client |
| `Keys/` | Baking keys (`baker`, `consensus`, `companion`), the octez-client wrapper, remote signers |
| `Funding/` | The teztnets faucet client, with its proof of work, and SHA-256 |
| `Services/` | Node and DAL node helpers, service control, the dashboard monitor and the installation probe |
| `Provisioning/` | The idempotent setup steps and `ProvisioningPlan.make`, which puts them together from the wizard's answers |

A platform implements `OctezPlatform`:
- macOS: `HomebrewPlatform` in `../macos`.
- Linux: still to write. It will use the tzinit Debian packages, systemd and the `tezos` user, as the Windows version does inside WSL.

The tests (`Tests/`) run the steps against `FakePlatform`, an in-memory platform.
