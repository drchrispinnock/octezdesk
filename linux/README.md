# OctezDesk for Linux

Not started yet. The plan:

- Install Octez from the Debian or Ubuntu packages directly, with no virtual machine in between.
- Ship as a shell installer or as a `.deb` package.
- Follow the same setup routine as the Windows version: the same wizard screens and the same health rules. See `spec/OctezDesk.md` and, for how Windows does it, `spec/Windows.md`.
- Swift with GTK, reusing the shared Swift package in `common/` (provisioning, health rules, keys, faucet) through a Debian implementation of its `OctezPlatform` protocol, as `macos/` does with Homebrew.
