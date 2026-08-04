# BCS Tool

BCS Tool is a Windows desktop application for managing a **Bannerlord Coop dedicated server**.

It provides a graphical interface for starting, stopping, saving, restarting, monitoring, and configuring the server without requiring a separate command-line management workflow.

> \*\*Current release:\*\* v0.1.1

## Features

* Manual server restart
* Last second save before restart to avoid rollback
* Automatic scheduled restart
* Restart warning message broadcast
* Automatic crash recovery
* Built-in interactable BCS terminal
* Automatic server executable detection
* Server configuration modification
* Mod configuration modification

## Requirements

* Windows 10 or Windows 11, 64-bit
* Mount \& Blade II: Bannerlord
* Bannerlord Coop / Bannerlord Coop dedicated server
* Steam installation is supported for automatic server detection

The published release is self-contained, so users do not need to install the .NET runtime separately.

## Download

Download the latest compiled version from the repository's **Releases** page.

For normal use, download:

```text
BCS Tool.exe
```

The files automatically provided by GitHub as `Source code (zip)` and `Source code (tar.gz)` contain the project source rather than the compiled application.

## Getting Started

1. Launch **BCS Tool.exe**.
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

## Automatic Restarts

BCS Tool can periodically restart the server at a configured interval and minute.

Before a scheduled restart, it can:

1. Broadcast countdown warnings
2. Save the campaign
3. Stop the server gracefully
4. Wait for shutdown
5. Restart the server

Crash recovery can also restart the server automatically if the managed process exits unexpectedly.

## Server Console

The Server Console is backed by a Windows pseudoconsole (ConPTY), allowing BCS Tool to display the server's native terminal UI inside the application.

Supported behavior includes:

* ANSI terminal colors
* Native command editing
* Tab completion
* Shift+Tab completion
* Arrow keys
* Home / End
* Delete / Backspace
* Escape
* Clipboard paste with Ctrl+V

Diagnostic logs are stored under:

```text
%LOCALAPPDATA%\\BCSServerTool\\Logs
```

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
publish\\BCS Tool.exe
```

The publish configuration is:

* Release
* `win-x64`
* Self-contained
* Single-file executable

## Versioning

BCS Tool uses semantic-style version numbers:

```text
0.1.0
```

The application version is defined in:

```text
BCSTool.csproj
```

For example:

```xml
<Version>0.1.0</Version>
```

The UI reads the compiled application version at runtime, so the project version is the single source of truth for release numbering.

GitHub release tags should use the corresponding `v` prefix:

```text
v0.1.0
v0.1.1
v0.2.0
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

