# OctezDesk on Windows — design notes (Objective 1: node only)

Scope: wizard screens 1 (Welcome), 2 (Node) and 6 (Main), plus a progress screen between 2 and 6. Octez runs inside WSL 2 using the tzinit Debian packages. DAL, keys and the baker come later. This design should not block them.

## 1. Big picture

```
┌──────────────── Windows ────────────────┐      ┌──────── WSL 2 distro "OctezDesk" (Debian 13) ────────┐
│ OctezDesk.exe (GUI + tray)              │      │ systemd                                              │
│  ├─ Wizard / Main window                │ wsl  │  └─ octez-node.service  (user: tezos)                │
│  ├─ common: OctezHost abstraction ──────┼─────▶│ /etc/default/octez-node  (DATADIR, RUNTIME_OPTS)     │
│  │    run/read/write as root via wsl.exe│ .exe │ /var/tezos/.tezos-node   (config.json, store)        │
│  └─ health: HTTP to localhost:8732 ─────┼─────▶│ /var/log/tezos/octez-node.log                        │
└─────────────────────────────────────────┘ RPC  └──────────────────────────────────────────────────────┘
```

All control goes through `wsl.exe -d OctezDesk -u root -- <cmd>`. Running as root inside the distro needs **no Windows elevation**. The only time we need admin rights is installing the WSL feature itself, if it is missing.

## 2. Key decisions

### 2.1 A dedicated distro, not the user's default
- We create our own distro called `OctezDesk` so we never touch the user's own Ubuntu or Debian setup.
- We create it with `wsl --install Debian --name OctezDesk --location <dir> --no-launch`. This needs WSL 2.4.4 or later; we tested against 2.7.3. We don't ship a rootfs.
  - `<dir>` holds the VHDX. The user picks it in the wizard, because a rolling mainnet store is large.
  - After install we check `VERSION_CODENAME` is **trixie**, which tzinit builds for. If it isn't, we unregister the distro and fail. If the Store's Debian image ever moves past trixie, fall back to `wsl --import` with a pinned rootfs.
  - **To verify:** that the Debian image's first-run user setup doesn't run (and block) for our non-interactive `--exec` calls.
- We do **not** make the VHDX sparse. WSL 2.7 refuses `--set-sparse true` with "Sparse VHD support is currently disabled due to potential data corruption" unless `--allow-unsafe` is given. As a result the VHDX never shrinks; reclaiming space would need a manual compact. Revisit if WSL re-enables sparse VHDs.
- Uninstall or reset is `wsl --unregister OctezDesk`. This deletes everything, so always confirm first.

### 2.2 systemd inside WSL
- Write `/etc/wsl.conf`:
  ```
  [boot]
  systemd=true
  [user]
  default=root
  [interop]
  appendWindowsPath=false
  ```
  Then `wsl --terminate OctezDesk` so it restarts with systemd.
- This lets us use the package's own `octez-node` unit unchanged, exactly as on Linux. Linux and Windows then share the same service, config and upgrade logic.

### 2.3 Keeping the distro alive
WSL stops a distro when no Windows-side process is attached to it, even if systemd services are running. **Confirmed on WSL 2.7.3 (2026-11-04):** the distro was shut down about 20 s after the last attached process exited, and the node stopped cleanly. On the next `wsl.exe` call it booted again, and the enabled `octez-node` unit started automatically. Plan:
- **Implemented:** a windowless helper, `OctezDesk.Win.exe --keep-alive`, holds a hidden `wsl.exe -d OctezDesk -u root --exec sleep infinity`. It re-attaches if WSL restarts and exits if the distro is removed.
  - The GUI only configures and monitors. It starts the helper if it's missing, and exiting the GUI leaves the node running. Reopening it connects and shows health within a few seconds; both verified 2026-11-04.
  - Instead of a tray icon or Task Scheduler entry, starting the helper at sign-in is an **opt-in** HKCU Run entry, toggled from the ☰ menu.
- Limitation: WSL is per-user, so the node runs only while the user is logged in. That's acceptable for a node. Revisit when we get to baking.

