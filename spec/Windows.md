# OctezDesk on Windows — design notes (Objective 1: node only)

Scope: wizard screens 1 (Welcome), 2 (Node), 3 (Baker), 4 (DAL node) and 6 (Main), plus a progress screen between 4 and 6. Octez runs inside WSL 2 using the tzinit Debian packages.

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
| Network (Mainnet, Shadownet, Bakingnet, Ushuaianet) | `--network=mainnet`, or a teztnets URL such as `https://teztnets.com/shadownet` |
| History mode (rolling / full / archive) | `--history-mode=rolling` |
| Gossip port (9732) | `--net-addr="0.0.0.0:9732"` |
| RPC port (8732) | `--rpc-addr="127.0.0.1:8732"` (localhost only; see §2.7) |
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
- **RPC from Windows:** we expected WSL 2's default localhost forwarding to make the node's ports reachable at `localhost` on Windows. **It did not on the dev machine** (NAT mode, no `.wslconfig`): neither 8732 nor 9732 was reachable from Windows, even with the node listening on `0.0.0.0`; only the distro's own IP worked.
  - **Decision (2026-11-04): use mirrored networking.** `EnableMirroredNetworkingStep`, run after systemd is enabled, adds `networkingMode=mirrored` under `[wsl2]` in `%USERPROFILE%\.wslconfig` (backing up an existing file to `.wslconfig.octezdesk-backup`) and runs `wsl --shutdown`. Verified on the dev machine: `http://127.0.0.1:8732` then works from Windows.
  - `.wslconfig` is **global to all of the user's distros**, so the step never fails setup. It leaves a `networkingMode` the user set themselves alone. It doesn't run `wsl --shutdown` while another distro is running; it logs that the change applies at the next WSL restart. On Windows 10 (mirrored needs Windows 11 22H2+) WSL falls back to NAT, and the step logs that.
  - The node RPC listens on `127.0.0.1` only (IPv4), so `127.0.0.1` works from Windows but `[::1]` doesn't. Clients that try `localhost` as IPv6 first may need `127.0.0.1`. Mirrored mode shares loopback with Windows; verified 2026-11-04 with a listener bound to `127.0.0.1` in the distro.
  - **The RPC must stay on localhost.** Until 2026-11-04 it listened on `0.0.0.0`. Octez then applied its restrictive default ACL (`acl = Secure`), which rejects `/monitor/heads/main`, and the DAL node exited with status 123 in a restart loop. On localhost the default ACL allows everything. `SetNodeRpcAddressStep` (after `ConfigureNodeStep`) moves an existing node to `127.0.0.1` with `octez-node config update` and restarts it. Gossip stays on `0.0.0.0`.
  - Without mirrored mode (Windows 10, or a NAT mode the user chose), Windows can no longer reach the RPC at all; with `0.0.0.0` it could, at the distro's IP. That is accepted: the DAL node and baker matter more.
  - With mirrored mode, the Hyper-V firewall's default inbound action is Block, so the node's ports are reachable from this PC but not from the LAN.
  - Health checks still run **inside the distro** (`wget http://127.0.0.1:<port>/…` via `IOctezHost`), which is also how a Linux build will work.
- **Inbound P2P (9732):** NAT mode allows outbound connections only, and mirrored mode's Hyper-V firewall blocks inbound by default. A node works fine that way, though with fewer peers. Inbound connections would need a Hyper-V firewall rule for 9732 (mirrored mode). **Not done yet.**
- `.wslconfig` also caps WSL memory (default 50% of RAM), and that cap is global too. Leave it alone for now.

## 3. Wizard flow on Windows

1. **Welcome.** Run prerequisite checks: Windows build, virtualisation enabled, and `wsl --status` / `wsl --version` to see whether WSL 2 is installed.
   - If WSL is missing, offer to run `wsl --install --no-distribution`. This shows a UAC prompt and may require a reboot. After the reboot the user reopens OctezDesk; nothing is lost, because nothing has been chosen yet.
   - **Implemented** (`Core/Wsl/WindowsPrerequisites.cs`; the rules are unit-tested). The checks, in order, and the first that fails blocks Next:
     1. Windows build at least 19041 (Windows 10 version 2004).
     2. Hardware virtualisation, from CIM: `Win32_Processor.VirtualizationFirmwareEnabled` or `Win32_ComputerSystem.HypervisorPresent`. While a hypervisor runs, Windows reports the firmware flag as False; the dev machine shows `False True`. If neither can be read, this check doesn't block.
     3. After OctezDesk ran `wsl --install` in this session: a restart, if Windows has one pending or `wsl --version` still fails.
     4. WSL installed (`wsl --version`), otherwise **Install WSL…**.
   - A pending restart found in the registry (`Component Based Servicing\RebootPending`, `WindowsUpdate\Auto Update\RebootRequired`) that OctezDesk didn't cause only shows a warning.
   - The installer never crashes the app. A missing `wsl.exe`, a declined UAC prompt and a non-zero exit each get a message.
   - If creating the distro fails, known WSL error codes get a plain explanation (`WslErrors`): virtualisation or Virtual Machine Platform off (`0x80370102`, `HCS_E_HYPERV_NOT_INSTALLED`), the WSL feature off, a WSL update needed, or no internet. The explanation comes before WSL's own message.
   - If the `OctezDesk` distro already exists with a configured node, skip straight to Main.
