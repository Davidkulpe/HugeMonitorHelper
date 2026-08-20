# HugeMonitorHelper (BorderDock)

A personal Windows utility for working across many projects on one very large
screen. Each window you care about gets a thick colored border, a title chip, and
a matching taskbar button, so seven near-identical Claude Code terminals stop
looking like seven near-identical Claude Code terminals.

Click a border to summon that window to a center slot; the one that was centered
folds back to where you'd placed it.

```powershell
cd BorderDock.Spike
dotnet run -c Debug
```

Closing the window hides to tray. **Quit** from the tray menu really exits — and
is the only path that saves window positions and hands back the titles and icons
it borrowed, so prefer it over killing the process.

## What it does

- **Colored border + title chip** around each managed window, z-ordered directly
  above that window rather than globally topmost, so anything covering the window
  covers its border too.
- **Taskbar identity** — a color swatch icon with the project's initials plus the
  label prefixed onto the title, so a collapsed window is still identifiable.
- **Center slot** — click a border or chip to summon; exactly one window is
  centered at a time and the previous one folds home.
- **Persistence** — rules survive restarts; a matching window that reappears is
  re-bordered and moved back to its saved position.
- **Attention pulse** — a terminal whose text goes quiet after being busy (Claude
  finished and is waiting on you) strobes its border until you look at it.
- **Launch project** — opens `claude -c` in its own conhost console at a saved
  project path. See "Why working directories" below for why this exists.
- **Hand gestures** (optional, off by default) — point to move focus, open palm to
  center, fist to fold home, swipe to cycle. Needs the Python sidecar.

Right-click a title chip for per-window settings: label, color, font size, keep
on top, taskbar tagging, or remove.

## Why working directories, not window titles

Rules are keyed by the **working directory** of the terminal behind the window,
not its title. A Claude Code terminal's title is Claude's live task text and
mutates constantly, so it is useless as an identity.

`ProcessInfo` finds the shell or node process behind an HWND and reads its current
directory straight out of the PEB (`NtQueryInformationProcess` →
`RTL_USER_PROCESS_PARAMETERS.CurrentDirectory`), the way Process Explorer does.

This only works when one window maps to one project. **Windows Terminal hosts every
tab in a single process**, so a window there cannot be attributed to a project at
all — `ProcessInfo` detects shared hosts and refuses to guess. That is why the
tray's *Launch project* opens a standalone `conhost` console instead: one process,
one window, one project, attachable automatically.

Windows with no shell behind them (VLC, browsers) fall back to title matching.

## Layout

| Path | What |
|---|---|
| `BorderDock.Spike/BorderManager.cs` | Core: WinEvent hook, center-slot swap, attention pulse, idle detection |
| `BorderDock.Spike/ProcessInfo.cs` | HWND → project directory, via the process tree and PEB |
| `BorderDock.Spike/TaskbarTag.cs` | Taskbar button icon + label |
| `BorderDock.Spike/FourEdgeOverlay.cs` | Border as four edge windows |
| `BorderDock.Spike/RegionOverlay.cs` | Border as one region-clipped window |
| `BorderDock.Spike/UiaText.cs` | Reads terminal text via UI Automation to detect idleness |
| `BorderDock.Spike/SpikeForm.cs` | Main window, tray, drag-to-pick |
| `gesture-spike/` | Headless Python/MediaPipe gesture sidecar |

Two border implementations sit behind `IBorderOverlay` and swap at runtime — four
edge windows versus one region-clipped window — because which one tracks a fast
drag better is an empirical question. Toggle it from the main window.

Config lives at `%APPDATA%\BorderDock\config.json` (tray → *Open config file*).

## Gesture sidecar

Optional. Vision runs in Python; BorderDock just reads gesture names off stdout.

```powershell
cd gesture-spike
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
```

The venv is not committed (~300MB). BorderDock finds it by walking up from the exe
and falls back to system `python`.

## Known rough edges

- The taskbar label is a running fight: Claude rewrites the console title every
  turn and BorderDock re-applies its prefix on `EVENT_OBJECT_NAMECHANGE`. Expect
  brief flicker on the button.
- Taskbar labels only show at all if the taskbar is set to never combine. With
  "always combine" the color swatch is the only remaining signal.
- If BorderDock crashes rather than quitting, windows keep a prefixed title and a
  dangling icon handle until they next set their own.
- Elevated windows can't be read or moved from a non-elevated BorderDock.