### 2.4 Installing Octez (inside the distro, as root)
```
apt-get update
apt-get install -y extrepo
extrepo enable tzinit
apt-get update
apt-get install -y octez-node octez-client
```
- Fallback if `extrepo` doesn't have `tzinit`: use the manual keyring plus a `sources.list.d` entry for `https://apt.tzinit.org/debian trixie main`, and check the key fingerprint `181E6294A0770AE9AF0173B21F688E1201D819B4`.
- Older CPUs: check `/proc/cpuinfo` flags and switch to the `BLSTPORTABLE` repo variant if required. **To confirm: which flags.**
- Run apt with `DEBIAN_FRONTEND=noninteractive` and stream its output to the progress screen.

### 2.5 Configuring the node (screen 2 → commands)
| Wizard field | Becomes |
|---|---|
| Network (Mainnet, Shadownet, …) | `--network=mainnet`, or a teztnets URL such as `https://teztnets.com/shadownet` |
| History mode (rolling / full / archive) | `--history-mode=rolling` |
| Gossip port (9732) | `--net-addr="[::]:9732"` |
| RPC port (8732) | `--rpc-addr="127.0.0.1:8732"` |
| Enable all RPC | `--allow-all-rpc=127.0.0.1:8732` |
| Install from snapshot | §2.6 |

Run as `tezos`: `runuser -u tezos -- octez-node config init --data-dir /var/tezos/.tezos-node …`. The data directory comes from `DATADIR` in `/etc/default/octez-node`; don't hardcode it.

**Reconfigure** (burger menu) reopens screen 2 pre-filled from the node's `config.json`:
- Changing ports or RPC: stop the node, run `octez-node config update …`, then start it again.
- Changing network or history mode: the data directory has to be wiped and the snapshot reinstalled. Show a destructive-action warning.

### 2.6 Snapshot import
- Source: `https://snapshots.tzinit.org/<network>/<history-mode>`. **To confirm: the URL scheme for non-mainnet networks.**
- Run the download and import as a **transient systemd unit** inside the distro, so it survives the GUI being closed and its logs land in the journal:
  ```
  systemd-run --unit=octezdesk-snapshot --uid=tezos --property=Type=oneshot \
    sh -c 'cd /var/tezos && wget -O snap <url> && octez-node snapshot import snap && rm snap'
  ```
  The GUI polls `systemctl show octezdesk-snapshot` and tails the journal for progress. Only after it succeeds do we run `systemctl enable --now octez-node`.
- Without a snapshot, start the node straight away; it syncs from genesis.

