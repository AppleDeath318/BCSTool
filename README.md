# BCS Tool v1.8.11

Current release: **v1.8.11**

The Visual Studio project and C# root namespace have been renamed from the redundant `BCSServerTool` naming to `BCSTool`. See `V1_8_11_PROJECT_NAMESPACE_RENAME.md`.

Open **`BCSTool.sln`** in Visual Studio.

---

# BCS Tool v1.8.10

Current release: **v1.8.10**

Native state values no longer include decorative vertical bars, and a clean `|` divider is restored in the visible Server Console footer. See `V1_8_10_FOOTER_BAR_FIX.md`.

---

# BCS Tool v1.8.9

Current release: **v1.8.9**

The native Bannerlord footer status is now reflected in the main Server state field while remaining hidden from the Server Console footer. See `V1_8_9_NATIVE_SERVER_STATE.md`.

---

# BCS Tool v1.8.8

Current release: **v1.8.8**

The built application is now named **BCS Tool.exe**. Missing server/mod configuration files now prompt the user to start the server once so Bannerlord Coop can generate them. See `V1_8_8_APP_NAME_CONFIG_PROMPT.md`.

---

# BCS Tool v1.8.7

Current release: **v1.8.7**

This release adds Steam Workshop detection for `BannerlordCoopServer.exe`. See `V1_8_7_WORKSHOP_AUTO_DETECT.md`.

---

# BCS Tool v1.8.6

Current release: **v1.8.6**

v1.8.6 adds automatic `BannerlordCoopServer.exe` detection. See `V1_8_6_AUTO_DETECT_SERVER.md`.

---

# BCS Tool v1.8.2

Current release: **v1.8.2**

This release refines the main-window settings layout and both configuration
editors. See `V1_8_2_UI_REFINEMENT.md`.

---

# BCS Tool v1.6 — Fully Commented Learning Edition

> This edition contains extensive explanatory comments throughout the C# and XAML source. > See `CODE_WALKTHROUGH.md` for a recommended reading order and architecture explanation.

A Windows WPF application for managing `BannerlordCoopServer.exe`.

## What v1.0 implements

- Owns the server process directly from C#.
- Redirects server stdin/stdout into the BCS Tool UI.
- No `SendKeys` and no separate CMD console are required.
- Detects server readiness from:
  `coop server up, waiting for clients`
- Clock-aligned scheduled restarts.
- Configurable:
  - restart interval
  - restart minute
  - warning lead time
