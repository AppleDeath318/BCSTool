# BCS Tool

BCS Tool is a Windows desktop application for managing a **Bannerlord Coop dedicated server**.

It provides a graphical interface for starting, stopping, saving, restarting, monitoring, and configuring the server without requiring a separate command-line management workflow.

**Current release:** v0.3.3.2

**Author:** [Apar](https://github.com/AppleDeath318)

## Features

* Manual server restart
* Automatic scheduled restart
* Restart warning message broadcast
* Automatic crash recovery
* Built-in live server log console with local command input
* SteamID64 banlist and whitelist access control (Beta)
* Automatic server executable detection
* Server configuration modification
* Mod configuration modification
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
BCS.Tool.v<version>.exe
```

The files automatically provided by GitHub as `Source code (zip)` and `Source code (tar.gz)` contain the project source rather than the compiled application.

GitHub release assets use periods in the filename. After an in-app update, BCS
Tool installs the executable locally with the friendlier `BCS Tool
v<version>.exe` filename.

## Updates

BCS Tool checks the latest stable GitHub release once when the application
opens. It does not repeatedly check while it remains running.

The version number in the bottom-right corner opens **About BCS Tool**. When an
update is available, the bottom-right version link also displays the available
version.

Installing an update is always user initiated and requires the managed server
to be stopped. BCS Tool downloads both the versioned executable and its
`.sha256` release asset, verifies the executable, closes, replaces the old
version, and reopens. The About window also includes a link to this GitHub
repository.

## Appearance

Use the **Theme** selector in the bottom-right footer to switch between Light
and Dark mode. The selected theme is saved under the normal BCS Tool Registry 
settings and restored on the next launch.

## Getting Started

1. Launch the versioned executable, for example **BCS Tool vX.Y.Z.exe**.
2. BCS Tool will try to locate `BannerlordCoopServer.exe` automatically.
3. If the executable is not detected, use **Browse** to select it manually.
4. Press **Start** to launch the server.

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

## Player Access Control (Beta)

> **Beta warning:** The banlist and whitelist feature has not received extensive
> testing. Monitor the BCS Tool Console while using it and verify its behavior
> with trusted players.

Player access control uses **SteamID64 for authentication**. Banlist is the
default mode. Select Banlist or Whitelist from the **Access Control (Beta)**
section in the main window:

* **Banlist:** Players whose resolved SteamID64 appears in the banlist are kicked.
* **Whitelist:** Players whose resolved SteamID64 does not appear in the whitelist are kicked.

Enforcement is performed by BCS Tool and requires it to be running, managing
the server, and receiving the server log and identity events.

Use **Banlist/Whitelist Panel** to add or remove 17-digit SteamID64 entries and
optional notes. The panel automatically opens the tab for the currently active
mode. Press **Apply** or **Apply & Close** to save manual list changes. Every
applied change is recorded separately in the BCS Tool Console and log with its
action, character name, Steam ID, and note.

Use **Player List** to view every character name and Steam ID learned in
`player-identities.json`. Search by character name or Steam ID, and right-click
any row to copy its SteamID. Each player can be banned, unbanned, whitelisted,
or unwhitelisted. List actions save immediately; buttons are disabled when the
requested list state is already active.

A player whose identity is still unresolved, including someone creating a 
character, remains **Pending** until the current server session confirms the 
character, Hero ID, and SteamID64 mapping. Denied players and the rule that 
caused each kick are recorded in the BCS Tool Console.

The access lists and learned identity data are stored under:

```text
%LOCALAPPDATA%\BCS Tool
```

The main files are `banlist.json`, `whitelist.json`, and
`player-identities.json`.

## Native Save Backups

Bannerlord Coop maintains its own per-world backup history beside the active
campaign save. BCS Tool does not create, rotate, or delete these backups.

The previous **Save Backups** section, retention setting, and **Open Backup
Folder** button have been removed. Existing files under `Game Saves\BCS
Backups` are legacy files from older BCS Tool versions and are no longer read,
written, or deleted by the application.

A Bannerlord Coop save consists of two companion files with the same base name:

```text
saveauto1.sav
saveauto1.json
```

Bannerlord Coop's native generations for a save named `saveauto1` are named:

```text
saveauto1.backup1.sav
saveauto1.backup1.json

saveauto1.backup2.sav
saveauto1.backup2.json
```

`backup1` is the newest native backup. Higher numbers are progressively older.

The active save remains unchanged in name:

```text
saveauto1.sav
saveauto1.json
```

Both the active save and native backup pairs are stored under:

```text
Documents\Mount and Blade II Bannerlord\CoopData\DedicatedServer\Game Saves
```

### Manual backup restore

The **Load Backup** button under **Server Configuration → World / Saving**
opens the available complete native backup generations and their modification
dates. Incomplete generations are not offered.

A backup can only be loaded while the managed server is **fully stopped**. If the server is starting, running, saving, stopping, restarting, or otherwise not in the normal `Stopped` state, BCS Tool will require the server to be stopped before continuing.

Selecting a backup and pressing **Apply** replaces the current save pair.

For example, loading:

```text
saveauto1.backup2
```

restores:

```text
saveauto1.backup2.sav  -> saveauto1.sav
saveauto1.backup2.json -> saveauto1.json
```

The native backup files themselves remain unchanged after being loaded.

BCS Tool stages the selected pair and temporarily preserves the current active pair while applying the restore to reduce the chance of an ordinary file-copy failure leaving the `.sav` and `.json` files mismatched.

> **Note:** Bannerlord Coop owns backup generation. BCS Tool provides only the
> manual loader and does not perform automatic corruption detection or rollback.

## Automatic Restarts

BCS Tool can periodically restart the server at a configured interval and minute.

Before a scheduled restart, it can:

1. Broadcast countdown warnings
2. Stop the server gracefully (the native `stop` command saves the campaign before exit)
3. Wait for shutdown
4. Restart the server

Crash recovery can also restart the server automatically if the managed process exits unexpectedly. It does not create a separate BCS Tool crash backup; Bannerlord Coop's native backups remain available for manual loading.

Scheduled automation becomes active only after the dedicated server's structured runtime state reports `SERVING`.

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
0.3.3.2
```

The application version is defined in:

```text
BCSTool.csproj
```

For example:

```xml
<Version>0.3.3.2</Version>
```

The UI reads the compiled application version at runtime, so the project version is the single source of truth for release numbering.

GitHub release tags should use the corresponding `v` prefix:

```text
v0.3.3.2
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