### 2.7 Networking
- **RPC from Windows:** we expected WSL 2's default localhost forwarding to make the node's ports reachable at `localhost` on Windows. **It did not on the dev machine** (NAT mode, no `.wslconfig`): neither 8732 nor 9732 was reachable from Windows. So health checks run **inside the distro** (`wget http://127.0.0.1:<port>/…` via `IOctezHost`), which is also how a Linux build will work. We still need to understand why the forwarding failed (possibly Docker Desktop's distro), before exposing the RPC to Windows-side wallets or tools.
- **Inbound P2P (9732):** WSL 2's default NAT mode allows outbound connections only. A node works fine that way, though with fewer peers. Inbound connections need one of these:
  - `networkingMode=mirrored` in `%USERPROFILE%\.wslconfig` (Windows 11 22H2+) plus a Hyper-V firewall rule. Note that `.wslconfig` is **global to all of the user's distros**.
  - A `netsh interface portproxy` rule plus a Windows Firewall rule. This needs admin, and the WSL IP changes on every restart.
  - **Decision needed.** Proposal: outbound-only for Objective 1, with mirrored mode as an opt-in advanced setting later.
- `.wslconfig` also caps WSL memory (default 50% of RAM), and that cap is global too. Leave it alone for now.

## 3. Wizard flow on Windows

1. **Welcome.** Run prerequisite checks: Windows build, virtualisation enabled, and `wsl --status` / `wsl --version` to see whether WSL 2 is installed.
   - If WSL is missing, offer to run `wsl --install --no-distribution`. This shows a UAC prompt and may require a reboot. The wizard resumes after the reboot.
   - If the `OctezDesk` distro already exists with a configured node, skip straight to Main.
2. **Node.** The fields from the spec, plus the **data location** (where the VHDX goes) and a free-space hint.
   - The ports are validated, and we check they're free on Windows, because localhost forwarding can clash with them.
   - Next button reads "Set up".
3. **Setting up (progress).** A checklist with a live log:
   1. create the distro;
   2. enable systemd;
   3. add the repo and install;
   4. `config init`;
   5. snapshot download and import;
   6. enable and start the node.

   Each step is idempotent and re-checks the actual state, so a failed or interrupted run can be retried from the failing step.
4. **Main (screen 6), node row only:**
   - **Status dot:**
     - 🔴 red: `systemctl is-active octez-node` isn't `active`, or the RPC is unreachable.
     - 🟡 yellow: the service is active but `GET /chains/main/is_bootstrapped` returns `bootstrapped=false` or `sync_state` ≠ `synced`, or there are 0 peers (`/network/connections`).
     - 🟢 green: active and synced.
   - **Detail line:** head level and timestamp (`/chains/main/blocks/head/header`), peer count and version (`/version`).
   - **Logs:** `tail -n 500 -F /var/log/tezos/octez-node.log`, streamed through a long-lived `wsl.exe` process.
   - **Burger menu:**
     - **Upgrade:** `apt-get update && apt-get install --only-upgrade -y octez-node octez-client`, then restart. Show the version before and after.
     - **Reconfigure:** see §2.5.
     - **Restart:** `systemctl restart octez-node`.
     - **Shutdown:** `systemctl stop octez-node`, then release the keep-alive. Optionally `wsl --terminate OctezDesk`.

State is derived from the distro, not from a Windows-side settings file: whether it exists, whether the package is installed, and whether `config.json` exists. Reinstalling OctezDesk or losing `%APPDATA%` then recovers cleanly. `%APPDATA%\OctezDesk\settings.json` holds only UI preferences.

## 4. Code structure and stack (proposal)

The spec suggests Swift. On Windows, though, Swift has no mature GUI toolkit (swift-winrt is early), and Windows is the first target. **Recommendation: C# / .NET (current LTS).**

```
common/   OctezDesk.Core  (class library, no UI)
          ├─ IOctezHost          RunAsRoot, RunAsTezos, ReadFile, WriteFile, StreamFile
          │    ├─ WslHost        (Windows: wsl.exe -d OctezDesk -u … --)
          │    └─ LocalHost      (Linux later: pkexec/sudo; macOS: TBD)
          ├─ Provisioner        idempotent setup steps (repo, install, config, snapshot, service)
          ├─ NodeConfig         wizard model ⇄ octez-node CLI args ⇄ config.json
          ├─ NodeHealth         systemd state + RPC polling → Green/Yellow/Red
          └─ WslManager         (win-only bits: detect/install WSL, import/unregister distro, keep-alive)
win/      OctezDesk.Win  (WPF or WinUI 3 front end, tray icon, Task Scheduler autostart)
installer/ WiX MSI (per-user install, no admin needed)
gtk/      later: GTK 4 front end on the same Core (GirCore bindings)
mac/      later
tests/    Core unit tests against a fake IOctezHost; optional integration tests against a real WSL distro
```

- `IOctezHost` is where the platforms meet. On Linux the Provisioner, NodeConfig and NodeHealth classes run unchanged against a local host; only `WslManager` is specific to Windows.
- **WPF vs WinUI 3:** WPF is mature and packages simply with WiX. WinUI 3 looks more modern but brings Windows App SDK runtime and packaging overhead. Leaning WPF for Objective 1.
- **wsl.exe quirks:**
  - wsl.exe's own messages come out as UTF-16LE. Set `WSL_UTF8=1` when launching it.
  - Output from the Linux command passes through as UTF-8.
  - Always pass `--` before the command.
  - Avoid going through a shell where we can, and quote carefully where we can't.

## 5. Open questions
1. ~~Stack~~: decided, C#/.NET 10 + WPF.
2. ~~Debian 13 rootfs~~: decided, use `wsl --install Debian --name/--location` and check the release (§2.1).
3. Inbound P2P: is outbound-only acceptable for Objective 1?
4. ~~extrepo~~: `extrepo enable tzinit` works on the WSL Debian (trixie) image. The manual fallback stays as a safety net.
5. ~~Snapshot URLs~~: `https://snapshots.tzinit.org/shadownet/rolling` works (about 760 MB). The mainnet rolling snapshot is about 12 GB.
6. The node runs only while the user is logged in (a WSL limitation). Is that OK for now?
7. Product name: OctezDesk, or one of the other names from the sketch?