- Broadcasts a warning every minute before restart.
- Sends `save` before restart.
- Sends `stop` for graceful shutdown.
- Automatic crash recovery.
- Managed Windows Job Object for cleaning child/orphan processes from the server instance.
- Optional port guard (disabled by default until the server's real exclusive port is known).
- Single-instance guard for BCS Tool.
- Persistent Windows Registry settings under `HKEY_CURRENT_USER\Software\BCSServerTool`.
- Manual Start / Save / Restart / Stop.
- Manual server-console command entry.
- Daily diagnostic logs under:
  `%LOCALAPPDATA%\BCSServerTool\Logs`

## Development environment

Recommended:

- Windows 10/11
- Visual Studio 2026 Community
- `.NET desktop development` workload
- .NET 10 SDK

Open:

`BCSServerTool.sln`

Then press **F5**.

## First run while developing

The app looks for `BannerlordCoopServer.exe` next to the built application by default.

When running from Visual Studio, use **Browse...** in BCS Tool to select the real
`BannerlordCoopServer.exe`, then click **Save Settings**.

This writes the server directory into `settings.json`.

## Scheduling semantics

Example:

- `RestartEveryHours = 2`
- `RestartMinute = 55`

Restart times are:

- 00:55
- 02:55
- 04:55
- 06:55
- ...

The next restart is calculated only after the server reports ready. If the
server becomes ready after a scheduled restart time, that missed restart is
not replayed.

## Building a normal executable

In Visual Studio:

1. Select `Release`.
2. Build -> Build Solution.
3. The output is under:
   `BCSServerTool\bin\Release\net10.0-windows\`

## Publishing a self-contained single EXE

Open PowerShell in the `BCSServerTool` project folder and run:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\Publish-SingleExe.ps1
```

The publish output is placed in:

`BCSServerTool\publish\`

The intended deployment is:

```text
Your Server Folder\
    ServerTool.exe
    settings.json
    BannerlordCoopServer.exe
    ...
```

## Important v1.0 behavior

BCS Tool owns the server's redirected stdin/stdout. Therefore the server output
is displayed inside BCS Tool instead of a separate server console window.

Closing BCS Tool while the server is running prompts you to save and stop the
server gracefully first.

## Suggested v1.1+ work

- System tray mode
- Configurable broadcast message templates in the UI
- Player count / player list if the server exposes it
- Multiple server profiles
- Start-with-Windows
- Better structured logging
- Update mechanism
- Optional remote admin/API




## Settings storage

BCS Tool no longer uses `settings.json`.

When **Save Settings** is pressed, values are stored under:

```text
HKEY_CURRENT_USER\Software\BCSServerTool
```

Because this is under `HKEY_CURRENT_USER`:

- administrator permission is not required;
- settings belong to the current Windows user;
- replacing `ServerTool.exe` with a newer build keeps the old settings;
- deployment can remain a single visible `ServerTool.exe`.

The **Reset to Defaults** button deletes this Registry key and restores the
defaults from `ServerSettings.cs`.

## Deployment

The intended deployment can now be:

```text
Bannerlord Server\
    ServerTool.exe
    BannerlordCoopServer.exe
    ...
```

If `BannerlordCoopServer.exe` is not beside `ServerTool.exe`, use **Browse...**
once and then press **Save Settings**. The selected path will be remembered in
the Registry.

## Default port guard

`ServerPort` defaults to `0`, which means the optional port guard is disabled.

The application still has:

- duplicate Bannerlord process detection;
- a single-instance guard for BCS Tool;
- Windows Job Object cleanup for child/orphan processes.

Only configure a non-zero `ServerPort` if the server's actual exclusive
listening port has been confirmed.


## v1.1 additions

### Colored server-state indicator

The Server State area now contains a status dot:

- Grey: stopped/offline
- Yellow: starting, loading, saving, stopping, or restarting
- Green: online/ready
- Red: crashed, error, or port-blocked

### Players panel

A dedicated Players panel now appears beside the server console.

BCS Tool can always recover the player count when the normal server pulse
contains:

```text
players=N
```

The original Bannerlord Coop terminal also has a visual player-roster pane.
That pane is terminal UI, while the current v1.1 process manager consumes
line-oriented redirected stdout.

`PlayerRosterTracker` therefore also attempts to recover the textual roster if
those terminal rows are present in redirected output.

If the count is available but the detailed character/state rows are not, the
Players panel explicitly shows that detailed rows were not exposed.

A future version can replace standard redirected output with Windows ConPTY to
host the full terminal screen and reproduce the native roster more faithfully.


## v1.1.1 orphan-process fix

The server is now assigned to a Windows Job Object configured with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`.

If BCS Tool itself is terminated unexpectedly (including Visual Studio
**Stop Debugging**), Windows cleans up the managed Bannerlord process tree so a
hidden old server should not block the next launch.


## v1.2 — Windows ConPTY terminal hosting

v1.2 replaces normal redirected stdout/stdin with Windows ConPTY.

The server now runs inside a Windows pseudo console:

```text
BannerlordCoopServer.exe
          ↕
      Windows ConPTY
          ↕
   BCS Tool terminal parser
          ↓
┌──────────────────────┬─────────────────┐
│ Log pane             │ Players pane    │
└──────────────────────┴─────────────────┘
```

BCS Tool contains a small VT/ANSI screen emulator that understands the common
cursor movement, erase, scrolling, and screen-control sequences used by
terminal applications.

The native `Players (N)` pane is now the primary source for player count and
roster rows.

The `players=N` pulse remains a fallback. It is useful because testing showed
that the pulse eventually changes after a disconnect, although it may lag by
roughly 10-20 seconds.

### New files

- `Services/ConPtySession.cs`
  - low-level Windows ConPTY process hosting
- `Services/VirtualTerminalScreen.cs`
  - VT/ANSI two-dimensional screen reconstruction
- `Services/TerminalScreenUpdatedEventArgs.cs`
  - immutable screen snapshot event
- `Services/PlayerRosterTracker.cs`
  - native Players-pane parser

### Windows requirement

ConPTY requires Windows 10 version 1809 or newer.


## v1.2.1 — stable native player roster

The ConPTY Players pane is now sticky across intermediate terminal redraw
frames. Once a valid native roster is detected, temporary snapshots that omit
the Players header no longer replace it with pulse-only fallback text.

This removes the visible switching between:

```text
1 player(s) online
(waiting for ...)
```

and the actual native player state.


## v1.3 — full native terminal

The separate BCS Tool Players panel has been removed.

The Server Console now displays the entire reconstructed ConPTY screen,
including Bannerlord Coop's own native `Players (N)` pane.

BCS Tool no longer slices the right side of the terminal or substitutes its own
player visualization.


## v1.4 — adaptive terminal geometry

The Windows ConPTY terminal now follows the actual visible size of the WPF
Server Console.

BCS Tool measures the console viewport using the current Consolas font and
Windows DPI scale, converts it into columns/rows, and calls
`ResizePseudoConsole` whenever the viewport changes.

The embedded console's own horizontal/vertical scrollbars are disabled: the
terminal geometry itself adapts to the available space.


## v1.4.1 — stable native terminal header

The virtual terminal now implements proper deferred VT autowrap instead of
immediately scrolling after the final column.

Terminal snapshots are also coalesced around redraw bursts, and once the native
`Log ... Players (N)` header has been observed, transient half-redrawn frames
cannot replace the last complete screen.

This prevents the top native terminal border/header from intermittently
disappearing while the server updates.


## v1.5 — ANSI terminal colors

BCS Tool now preserves SGR foreground colors from the ConPTY stream and renders
them in the embedded Server Console.

Supported color forms include standard 16-color ANSI, 256-color indexed
foregrounds, and 24-bit RGB foregrounds.

The terminal uses a Windows-Terminal-like palette on a black background to
closely resemble the native Bannerlord server console.


## v1.6 — separate BCS Tool console and clearer restart settings

### Console layout

The application now displays:

```text
Server Console
    Live Bannerlord ANSI/VT ConPTY screen

BCS Tool Console
    [BCS Tool] scheduling / restart / save / recovery information

Command input                                            Send
```

The **BCS Tool Console** restores the tool messages that used to share the old
line-oriented console, while keeping the native ConPTY terminal untouched.

### Clear button

The Server Controls button is now **Clear BCS Tool Console**.

It clears only the visible BCS Tool Console. It does not clear or modify the
Bannerlord Server Console.

### Restart settings

The UI now clearly shows:

```text
Restart every (hours)                       1..24
Restart at minute                           0..59
Restart countdown warning (minutes before) 0..10
```

Older saved warning values above 10 are clamped to 10 when loaded.

### Manual first start

Opening BCS Tool never launches the server automatically. The user must press
**Start** for the first launch.

Scheduled restarts and optional automatic crash recovery continue to operate
after BCS Tool has launched the server.


## v1.7 — native ConPTY autocomplete and command-line editing

The BCS Tool command box is now a live proxy for Bannerlord's own native
console line editor instead of an independent WPF command field.

Supported terminal input includes:

```text
Text         native typing
Tab          native autocomplete
Shift+Tab    reverse-tab sequence
Up / Down    native command history
Left / Right native cursor movement
Home / End   native line navigation
Backspace
Delete
Escape
Enter
Ctrl+V       terminal paste
```

BCS Tool does not maintain a hard-coded list of Bannerlord/Coop commands.

Typing:

```text
st
```

then pressing Tab sends the real Tab key through ConPTY. Bannerlord can render
its own suggestions such as:

```text
status    stop
```

or complete the input when only one match exists.

BCS Tool then reads the native bottom `> ...` prompt from the reconstructed
terminal and synchronizes that text and cursor position back into the WPF
command input.

The Send button is equivalent to Enter: it sends only carriage return because
the native terminal already owns the current command line.


## v1.7.1 — cleaner native footer

BCS Tool now applies a display-only filter to Bannerlord's native bottom
terminal footer.

Native ConPTY content such as:

```text
F10 Stop | SERVING · save saveauto1 · port 4200 · players 0 · up 0:02:58
```

is displayed as:

```text
save saveauto1 · port 4200 · players 0
```

Hidden fields:

- `F10 Stop`
- `SERVING`
- `up H:MM:SS`

Retained fields:

- save name
- port
- player count

The underlying ConPTY screen is unchanged. Native autocomplete, prompt
synchronization, command history, and cursor handling continue to use the
unfiltered terminal state.


## v1.7.2 — robust footer filtering

The display filter now uses semantic field boundaries instead of exact
separator characters.

Native:

```text
F10 Stop | SERVING · save saveauto1 · port 4200 · players 0 · up 0:02:58
```

Displayed:

```text
save saveauto1 · port 4200 · players 0
```

BCS Tool finds `save `, keeps the styled text from that point onward, and cuts
the final `up H:MM:SS` field using TimeSpan parsing. The underlying ConPTY
screen is unchanged.
