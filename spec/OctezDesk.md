# OctezDesk - a GUI interface to Octez

The purpose of this project is to make a GUI interface to Octez. It will have two main functions:

1. A set up wizard to help the user configure a node and optionally a DAL node and a baker.

2. A monitoring window to give state of the system to the user with logs and a menu to upgrade, restart services and shutdown.

On Windows, we will want an MSI or similar installer. On Mac, we will want an application (folder). On Linux we will use GTK and offer dpkgs and so on. 

Octez can be installed on Linux using the Debian packages system. On Windows, we can use the same packages in a WSL instance. On Mac, we will have to have a think. If we can use the Debian packages in the apt repos at tzinit.org, we can do upgrades easily.

The priority is: Windows, then Linux, then Mac.

Ideally we should share code, putting specifics in mac, win and gtk, and common code in common. We can use Swift, but I'm open to suggestions

The picture UX.png in this folder, gives a very rough indication of the setup wizard flow. on the first boot, you will enter the set up wizard.

Each screen has Back and Next.

1. Set up Wizard welcome screen

2. Node - Selectable Network (Mainnet, Shadownet, ...), Selectable history mode, Gossip and RPC ports. Enable all RPC is --allow-all-rpc. Then install from a snapshot means we will get a snapshot and install it before first run.

3. Baking key generation. We will figure out the detail here later.

4. DAL node. Once we have a baking key, we can set up the DAL node with the right attester. (Or not have DAL but with warnings).

5. Baker - capture the parameters like liquidty vote and keys (defaulting to ones we have in earlier screens).

6. Gives health - green dot good, yellow dot degrade, red dot not working. And a burger menu with Upgrade, Reconfigure, Restart, Shutdown.

Objective 1 - let's start by getting the node working. Do screens 1, 2 and 6

