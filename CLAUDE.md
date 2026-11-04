# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

OctezDesk is a desktop GUI for running Octez (the Tezos node software). It has a setup wizard and a monitoring screen. Windows comes first, then Linux (GTK), then macOS. The current goal is **Objective 1: get a node running on Windows** (wizard screens 1, 2 and 6). The Baker screen (keys and liquidity baking vote), the Funds screen (test networks: faucet and staking) and the DAL node screen are also done.

Specs:
- `spec/OctezDesk.md`: the requirements.
- `spec/Windows.md`: the Windows design (WSL distro, provisioning, health rules), including its open questions.
- `spec/UX.png`: a hand-drawn wizard sketch, about 21 MB. Read it only if you need UI detail.

## Commands

Uses the .NET 10 SDK. The solution file is `OctezDesk.slnx`.

```
dotnet build OctezDesk.slnx
dotnet test tests/OctezDesk.Core.Tests
dotnet test tests/OctezDesk.Core.Tests --filter "FullyQualifiedName~HealthEvaluatorTests"   # one class
dotnet test tests/OctezDesk.Core.Tests --filter "DisplayName~Archive_cannot_use_snapshot"  # one test
dotnet run --project win/OctezDesk.Win
dotnet run --project win/OctezDesk.Win -- --wizard   # open the setup wizard even if the node is already set up
./scripts/release.ps1 -Version 0.2.1             # build the installer and update packages into artifacts/releases
./scripts/release.ps1 -Version 0.2.1 -Publish    # ...and publish GitHub release v0.2.1, which installed copies update from
```

**Releases use Velopack** (`vpk` CLI 1.2.161, matching the `Velopack` NuGet package; keep the two versions in step). The script runs the tests, publishes self-contained win-x64, downloads the previous release for a delta, then runs `vpk pack`. Bump `<Version>` in `OctezDesk.Win.csproj` to match. Release notes go in `release-notes/<version>.md`. Builds are unsigned for now. Rules:
- The pack ID is **`OctezDesk.Win`**, never `OctezDesk`. Velopack installs to `%LocalAppData%\<packId>` and deletes that folder on uninstall, and `%LocalAppData%\OctezDesk\wsl` holds the node's VHDX.
- The channel is always `win`.
- Updates come from GitHub Releases on the public repo (`AppUpdater`). Set `OCTEZDESK_UPDATE_URL` to a folder of `vpk pack` output to test against a local feed instead. A copy run from `bin/` never updates.
- **After its hooks, Velopack kills every process left running from the app folder.** So an update stops the keep-alive helper first (`OnBeforeUpdateFastCallback`). The helper is started again by Velopack restarting the app: as the GUI for "Restart now", or with `--keep-alive` for an update applied when the window closes. Don't start it from a hook.

```
```

**The keep-alive helper locks the build output.** `OctezDesk.Win.exe --keep-alive` runs from `bin/`, so the next build fails with "file is locked by OctezDesk.Win". Stop it gracefully before building:
```powershell
$h=$null; if ([Threading.EventWaitHandle]::TryOpenExisting('Local\OctezDesk.KeepAlive.Stop',[ref]$h)) { $h.Set() }
```
With the helper stopped, WSL stops the node about 20 s later, unless something else is attached to the distro. Opening the GUI starts the helper again.

Running the app for real affects the machine. Finishing the wizard creates a WSL distro called `OctezDesk` and downloads a snapshot. `wsl --unregister OctezDesk` deletes the distro and all node data, so ask the user before running it.

## Architecture

```
common/OctezDesk.Core   net10.0, no UI. All platform-neutral logic.
win/OctezDesk.Win       net10.0-windows WPF front end (CommunityToolkit.Mvvm)
tests/OctezDesk.Core.Tests  xUnit tests for Core, using FakeHost
gtk/, mac/              later front ends on the same Core
```

