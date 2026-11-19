# OctezDesk for Windows

A WPF app (C# / .NET 10) that runs the Octez node in its own WSL distro. Download the installer from [Releases](https://github.com/drchrispinnock/octezdesk/releases).

```
src/OctezDesk.Core          the logic: WSL, provisioning, health, keys (no UI)
src/OctezDesk.Win           the WPF app
tests/OctezDesk.Core.Tests  unit tests
scripts/release.ps1         builds the installer and update packages (Velopack)
release-notes/              one file per release
```

Build and test from this directory:

```
dotnet build OctezDesk.slnx
dotnet test tests/OctezDesk.Core.Tests
```

The design is in `spec/Windows.md` at the top of the repo.