2. **Node.** The fields from the spec, plus the **data location** (where the VHDX goes) and a free-space hint.
   - The ports are validated, and we check they're free on Windows, because with mirrored networking the distro shares Windows' ports.
3. **Baker (screen 3).** The keys, as one of:
   - **Don't bake** (the default): no keys.
   - **Master key**, of type tz1 (Ed25519), tz2 (secp256k1), tz3 (P-256) or tz4 (BLS).
   - **Master and companion keys**, both tz4. A tz4 baker needs the companion key for DAL attestations.
   - **Consensus and companion keys**, both tz4, for a baker whose master key stays in another wallet or a Ledger.
   - **On mainnet only "Don't bake" and "Consensus and companion keys" are offered** (`BakingKeyChoice.IsAllowedOn`, and the plan refuses a master key there). The master key holds real tez, so it stays in the user's own wallet (octez-client, Signet or a Ledger), never unencrypted in the distro. When setup finishes, the keys screen shows the commands to run from that wallet: `register key <baker> as delegate --consensus-key … --consensus-key-pop … --companion-key … --companion-key-pop … --initial-stake 6000` for a new baker, or `set consensus key` / `set companion key` (and `stake`) for an existing one.
   - With a master key (test networks), the keys screen shows no proofs of possession. Its next steps come from the chain (balance, stake, registration, companion key), with a **Register as baker** button when the key has tez but isn't a delegate yet. When the key has stake there's an **Unstake** panel: an amount, or "Everything", via `octez-client unstake <tez|everything> for baker`. It asks for confirmation, and warns if less than 6,000 ꜩ would stay staked, because the baker then loses its rights. Unstaked tez show as frozen (`unstaked_frozen_balance`), then as ready to withdraw (`unstaked_finalizable_balance`), with a **Withdraw** button (`finalize unstake for baker`).

   The screen only records the choice, because octez-client isn't installed yet. Setup generates the keys once the node is running (octez-client is very slow without one). A results screen then shows each key's address and public key, plus a proof of possession for tz4 keys (`octez-client create bls proof for <alias>`). It also shows what to do next: `register key … as delegate [--companion-key …]`, or, for consensus and companion keys, the `set consensus key` / `set companion key … --…-key-pop` commands to run from the wallet that holds the master key.
   - Keys are generated as the tezos user into its default octez-client wallet (`/var/tezos/.tezos-client`), which is the one the packaged baker uses. They're **unencrypted**, so the baker can run unattended. Revisit with a remote signer.
   - The aliases are `baker`, `consensus` and `companion`. An existing alias of the right type is reused; one of the wrong type fails setup. Keys are never overwritten, because they may hold funds.
   - `/var/tezos/.octezdesk-keys.json` records the choice and aliases (public data only). It's used to check the step is done, and to give the DAL and baker screens their defaults.
   - From the dashboard, ☰ → **Baker keys…** shows the recorded keys. If there are none, it opens the same choice screen and generates only the keys, immediately; starting the baker needs the setup wizard.
   - **Liquidity baking vote** (Pass, On, Off; Pass is the default), shown when baking. It sets `LQ_VOTE` for the baker. The package's unit defaults to `break`, so the baker won't start until a vote is chosen.
   - **The baker.** When baking, setup installs `octez-baker` and writes `/etc/default/octez-baker` in full:
     - `DATADIR`: the node's data directory;
     - `KEYS`: the consensus or master key alias, then the companion key, which the baker needs for tz4 DAL attestations;
     - `DAL_OPTIONS`: `--dal-node http://127.0.0.1:<dal rpc>` when the DAL node is on, else `--without-dal`;
     - `LQ_VOTE`.

     The step is done when the file already has that exact content, and a running baker is restarted when it changes. Then `systemctl enable --now octez-baker` (`run with local node`) runs after the node and the DAL node. With "Don't bake", setup stops and disables a baker set up earlier, keeping its package and keys.
   - Files written into the distro use LF line endings: systemd reads `pass\r` from a CRLF EnvironmentFile. `ProcessRunner` also joins captured output with LF, so a file read back with `cat` compares equal to what was written.
