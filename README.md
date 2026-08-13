# BCS Tool

BCS Tool is a Windows desktop application for managing a **Bannerlord Coop dedicated server**.

It provides a graphical interface for starting, stopping, saving, restarting, monitoring, and configuring the server without requiring a separate command-line management workflow.

``
**Current release:** v0.2.3
``

## Features

* Manual server restart
* Automatic scheduled restart
* Restart warning message broadcast
* Automatic crash recovery
* Built-in live server log console with local command input
* Automatic server executable detection
* Server configuration modification
* Mod configuration modification
* Save backups
* Backup restore

## Requirements

* Windows 10 or Windows 11, 64-bit
* Mount \& Blade II: Bannerlord
* Bannerlord Coop / Bannerlord Coop dedicated server
* Steam installation is supported for automatic server detection

## Download

Download the latest compiled version from the repository's **Releases** page.

For normal use, download:

```text
BCS Tool v<version>.exe
```

The files automatically provided by GitHub as `Source code (zip)` and `Source code (tar.gz)` contain the project source rather than the compiled application.

## Getting Started

1. Launch the versioned executable, for example **BCS Tool vX.Y.Z.exe**.
2. BCS Tool will try to locate `BannerlordCoopServer.exe` automatically.
3. If the executable is not detected, use **Browse** to select it manually.
4. Press **Start** to launch the server.
5. Configure save-backup rotation under **Server Configuration → Save Backups** if desired.

## Configuration Editors

BCS Tool includes editors for the Bannerlord Coop server and mod configuration files.

### Server configuration

```text
Documents\\Mount and Blade II Bannerlord\\CoopData\\DedicatedServer\\server-config.json
```

### Mod configuration

```text
Documents\\Mount and Blade II Bannerlord\\CoopData\\mod-config.json
```

If either configuration file does not exist yet, start the server once so Bannerlord Coop can generate it.

BCS Tool preserves the existing JSONC structure and comments when saving supported settings, and creates a `.bak` backup before overwriting a configuration file.

## Save Backups

BCS Tool can maintain a rotating history of Bannerlord Coop campaign saves.

A Bannerlord Coop save consists of two companion files with the same base name:

```text
saveauto1.sav
saveauto1.json
```

BCS Tool treats these files as a single save pair. Backup rotation and manual restore always operate on both files together.

### Backup rotation

Backup rotation is configured under **Server Configuration → Save Backups**.

The number of retained backup generations can be set from **1 to 5**.

After the server reports that a save completed successfully, BCS Tool waits for both save files to become stable before creating the next backup generation.

For a save named `saveauto1`, the rotating backups are named:

```text
saveauto1.backup1.sav
saveauto1.backup1.json

saveauto1.backup2.sav
saveauto1.backup2.json

...

saveauto1.backup5.sav
saveauto1.backup5.json
```

`backup1` is the newest retained backup. Higher numbers are progressively older.

For example, with three backups retained, a new backup rotates the existing history as follows:

```text
backup2 -> backup3
backup1 -> backup2
current save -> backup1
```

The active save remains unchanged in name:

```text
saveauto1.sav
saveauto1.json
```

Backup files are stored under:

```text
Documents\Mount and Blade II Bannerlord\CoopData\DedicatedServer\Game Saves\BCS Backups
```

The **Open Backup Folder** button opens this directory. The folder is created only after BCS Tool successfully creates its first backup.

Disabling backup rotation stops new backups from being created but does not delete existing backups.

### Manual backup restore

The **Load Backup** button opens a list of available complete backup generations and their modification dates.

A backup can only be loaded while the managed server is **fully stopped**. If the server is starting, running, saving, stopping, restarting, or otherwise not in the normal `Stopped` state, BCS Tool will require the server to be stopped before continuing.

Selecting a backup and pressing **Apply** replaces the current save pair.

For example, loading:

```text
saveauto1.backup3
```

restores:

```text
saveauto1.backup3.sav  -> saveauto1.sav
saveauto1.backup3.json -> saveauto1.json
```

The backup files themselves remain in the backup directory after being loaded.

BCS Tool stages the selected pair and temporarily preserves the current active pair while applying the restore to reduce the chance of an ordinary file-copy failure leaving the `.sav` and `.json` files mismatched.

> **Note:** The current system provides rotating backups and manual restore. Automatic save-corruption detection and automatic rollback are not currently implemented.

## Automatic Restarts

BCS Tool can periodically restart the server at a configured interval and minute.

Before a scheduled restart, it can:

1. Broadcast countdown warnings
2. Save the campaign
3. Stop the server gracefully
4. Wait for shutdown
5. Restart the server

Crash recovery can also restart the server automatically if the managed process exits unexpectedly.

## Building from Source

BCS Tool is a WPF application targeting:

```text
.NET 10
Windows x64
```

### Visual Studio

1. Install Visual Studio with the **.NET desktop development** workload.
2. Install the .NET 10 SDK if it is not already available.
3. Open:

```text
BCSTool.sln
```

4. Build the solution normally for development.

### Publish a standalone EXE

The project includes:

```text
BCSTool\\Publish-SingleExe.ps1
```

Run it from PowerShell in the project directory:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\\Publish-SingleExe.ps1
```

The resulting standalone executable will be placed in:

```text
publish\\BCS Tool v<version>.exe
```

The publish configuration is:

* Release
* `win-x64`
* Self-contained
* Single-file executable

## Versioning

BCS Tool uses semantic-style version numbers:

```text
0.2.3
```

The application version is defined in:

```text
BCSTool.csproj
```

For example:

```xml
<Version>0.2.3</Version>
```

The UI reads the compiled application version at runtime, so the project version is the single source of truth for release numbering.

GitHub release tags should use the corresponding `v` prefix:

```text
v0.2.3
```

## Development Status

BCS Tool is currently an early-stage project.

The `0.x` version number indicates that features, behavior, and configuration handling may continue to change before a stable `1.0.0` release.

If you encounter a reproducible problem, please open a GitHub Issue and include relevant BCS Tool logs and a description of what happened.

## License

BCS Tool is free and open-source software licensed under the **GNU General Public License v3.0 (GPL-3.0-only)**.

You are free to use, study, modify, and redistribute BCS Tool under the terms of the GPLv3. If you distribute a modified version or other derivative work covered by the GPL, you must provide the corresponding source code under the same license terms.

See [`LICENSE`](LICENSE) for the full license text.

## Disclaimer

BCS Tool is an independent community utility and is not an official TaleWorlds product.

Mount \& Blade II: Bannerlord and related names belong to their respective owners.