**`IOctezHost` (Core/Hosting) is the seam between platforms.** It runs a Linux argv as `root` or `tezos`.
- On Windows the implementation is `WslHost`, which calls `wsl.exe -d OctezDesk -u <user> --cd / --exec <argv>`. Using `--exec` means no shell is involved.
- A Linux build will have a local host implementation.
- Everything above the host is platform-neutral: `NodeService`, the provisioning steps and `NodeMonitor`.
- When you need a shell, use `host.ShAsync(script, args)` and refer to arguments as `"$1"` and so on; don't interpolate values into the script.
- Run apt through `host.AptGetAsync`, which runs it non-interactively.
- Text files written into the distro must use LF (no `AppendLine`, no `Environment.NewLine`); systemd reads `value\r` from CRLF. `ProcessRunner` joins captured output with LF to match.

**Octez comes from the tzinit Debian packages and follows their conventions** (`Core/Octez/OctezPaths.cs`):
- systemd unit `octez-node`;
- service user `tezos`;
- `/etc/default/octez-node`, whose `DATADIR` setting gives the data directory, read via `NodeService.GetDataDirAsync`; don't hardcode the path;
- log file `/var/log/tezos/octez-node.log`.

Upgrades are done with `apt`.

**Provisioning (`Core/Provisioning`)** is an ordered list of `IProvisioningStep`s.
- Each step's `IsDoneAsync` checks the *real* state of the host, so re-running after a failure or app restart resumes where it stopped. New steps must keep that property.
- `ProvisioningPlans.WindowsNode` assembles the Windows plan:
  1. create the distro (Debian, checked to be trixie);
  2. enable systemd through `/etc/wsl.conf`;
  3. enable WSL mirrored networking in the user's global `%UserProfile%\.wslconfig` (`EnableMirroredNetworkingStep`). It never fails setup, leaves a mode the user chose alone, and only runs `wsl --shutdown` when no other distro is running;
  4. install packages (`extrepo enable tzinit`, falling back to a manual keyring with a fingerprint check);
  5. `octez-node config init`, run as tezos;
  6. snapshot import, run as a transient systemd unit `octezdesk-snapshot` so it survives the GUI closing;
  7. `systemctl enable --now octez-node`;
  8. generate baking keys, only if chosen on screen 3 (`GenerateBakingKeysStep`, `Core/Octez/BakingKeys.cs`). This comes **after the node is up**, because octez-client waits about 20 s per call when no node answers. Keys go into the tezos user's default octez-client wallet, unencrypted, as `baker`, `consensus` and `companion`. Existing keys are reused, never overwritten. `/var/tezos/.octezdesk-keys.json` records which keys were made;
  9. if the DAL node is on (screen 4), install `octez-dal-node`, run `octez-dal-node config init` and start it (`Core/Provisioning/DalSteps.cs`). The attester is the `baker` master key or an address the user typed. `/var/tezos/.octezdesk-dal.json` records the settings. If it's off, disable one set up earlier;
  10. if baking, install `octez-baker`, write `/etc/default/octez-baker` in full and start it (`Core/Provisioning/BakerSteps.cs`). The file has `KEYS` (consensus or master key, then companion key), `DAL_OPTIONS` and `LQ_VOTE` (the liquidity baking vote from screen 3), and the step is done when it already has that exact content. If not baking, disable a baker set up earlier;
  11. on a test network, when setup creates a master key and the Funds screen (between Baker and DAL node) asked for it, as the **last** steps (`Core/Provisioning/FundingSteps.cs`): get test tez from the teztnets faucet (`Core/Octez/Faucet.cs`, which solves its proof-of-work challenges; at least 6,001 ꜩ), then register `baker` as a delegate (with `--companion-key companion` if there is one) and stake 6,000 ꜩ or 10% of the amount, whichever is higher. Each step checks the chain through the local node's RPC (balance, `staked_balance`, `delegates/<pkh>`), so a re-run skips what's on chain already. Mainnet funding is not done yet. **On mainnet OctezDesk never creates a master key**: the Baker screen offers only "Don't bake" and consensus and companion keys (`BakingKeyChoice.IsAllowedOn`, also enforced by `ProvisioningPlans.WindowsNode`), and the keys screen then shows the `octez-client` commands to register them from the user's own wallet.