4. **Funds (screen 4, test networks only).** Shown only when the network isn't mainnet and the Baker screen creates a master key ("Master key" or "Master and companion"). Otherwise the wizard goes straight from Baker to DAL node. Mainnet funding comes later.
   - **Get test tez from the faucet** (on by default): an amount of at least **6,001 ꜩ** (6,000 to stake plus fees), and at most the faucet's `maxTez`, which the screen reads from `/info` (2026-11-04: Shadownet 6,001, Bakingnet 24,000, Ushuaianet 40,000).
   - **Register as a baker and stake** (on by default, needs funding): stakes **6,000 ꜩ or 10% of the amount, whichever is higher**.
   - The key doesn't exist yet at this point, so setup does the work, as its **last** steps. Then the node, DAL node and baker are set up even if the faucet is down, and the node has had the longest to catch up:
     1. `FundFromFaucetStep`. The faucet is `https://faucet.<network>.teztnets.com`, from github.com/tacoinfra/tezos-faucet-backend. If `/info` says `challengesEnabled`, the step `POST`s `/challenge {address, amount}`, then repeats `POST /verify {address, nonce, solution}` until it gets a `txHash`. A solution is SHA-256 of `"<challenge>:<nonce>"` in hex, starting with `difficulty` zeros, and the nonce starts from 1. With challenges off, a single `POST /verify {address, amount}` does it. No captcha; one would only reduce the number of challenges (2026-11-04, Bakingnet: 251 challenges at difficulty 3 for 6,001 ꜩ, a few seconds of CPU). The step requests only what's missing, then waits for the local node to see the balance. It's done when spendable plus staked is at least the amount, less 1 ꜩ for fees already paid.
     2. `RegisterDelegateStep`: `octez-client register key baker as delegate [--companion-key companion]`, after `octez-client bootstrapped`. Done when `/chains/main/blocks/head/context/delegates/<pkh>` answers (it errors with `delegate.not_registered` until then).
     3. `StakeStep`: `octez-client stake <missing> for baker`. Done when `staked_balance` reaches the stake.
5. **DAL node (screen 5).** A checkbox to run a DAL node. It's **on by default when baking and off otherwise**. Turning it off while baking shows a warning that the baker will miss DAL attestations and rewards.
   - **Attester.** If screen 3 creates a master key (the master key options), the DAL node attests for it. Setup resolves its address once the key exists. Otherwise the screen asks for the address of the baker's master key (tz1 to tz4). It's required when baking with consensus and companion keys, and optional when not baking (no attester profile).
   - **Ports:** P2P 11732 (`--net-addr 0.0.0.0:…`) and RPC 10732 (`--rpc-addr 127.0.0.1:…`, so only the baker in the distro uses it). They must differ from each other and from the node's.
   - The tzinit `octez-dal-node` package (about 640 MB, mostly the DAL trusted setup) provides the unit `octez-dal-node`, `DATADIR` in `/etc/default/octez-dal-node` (default `/var/tezos/.tezos-dal-node`) and the log file `/var/log/tezos/octez-dal-node.log`.
   - `octez-dal-node config init --data-dir … --endpoint http://127.0.0.1:<node rpc> --rpc-addr … --net-addr … [--attester-profiles <pkh>]` runs as tezos. `/var/tezos/.octezdesk-dal.json` records the attester and ports. A changed record rewrites `config.json`, keeping the DAL data, and restarts a running DAL node.
   - If the DAL node is off, setup stops and disables one set up earlier, but keeps the package and data.
   - Upgrade also upgrades `octez-dal-node` when it's installed, and restarts it if it's running.
   - Dashboard row: grey if not set up, green if running, red if enabled but not running. Details show the attester and ports.
6. **Setting up (progress).** A checklist with a live log:
   1. create the distro;
   2. enable systemd;
   3. enable mirrored networking (§2.7);
   4. add the repo and install;
   5. `config init`;
   6. snapshot download and import;
   7. enable and start the node;
   8. generate the baking keys, if any were chosen. This needs the node up: without one, each octez-client call waits about 20 s for it, and a tz4 key with its proof of possession takes several calls. The step first waits up to a minute for the node's RPC;
   9. install, configure and start the DAL node if it's on, or turn off one set up earlier;
   10. install, configure and start the baker if baking, or turn off one set up earlier.

   Each step is idempotent and re-checks the actual state, so a failed or interrupted run can be retried from the failing step.
