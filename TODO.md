## Realignment and bug fixes 0.2.4

- [x] Instead of going to the Setup Wizard, if we haven't setup the baker or DAL-node, have separate menu items
- [x] Use OctezDesk, but if it already exists we need to handle it and not stomp on anything
- [x] On the Menu we should have:
  - [x] Start... (Node, Baker, DAL)
  - [x] Stop... (Node, Baker, DAL)
  - [x] Restart...
  - [x] Deconfigure and stop.
- [x] Another way to get to keys would be to connect to a remote signer - HTTP, HTTPS, TCP and a port number (use 6732, 443, 7732 as standard), allow to list keys.
- [x] Is there a secure enclave we could use on the machine to make keys? Probably not BLS ones though...

## Monorepo, Linux and Macintosh 0.3

- [x] Re-organise the source code repository to contain the windows version
- [x] Directories for Linux and Mac
- [ ] Linux will use the Debian or Ubuntu packages directly. Shell installer or deb package itself.
- [x] Mac will use the Homebrew packages directly. DMG.
- [ ] Same setup routine. You could use Swift and Swift + GTK for Linux
- [ ] Ideally want to reuse what code we can, but tricky with C#
- [ ] We can use Claude on another machine for this
