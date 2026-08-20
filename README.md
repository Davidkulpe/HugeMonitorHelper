# BorderDock

A personal Windows utility: give specific windows a colored, clickable border so
you can tell them apart on a big screen, and click the border (or a hotkey) to
summon a window to center at a preset size.

Full design + reviews:
(local design notes, not committed)

Matching is by **window title** (editable pattern), not exe — so many same-exe
windows (multiple Windows Terminals) each get their own border.

## BorderDock.Spike — the de-risk spike (build this first)

A throwaway WinForms app that proves the three risky Win32 bits before the real
app is built. Run it, then work the checklist below.

```powershell
cd BorderDock.Spike
dotnet run -c Debug
```

### The interaction to feel

1. Open 2-3 windows (e.g. a couple of Notepads + a browser) and **place them where
   you want** on the screen.
2. For each: click **Add window (3s)**, then focus that window within 3 seconds.
   It gets a **thick colored border** (color cycles per add) and a **title chip
   top-left**. Add 2-3 so they have different colors.
3. **Click a window's border** → that window jumps to **center and grows to 2×**;
   whichever window was centered before **folds back to where you'd placed it** (and
   its original size). Only one window is centered at a time.
4. **Click the centered window's border again** → it folds back home too (toggle).
5. Clicking the **title chip** summons too.

### What I'm checking with you (the kill-tests)

- **Foreground steal:** when a window goes to center, does it actually come to the
  front? (Test it against a window that was behind others, and a **minimized** one —
  minimize a managed window, then click its title chip → should restore + center.)
- **Tracking / trail:** drag a managed window fast → border stays glued (~5px)?
  Click **Render: ... (switch)** to compare **four edge windows** vs **single region
  window** across all managed windows. Which tears less / clicks better?
- **No feedback storm:** the readout's `excluded (own border)` climbs while the app
  stays responsive = the own-HWND guard works.
- **Cleanup:** close a managed window → its border + chip vanish (no orphans)?

### Report back

Did summon bring windows to front (incl. minimized)? Did the 2x-center + fold-back
swap feel right? Which render mode tracked cleaner? Any flicker, orphan borders, or
chip-position weirdness? That decides what real v1 locks in.
