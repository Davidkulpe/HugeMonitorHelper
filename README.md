# WAuraDows

**Tell your windows apart on a very large monitor.**

![platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0078D4)
![.NET](https://img.shields.io/badge/.NET-8.0--windows-512BD4)
![license](https://img.shields.io/badge/license-MIT-green)

On a 40" 4K panel you can keep a dozen windows open at once. The problem isn't
space — it's identity. Seven terminal windows running seven different projects
look *identical*, in the taskbar and on screen. You end up reading titles to work
out which one is which, over and over, all day.

WAuraDows gives each window you care about a **thick colored border**, a
**name tag**, and a **matching taskbar button**, so you recognise it by color
instead of by reading. Click a border to pull that window to the center of the
screen at a comfortable size; the one that was there folds back to where you'd
put it.

It was built for juggling many **Claude Code / AI coding sessions** at once, but
nothing about it is AI-specific — it works on any window.

---

## What you get

| | |
|---|---|
| 🎨 **Colored border + name chip** | Around each managed window. Sits directly above *that* window in the z-order, not globally on top, so anything covering the window covers its border too. |
| 🔖 **Taskbar identity** | A color swatch icon with the project's initials, plus the name prefixed onto the button label. A collapsed window is still identifiable. |
| 🎯 **Center slot** | Click a border or chip to summon a window to center. Exactly one window is centered at a time; the previous one folds home automatically. |
| 💾 **Survives restarts** | Rules persist. When a matching window reappears it gets re-bordered and moved back to its saved position. |
| 🔔 **Attention strobe** | A terminal whose output goes quiet after being busy — your AI session finished and is waiting on you — strobes its border until you look at it. |
| 🚀 **Launch project** | Opens a session in its own console at a saved project path, from the tray. |
| ✋ **Hand gestures** *(optional)* | Point to move focus, open palm to center, fist to fold home, swipe to cycle. Webcam + Python sidecar. |

Right-click any name chip for per-window settings: label, color, font size, keep
on top, taskbar tagging, or remove.

## Install

Needs Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/Davidkulpe/WAuraDows.git
cd WAuraDows/WAuraDows
dotnet run -c Debug
```

Tray menu → **Start with Windows** to make it permanent.

## Using it

1. **Add a window** — drag the 🎯 target from the main panel onto any window and
   release. It gets a border, a chip, and a taskbar tag. Colors cycle as you add.
2. **Summon** — click a window's border or its name chip. It moves to the center
   slot and comes to the front, even from minimized. Click again to fold it home.
3. **Rename / recolor** — right-click a name chip.
4. **Resize the center slot** — summon a window, resize it, and that becomes the
   remembered center size for everything.

Closing the main window hides to tray. **Quit from the tray menu is the real
exit** — it's the only path that saves window positions and hands back the titles
and icons the app borrowed, so prefer it over killing the process.

Config lives at `%APPDATA%\WAuraDows\config.json` (tray → *Open config file*).

## How it identifies a window

This is the part that took the most work, and it's worth explaining because it
determines what the tool can and can't do.

**Rules are keyed by the working directory of the process behind the window, not
by the window title.** A terminal running an AI coding agent has a title that is
the agent's live task text — it changes every few seconds. As an identity, it's
worthless.

So `ProcessInfo.cs` walks the process tree behind a window handle, finds the
shell or `node` process, and reads its current directory **straight out of the
PEB** (`NtQueryInformationProcess` → `RTL_USER_PROCESS_PARAMETERS.CurrentDirectory`)
— the technique Process Explorer uses. That directory is stable for the life of
the session, so it makes a durable key.

**The catch: Windows Terminal hosts every tab in a single process.** One process,
many windows, many projects — a window there simply cannot be attributed to a
project. The code detects shared hosts and refuses to guess rather than
mislabelling. That's why the tray's *Launch project* opens a standalone `conhost`
console: one process, one window, one project, attachable automatically.

Windows with no shell behind them (VLC, browsers) fall back to title matching.

## How the taskbar tagging works

No Windows API lets one process restyle another process's taskbar button —
`ITaskbarList3::SetOverlayIcon` is own-process only. So the app pokes the target
window itself: `WM_SETICON` with a generated color swatch, and `WM_SETTEXT` to
prefix the label onto the title. Both go through `SendMessageTimeout` with
`SMTO_ABORTIFHUNG`, because a wedged terminal would otherwise hang the app.

Since an AI agent rewrites its console title constantly, the prefix is
re-applied on every `EVENT_OBJECT_NAMECHANGE`.

## Project layout

| Path | What |
|---|---|
| `WAuraDows/BorderManager.cs` | Core: WinEvent hook, center-slot swap, attention pulse, idle detection |
| `WAuraDows/ProcessInfo.cs` | Window handle → project directory, via the process tree and PEB |
| `WAuraDows/TaskbarTag.cs` | Taskbar button icon + label |
| `WAuraDows/FourEdgeOverlay.cs` | Border as four thin edge windows |
| `WAuraDows/RegionOverlay.cs` | Border as one region-clipped window |
| `WAuraDows/UiaText.cs` | Reads terminal text via UI Automation to detect idleness |
| `WAuraDows/MainForm.cs` | Main panel, tray, drag-to-pick |
| `gesture-sidecar/` | Headless Python + MediaPipe gesture sidecar |

Two border implementations sit behind `IBorderOverlay` and swap at runtime — four
edge windows versus one region-clipped window — because which tracks a fast drag
better is an empirical question. Toggle from the main panel and see for yourself.

> **On the name:** *Windows* + *aura* — every window gets its own colored glow.
> Earlier revisions called it BorderDock; the app migrates a pre-rename config
> and autostart entry across automatically on first run.

## Gesture sidecar (optional)

Vision runs in Python; the app just reads gesture names from the sidecar's stdout.

```powershell
cd gesture-sidecar
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
```

Then tray → **Enable hand gestures**. The venv isn't committed (~300 MB); the app
finds it by walking up from the executable and falls back to system `python`.

## Known limitations

- **Taskbar labels only appear if your taskbar is set to never combine buttons.**
  With "always combine", the color swatch is the only remaining signal.
- The taskbar label is a running fight against whatever keeps rewriting the
  window title. Expect brief flicker on the button.
- If the app crashes rather than quitting cleanly, windows keep a prefixed title
  and a stale icon until they next set their own.
- Elevated windows can't be read or moved by a non-elevated instance.
- Single monitor per window for the center slot — it centers on the monitor the
  window is currently on.
- Windows-only, and deliberately so: it's built on Win32 window hooks throughout.

## Contributing

Issues and PRs welcome. The code is heavily commented, especially the parts where
Windows behaves unintuitively — those comments explain *why*, and are worth
reading before changing that code.

## License

MIT — see [LICENSE](LICENSE).