**Configured settings are recorded in the distro.** `ConfigureNodeStep` writes `/var/tezos/.octezdesk-node.json` (`NodeSettingsRecord`). The step counts as done only if that record matches the wizard's choices, so going Back and changing the network doesn't silently keep the old config. If only ports or RPC access differ (same network and history mode), it runs `octez-node config update` and keeps the chain data, so re-running the wizard on a working node isn't a dead end. A different network or history mode replaces the config only if there's no chain data yet. A failed setup can always be left with Cancel, which goes back to the dashboard if a node is set up.

**State comes from the distro, not from Windows settings.** `InstallationProbe` checks whether WSL is installed, whether the distro exists, and whether the package and `config.json` are present. It uses that to pick between the wizard and the dashboard at startup.

**Health (`Core/Health`):**
- `NodeMonitor` gathers systemd state plus node RPC calls. The RPC calls run **inside the host** (`wget` to `127.0.0.1:<port>`, using the port from the node's `config.json`). They don't depend on Windows reaching the distro. The node RPC listens on `127.0.0.1` only (gossip on `0.0.0.0`), and with mirrored networking Windows tools reach it at `127.0.0.1:<port>` too (NAT-mode localhost forwarding didn't work on the dev machine; see spec/Windows.md §2.7). **Keep the RPC on localhost:** on any other address Octez applies its restrictive default ACL, which blocks `/monitor/heads/main`, and the DAL node exits. `SetNodeRpcAddressStep` repairs nodes set up by earlier builds that listened on `0.0.0.0`.
- The pure function `HealthEvaluator` maps that to Red, Yellow or Green. The rules are unit-tested; change them there.

**WSL specifics (`Core/Wsl`):**
- `WslManager` wraps the `wsl.exe` commands. Always set `WSL_UTF8=1`, otherwise wsl.exe's own output is UTF-16.
- WSL stops a distro, and the node with it, about 20 s after no Windows process is attached. This has been confirmed.
  - The **GUI never holds the distro.** A separate windowless helper, the same exe run with `--keep-alive` (`win/OctezDesk.Win/KeepAliveService.cs`), holds a `sleep infinity` open through `WslKeepAlive` and re-attaches if WSL restarts.
  - There is one helper per Windows session, enforced by the mutex `Local\OctezDesk.KeepAlive`, and it can be stopped through the event `Local\OctezDesk.KeepAlive.Stop`.
  - The GUI starts the helper when needed. Exiting the GUI leaves the node running.
  - Starting the helper at sign-in (the HKCU Run key) is **opt-in**, through a ☰ menu item. Never register it silently.
- The WSL Debian image is minimal (no wget or curl) and already boots with systemd, so `EnableSystemdStep` usually finds nothing to do.
- Sparse VHDs are disabled in current WSL, so don't use `--set-sparse`.
- There is no tray icon yet, so closing the window releases the distro and the node stops soon after.

**WPF app:**
- `MainViewModel` handles navigation by swapping the page view model in `CurrentPage`. `App.xaml` DataTemplates map each view model to its view.
- Work that touches Linux runs off the UI thread. Log lines go through `LogBuffer`, which hands them to the dispatcher.
- `AppServices` in `App.xaml.cs` is where all the services are created.

## Not done yet (Objective 1)

- Reconfigure. Its menu item is disabled; see spec/Windows.md §2.5.
- A design pass on the UX: the screens are functional but use unstyled default WPF.
- Code signing of the installer and app (SmartScreen warns "unknown publisher" until then).
- Allowing incoming P2P connections into WSL.

The open questions in spec/Windows.md §5 are still unresolved.