7. **Main:**
   - **Status dot:**
     - 🔴 red: `systemctl is-active octez-node` isn't `active`, or the RPC is unreachable.
     - 🟡 yellow: the service is active but `GET /chains/main/is_bootstrapped` returns `bootstrapped=false` or `sync_state` ≠ `synced`, or there are 0 peers (`/network/connections`).
     - 🟢 green: active and synced.
   - **Detail line:** head level and timestamp (`/chains/main/blocks/head/header`), peer count and version (`/version`).
   - **Logs:** `tail -n 500 -F /var/log/tezos/octez-node.log`, streamed through a long-lived `wsl.exe` process.
   - **Burger menu:**
     - **Upgrade:** `apt-get update && apt-get install --only-upgrade -y octez-node octez-client octez-dal-node octez-baker`, then restart (the DAL node and baker with `try-restart`). Show the version before and after.
     - **Setup wizard…:** runs the wizard again, for example to add baking keys or a DAL node. Screens 2 to 4 are prefilled from what's installed (`.octezdesk-*.json` and the units' state), so pressing Next throughout keeps everything as it is, and setup skips the finished steps.
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
scripts/release.ps1  Velopack installer and update packages (see §4.1)
gtk/      later: GTK 4 front end on the same Core (GirCore bindings)
mac/      later
tests/    Core unit tests against a fake IOctezHost; optional integration tests against a real WSL distro
```

- `IOctezHost` is where the platforms meet. On Linux the Provisioner, NodeConfig and NodeHealth classes run unchanged against a local host; only `WslManager` is specific to Windows.
- **WPF vs WinUI 3:** WPF is mature and packages simply (Velopack, §4.1). WinUI 3 looks more modern but brings Windows App SDK runtime and packaging overhead. Leaning WPF for Objective 1.
- **wsl.exe quirks:**
  - wsl.exe's own messages come out as UTF-16LE. Set `WSL_UTF8=1` when launching it.
  - Output from the Linux command passes through as UTF-8.
  - Always pass `--` before the command.
  - Avoid going through a shell where we can, and quote carefully where we can't.

### 4.1 Packaging and updates (from 0.2)
**Decision (2026-11-04): Velopack instead of a WiX MSI**, because it does both installing and self-updating.
- **Installer.** `OctezDesk.Win-win-Setup.exe` installs per user into `%LocalAppData%\OctezDesk.Win`, without admin, and adds Start menu and desktop shortcuts. The build is self-contained win-x64, about 73 MB, so no .NET runtime is needed. Unsigned for now: SmartScreen warns until there's a certificate, and `vpk pack` takes `--signParams` or `--azureTrustedSignFile` when there is.
- **Pack ID `OctezDesk.Win`, not `OctezDesk`.** Uninstalling deletes `%LocalAppData%\<packId>`, and `%LocalAppData%\OctezDesk\wsl` holds the node's VHDX. Uninstalling removes the app only. The distro, node data and keys stay; `wsl --unregister OctezDesk` removes them.
- **Updates.** Releases are on the public GitHub repo, made by `scripts/release.ps1 -Publish`. The dashboard checks at start and every 6 hours, and downloads in the background, as a delta when possible (0.3 MB in testing). A banner then offers **Restart now**. Otherwise the update installs silently when the window closes. ☰ → *Check for OctezDesk updates* checks straight away.
- **The keep-alive helper during an update.** Velopack's `OnBeforeUpdateFastCallback` stops the helper, because it runs from the exe being replaced. **After its hooks, Velopack kills every process left running from the app folder**, so nothing is started from a hook. Instead Velopack restarts the app: as the GUI after Restart now (the dashboard starts the helper), or with `--keep-alive` for an update applied on close. Tested 2026-11-04 with a local feed (0.1.92 → 0.1.93, applied on close): the helper came back from the new version, and the node, DAL node and baker kept running without a restart. Uninstalling stops the helper and removes the sign-in Run entry; that's tested too.
- **Testing updates locally:** set `OCTEZDESK_UPDATE_URL` to a folder of `vpk pack` output.

## 5. Open questions
1. ~~Stack~~: decided, C#/.NET 10 + WPF.
2. ~~Debian 13 rootfs~~: decided, use `wsl --install Debian --name/--location` and check the release (§2.1).
3. Inbound P2P: is outbound-only acceptable for Objective 1?
4. ~~extrepo~~: `extrepo enable tzinit` works on the WSL Debian (trixie) image. The manual fallback stays as a safety net.
5. ~~Snapshot URLs~~: `https://snapshots.tzinit.org/shadownet/rolling` works (about 760 MB). The mainnet rolling snapshot is about 12 GB.
6. The node runs only while the user is logged in (a WSL limitation). Is that OK for now?
7. Product name: OctezDesk, or one of the other names from the sketch?
