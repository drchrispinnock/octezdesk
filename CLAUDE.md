# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

OctezDesk is a desktop GUI for running Octez (the Tezos node software). It has a setup wizard and a monitoring screen. Windows comes first, then Linux (GTK), then macOS. The current goal is **Objective 1: get a node running on Windows** (wizard screens 1, 2 and 6). The DAL node, keys and the baker come later.

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
  3. enable WSL mirrored networking in the user's global `%UserProfile%.wslconfig` (`EnableMirroredNetworkingStep`). It never fails setup, leaves a mode the user chose alone, and only runs `wsl --shutdown` when no other distro is running;
  4. install packages (`extrepo enable tzinit`, falling back to a manual keyring with a fingerprint check);
  5. `octez-node config init`, run as tezos;
  6. snapshot import, run as a transient systemd unit `octezdesk-snapshot` so it survives the GUI closing;
  7. `systemctl enable --now octez-node`.

**Configured settings are recorded in the distro.** `ConfigureNodeStep` writes `/var/tezos/.octezdesk-node.json` (`NodeSettingsRecord`). The step counts as done only if that record matches the wizard's choices, so going Back and changing the network doesn't silently keep the old config. A config that doesn't match is replaced only if there's no chain data yet.

**State comes from the distro, not from Windows settings.** `InstallationProbe` checks whether WSL is installed, whether the distro exists, and whether the package and `config.json` are present. It uses that to pick between the wizard and the dashboard at startup.

**Health (`Core/Health`):**
- `NodeMonitor` gathers systemd state plus node RPC calls. The RPC calls run **inside the host** (`wget` to `127.0.0.1:<port>`, using the port from the node's `config.json`). They don't depend on Windows reaching the distro. The node listens on `0.0.0.0`, and with mirrored networking Windows tools reach it at `127.0.0.1:<port>` (NAT-mode localhost forwarding didn't work on the dev machine; see spec/Windows.md §2.7).
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
- The MSI installer, planned to use WiX.
- Allowing incoming P2P connections into WSL.

The open questions in spec/Windows.md §5 are still unresolved.
