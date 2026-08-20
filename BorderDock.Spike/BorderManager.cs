namespace BorderDock.Spike;

/// <summary>
/// The core of BorderDock: tracks many managed windows with one out-of-context
/// WinEvent hook, draws their borders + title chips (z-ordered above each own
/// window, not global topmost), runs the single center-slot swap, and persists
/// everything as title-keyed rules so borders + positions survive a restart.
/// </summary>
internal sealed class BorderManager : IDisposable
{
    private static readonly Color[] Palette =
    {
        Color.FromArgb(0, 158, 115),   // green
        Color.FromArgb(0, 114, 178),   // blue
        Color.FromArgb(230, 159, 0),   // orange
        Color.FromArgb(86, 180, 233),  // sky
        Color.FromArgb(213, 94, 0),    // vermillion
        Color.FromArgb(204, 121, 167), // purple
    };

    public const int BorderWidth = 10;

    private readonly Config _config;
    private readonly List<ManagedWindow> _windows = new();
    private readonly Dictionary<IntPtr, ManagedWindow> _byHwnd = new();
    private readonly Dictionary<ManagedWindow, Config.Rule> _ruleOf = new();
    private readonly HashSet<IntPtr> _excluded = new();
    private readonly Native.WinEventDelegate _proc;

    private IntPtr _hookObjects, _hookSystem;
    private bool _useRegion;
    private bool _inUpdate;
    private bool _paused;
    private int _colorIndex;
    private ManagedWindow? _centered;
    private ManagedWindow? _active;   // last-focused managed window (gesture target)
    private Size? _centerSize;

    // Calm attention pulse: windows that went quiet (Claude waiting) while unfocused.
    private readonly HashSet<ManagedWindow> _attention = new();
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 50 };

    // Idle detection: read terminal text via UIA, pulse when it stops changing.
    private readonly UiaText _uia = new();
    private readonly System.Windows.Forms.Timer _idle = new() { Interval = 5000 };
    private const long QuietMs = 30000;    // on-screen text static this long after activity → "waiting"
                                           // (Claude shows a ticking timer while busy, so 30s still = truly idle)
    public int IdlePulses;
    public string LastIdle = "(none)";

    public long TotalLocationEvents, ActedEvents, ExcludedEvents;
    public int Count => _windows.Count;
    public bool UseRegion => _useRegion;
    public bool Paused => _paused;
    public string CenteredTitle => _centered?.Title ?? "(none)";
    public string CenterSizeText => _centerSize is { } s ? $"{s.Width}x{s.Height}" : "(half screen, default)";
    public bool IsOurs(IntPtr h) => _excluded.Contains(h) || _byHwnd.ContainsKey(h);

    public BorderManager(Config config)
    {
        _config = config;
        _centerSize = config.CenterSize;
        _proc = WinEventProc;
        // Range runs to NAMECHANGE (0x800C), one past LOCATIONCHANGE, because the
        // taskbar label has to be re-applied every time Claude rewrites the title.
        _hookObjects = Native.SetWinEventHook(
            Native.EVENT_OBJECT_CREATE, Native.EVENT_OBJECT_NAMECHANGE,
            IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        _hookSystem = Native.SetWinEventHook(
            Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        _pulse.Tick += (_, _) => PulseTick();
        _pulse.Start();
        _idle.Tick += (_, _) => IdleTick();
        _idle.Start();
    }

    /// <summary>Poll each managed terminal's text. A window that was changing (Claude
    /// working / spinner) and then goes static for QuietMs while unfocused = "waiting"
    /// → pulse. Resumed activity or focusing it clears the pulse.</summary>
    private void IdleTick()
    {
        if (_windows.Count == 0) return;
        long now = Environment.TickCount64;
        foreach (var mw in _windows)
        {
            var sig = _uia.Snapshot(mw.Hwnd);
            if (sig is null) continue;                 // not readable via UIA → skip

            if (mw.LastTextSig is null) { mw.LastTextSig = sig; mw.LastChangeMs = now; continue; }

            if (sig != mw.LastTextSig)
            {
                mw.LastTextSig = sig;
                mw.LastChangeMs = now;
                mw.WasActive = true;                   // it's working again
                ClearAttention(mw);                    // so not "waiting" anymore
            }
            else if (mw.WasActive && now - mw.LastChangeMs >= QuietMs)
            {
                mw.WasActive = false;                  // pulse once per work→wait transition
                if (Native.GetForegroundWindow() != mw.Hwnd)
                {
                    _attention.Add(mw);
                    IdlePulses++;
                    string t = Native.WindowTitle(mw.Hwnd);
                    LastIdle = t.Length > 40 ? t[..40] : t;
                }
            }
        }
    }

    // ---- attention pulse (Claude bell while unfocused → calm breathing) -------

    // Diagnostics so we can see, live, whether attention flashes are arriving.
    public int FlashCount;
    public string LastFlash = "(none)";

    /// <summary>A window flashed for attention (shell HSHELL_FLASH). If it's managed
    /// and not currently focused, start pulsing its border until the user focuses it.</summary>
    public void NotifyFlash(IntPtr hwnd)
    {
        FlashCount++;
        string title = Native.WindowTitle(hwnd);
        bool managed = _byHwnd.TryGetValue(hwnd, out var mw);
        LastFlash = $"{(managed ? "managed" : "unmanaged")}: {(title.Length > 40 ? title[..40] : title)}";
        if (!managed) return;
        if (Native.GetForegroundWindow() == hwnd) return; // already looking at it
        _attention.Add(mw!);
    }

    private void ClearAttention(ManagedWindow mw)
    {
        if (_attention.Remove(mw)) mw.Overlay.SetAttention(null, null); // back to base color + width
    }

    private void PulseTick()
    {
        if (_attention.Count == 0) return;
        // VIOLENT strobe: fat 5x border, full-range slam between the base color and
        // white on a fast (~450ms) cycle so a waiting terminal is impossible to miss.
        double phase = (Environment.TickCount64 % 450) / 450.0;
        float t = (float)((Math.Sin(phase * Math.PI * 2) + 1) / 2); // 0..1
        foreach (var mw in _attention)
            if (!_paused)
                mw.Overlay.SetAttention(Blend(mw.Color, Color.White, t), BorderManager.BorderWidth * 5);
    }

    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));

    // ---- adding ---------------------------------------------------------------

    /// <summary>Picker entry: add a window the user dragged onto. Reuses an existing
    /// rule for the same title, else creates a new rule (cycled color).</summary>
    public bool Add(IntPtr hwnd)
    {
        if (!IsAddable(hwnd)) return false;
        string title = Native.WindowTitle(hwnd);

        var rule = ResolveRule(hwnd, title, out var pathKey);
        if (rule is null)
        {
            // New rule keyed to the terminal's WORKING DIR (stable); title is kept as
            // a fallback + display source. DisplayName stays null → chip shows the
            // folder name until you rename it.
            rule = new Config.Rule { PathKey = pathKey, TitleMatch = title, ColorArgb = Palette[_colorIndex++ % Palette.Length].ToArgb() };
            _config.Rules.Add(rule);
        }
        if (Native.GetWindowRect(hwnd, out var cur)) rule.SetHome(cur);

        bool ok = Attach(hwnd, rule, restorePosition: false);
        _config.Save();
        return ok;
    }

    /// <summary>Create the overlay + managed entry for a window, optionally moving it
    /// to the rule's saved home (used when restoring).</summary>
    private bool Attach(IntPtr hwnd, Config.Rule rule, bool restorePosition)
    {
        if (_byHwnd.ContainsKey(hwnd)) return false;
        var overlay = MakeOverlay(rule, Native.WindowTitle(hwnd));
        var mw = new ManagedWindow(hwnd, rule.Color, overlay);
        overlay.SummonRequested += () => Summon(mw);
        overlay.ConfigureRequested += () => ConfigureRequested?.Invoke(mw);

        _windows.Add(mw);
        _byHwnd[hwnd] = mw;
        _ruleOf[mw] = rule;
        RebuildExclusions();

        ApplyTaskbarTag(mw, rule);

        if (restorePosition) WindowMover.MoveTo(hwnd, rule.Home, bringToFront: false);
        if (rule.AlwaysOnTop) ApplyTopmost(hwnd, true);
        if (Native.GetWindowRect(hwnd, out var r)) { overlay.UpdateBounds(r); overlay.RestackAbove(hwnd); }
        return true;
    }

    /// <summary>Resolve a window to its saved rule, keyed by the terminal's working
    /// directory when available (stable) and falling back to title (VLC/browsers,
    /// legacy rules). Auto-migrates a matched legacy rule onto the stable path key.</summary>
    private Config.Rule? ResolveRule(IntPtr hwnd, string title, out string? pathKey)
    {
        pathKey = ProcessInfo.WorkingDirForWindow(hwnd);
        var rule = _config.Resolve(pathKey, title);
        if (rule is not null && pathKey is not null && string.IsNullOrEmpty(rule.PathKey))
            rule.PathKey = pathKey; // upgrade an old title-only rule in place
        return rule;
    }

    private bool IsAddable(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && Native.IsWindow(hwnd) && Native.IsWindowVisible(hwnd)
        && !_byHwnd.ContainsKey(hwnd) && !_excluded.Contains(hwnd);

    /// <summary>Chip label = the rule's custom name, else the project FOLDER name
    /// (from the working-dir key), else the live window title.</summary>
    private static string LabelFor(Config.Rule rule, string title)
    {
        if (!string.IsNullOrWhiteSpace(rule.DisplayName)) return rule.DisplayName!;
        if (!string.IsNullOrWhiteSpace(rule.PathKey))
        {
            var name = System.IO.Path.GetFileName(rule.PathKey!.TrimEnd('\\', '/'));
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return title;
    }

    private IBorderOverlay MakeOverlay(Config.Rule rule, string title) => _useRegion
        ? new RegionOverlay(rule.Color, BorderWidth, LabelFor(rule, title), rule.SafeFontSize)
        : new FourEdgeOverlay(rule.Color, BorderWidth, LabelFor(rule, title), rule.SafeFontSize);

    /// <summary>Raised (on UI thread) when a chip is right-clicked, so the form can
    /// pop the configure menu for that window.</summary>
    public event Action<ManagedWindow>? ConfigureRequested;

    public Config.Rule? RuleFor(ManagedWindow mw) => _ruleOf.GetValueOrDefault(mw);

    public void SaveConfig() => _config.Save();

    private static void ApplyTopmost(IntPtr hwnd, bool on) =>
        Native.SetWindowPos(hwnd, on ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    /// <summary>Keep-on-top for one window (saved per rule = per full path).</summary>
    public void SetAlwaysOnTop(ManagedWindow mw, bool on)
    {
        if (!_ruleOf.TryGetValue(mw, out var rule)) return;
        rule.AlwaysOnTop = on;
        ApplyTopmost(mw.Hwnd, on);
        if (Native.GetWindowRect(mw.Hwnd, out var r)) mw.Overlay.RestackAbove(mw.Hwnd);
        _config.Save();
    }

    // ---- taskbar button identity ---------------------------------------------

    /// <summary>Build (or tear down) this window's taskbar tag to match its rule.
    /// Called on attach and again whenever the label, color or toggle changes.</summary>
    private void ApplyTaskbarTag(ManagedWindow mw, Config.Rule rule)
    {
        mw.Tag?.Dispose();                 // hands back the old icon + strips the old prefix
        mw.Tag = null;
        if (!rule.TaskbarTag || _paused) return;

        var tag = new TaskbarTag(mw.Hwnd, rule.Color, LabelFor(rule, Native.WindowTitle(mw.Hwnd)));
        tag.ApplyIcon();
        tag.ApplyTitle();
        mw.Tag = tag;
    }

    /// <summary>Per-window toggle from the settings dialog.</summary>
    public void SetTaskbarTag(ManagedWindow mw, bool on)
    {
        if (!_ruleOf.TryGetValue(mw, out var rule)) return;
        rule.TaskbarTag = on;
        ApplyTaskbarTag(mw, rule);
        _config.Save();
    }

    /// <summary>Rebuild one window's overlay after its rule changed (name/color/font).</summary>
    public void RefreshAppearance(ManagedWindow mw)
    {
        if (!_ruleOf.TryGetValue(mw, out var rule)) return;
        var fresh = MakeOverlay(rule, Native.WindowTitle(mw.Hwnd));
        fresh.SummonRequested += () => Summon(mw);
        fresh.ConfigureRequested += () => ConfigureRequested?.Invoke(mw);
        mw.Overlay.Dispose();
        mw.SetColor(rule.Color);
        mw.SetOverlay(fresh);
        RebuildExclusions();
        ApplyTaskbarTag(mw, rule);   // label/color changed → restamp the taskbar button
        if (!_paused && Native.GetWindowRect(mw.Hwnd, out var r)) { fresh.UpdateBounds(r); fresh.RestackAbove(mw.Hwnd); }
        _config.Save();
    }

    // ---- persistence: restore on startup + on window appearance --------------

    /// <summary>Scan currently-open windows and border any that match a saved rule,
    /// restoring each matched rule's saved position to the FIRST match only.</summary>
    public void RestoreOpenWindows()
    {
        var seen = new List<(IntPtr h, string title)>();
        Native.EnumWindows((h, _) =>
        {
            if (Native.IsWindowVisible(h))
            {
                string t = Native.WindowTitle(h);
                if (t is not ("(untitled)" or "")) seen.Add((h, t));
            }
            return true;
        }, IntPtr.Zero);

        var positioned = new HashSet<Config.Rule>();
        foreach (var (h, title) in seen)
        {
            if (IsOurs(h)) continue;
            var rule = ResolveRule(h, title, out _);
            if (rule is null) continue;
            bool first = positioned.Add(rule);     // restore position to first match only
            Attach(h, rule, restorePosition: first);
        }
    }

    /// <summary>Match-on-appear: a newly shown window that matches a rule gets bordered
    /// + restored to its saved position.</summary>
    private void TryHandleNewWindow(IntPtr hwnd)
    {
        if (IsOurs(hwnd) || !Native.IsWindowVisible(hwnd)) return;
        if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return; // top-level only
        string title = Native.WindowTitle(hwnd);
        if (title is "(untitled)" or "") return;
        var rule = ResolveRule(hwnd, title, out _);
        if (rule is null) return;
        Attach(hwnd, rule, restorePosition: true);
    }

    // ---- the center-slot swap -------------------------------------------------

    public void Summon(ManagedWindow mw)
    {
        if (mw.IsCentered)
        {
            WindowMover.MoveTo(mw.Hwnd, mw.Home, bringToFront: true);
            mw.IsCentered = false; _centered = null;
            return;
        }
        if (_centered is { } prev && prev != mw)
        {
            WindowMover.MoveTo(prev.Hwnd, prev.Home, bringToFront: false);
            prev.IsCentered = false;
        }
        if (WindowMover.TryGetRect(mw.Hwnd, out var cur)) mw.Home = cur;
        _centerSize ??= WindowMover.DefaultCenterSize(mw.Hwnd);
        WindowMover.MoveTo(mw.Hwnd, WindowMover.CenteredRect(mw.Hwnd, _centerSize.Value), bringToFront: true);
        mw.IsCentered = true; _centered = mw;
        mw.Overlay.Flash();
    }

    // ---- pause / render mode / explicit removal ------------------------------

    public void SetPaused(bool paused)
    {
        _paused = paused;
        foreach (var mw in _windows)
        {
            if (paused) mw.Overlay.Hide();
            else if (Native.GetWindowRect(mw.Hwnd, out var r)) { mw.Overlay.UpdateBounds(r); mw.Overlay.RestackAbove(mw.Hwnd); }
            // Pause means "give me my windows back untouched" — that has to include
            // the taskbar buttons, or the titles stay prefixed while borders are off.
            if (paused) { mw.Tag?.Dispose(); mw.Tag = null; }
            else if (_ruleOf.TryGetValue(mw, out var rule)) ApplyTaskbarTag(mw, rule);
        }
    }

    public void SetRenderMode(bool useRegion)
    {
        if (useRegion == _useRegion) return;
        _useRegion = useRegion;
        foreach (var mw in _windows.ToList())
        {
            var rule = _ruleOf[mw];
            var fresh = MakeOverlay(rule, Native.WindowTitle(mw.Hwnd));
            fresh.SummonRequested += () => Summon(mw);
            fresh.ConfigureRequested += () => ConfigureRequested?.Invoke(mw);
            mw.Overlay.Dispose();
            mw.SetOverlay(fresh);
            if (!_paused && Native.GetWindowRect(mw.Hwnd, out var r)) { fresh.UpdateBounds(r); fresh.RestackAbove(mw.Hwnd); }
        }
        RebuildExclusions();
    }

    public IReadOnlyList<ManagedWindow> Windows => _windows;

    /// <summary>Tray "remove": stop managing AND forget the rule (won't re-border).</summary>
    public void RemoveRuleFor(ManagedWindow mw)
    {
        if (_ruleOf.TryGetValue(mw, out var rule)) _config.Rules.Remove(rule);
        DetachWindow(mw);
        _config.Save();
    }

    // ---- snapshot current positions into the config (called on close) --------

    public void SaveSnapshot()
    {
        foreach (var mw in _windows)
        {
            if (!_ruleOf.TryGetValue(mw, out var rule)) continue;
            // Persist where the window sits when NOT centered (its home).
            var home = mw.IsCentered ? mw.Home : (Native.GetWindowRect(mw.Hwnd, out var r) && !Native.LooksMinimized(r) ? r : mw.Home);
            rule.SetHome(home);
        }
        if (_centerSize is { } s) { _config.CenterWidth = s.Width; _config.CenterHeight = s.Height; }
        _config.Save();
    }

    // ---- internals -----------------------------------------------------------

    // ---- gesture actions (called on UI thread from the gesture bridge) -------

    private ManagedWindow? GestureTarget() =>
        (_active is not null && _byHwnd.ContainsValue(_active)) ? _active
        : _centered ?? _windows.FirstOrDefault();

    /// <summary>Open palm → center the active managed window.</summary>
    public void GestureCenterActive()
    {
        var t = GestureTarget();
        if (t is not null && !t.IsCentered) Summon(t);
    }

    /// <summary>Fist → fold the currently-centered window back home.</summary>
    public void GestureFoldHome()
    {
        if (_centered is not null) Summon(_centered); // Summon toggles a centered window home
    }

    /// <summary>Point → move focus to the managed window that lies in the given
    /// direction (dx/dy in screen space: +x right, +y down). Flashes the chosen
    /// window's border and brings it forward; open-palm then centers it.</summary>
    public void GestureFocusDirection(int dx, int dy)
    {
        if (_windows.Count == 0) return;
        var refWin = GestureTarget();
        if (refWin is null || !Native.GetWindowRect(refWin.Hwnd, out var rr)) return;
        var (rx, ry) = ((rr.Left + rr.Right) / 2, (rr.Top + rr.Bottom) / 2);

        ManagedWindow? best = null;
        long bestScore = long.MaxValue;
        foreach (var w in _windows)
        {
            if (w == refWin) continue;
            if (!Native.GetWindowRect(w.Hwnd, out var wr) || Native.LooksMinimized(wr)) continue;
            int cx = (wr.Left + wr.Right) / 2, cy = (wr.Top + wr.Bottom) / 2;
            int ddx = cx - rx, ddy = cy - ry;
            // candidate must be predominantly in the requested direction
            bool inDir = dx != 0 ? Math.Sign(ddx) == dx && Math.Abs(ddx) >= Math.Abs(ddy)
                                 : Math.Sign(ddy) == dy && Math.Abs(ddy) >= Math.Abs(ddx);
            if (!inDir) continue;
            long score = (long)ddx * ddx + (long)ddy * ddy; // nearest in that direction wins
            if (score < bestScore) { bestScore = score; best = w; }
        }

        var chosen = best ?? refWin;       // nothing that way → just re-flash current
        _active = chosen;
        WindowMover.ForceForeground(chosen.Hwnd);
        chosen.Overlay.Flash();
    }

    /// <summary>Swipe → cycle which managed window holds the center slot.</summary>
    public void GestureCycle(int dir)
    {
        if (_windows.Count == 0) return;
        var reference = _centered ?? GestureTarget();
        int i = reference is null ? -1 : _windows.IndexOf(reference);
        int next = ((i + dir) % _windows.Count + _windows.Count) % _windows.Count;
        Summon(_windows[next]);
    }

    private void DetachWindow(ManagedWindow mw)
    {
        if (_centered == mw) _centered = null;
        if (_active == mw) _active = null;
        _attention.Remove(mw);
        _uia.Forget(mw.Hwnd);
        // Restores the window's own icon + title. On EVENT_OBJECT_DESTROY the HWND
        // is already gone and the sends simply fail — but the HICONs still need freeing.
        mw.Tag?.Dispose();
        mw.Tag = null;
        mw.Overlay.Dispose();
        _windows.Remove(mw);
        _byHwnd.Remove(mw.Hwnd);
        _ruleOf.Remove(mw);
        RebuildExclusions();
    }

    private void RebuildExclusions()
    {
        _excluded.Clear();
        foreach (var w in _windows)
            foreach (var h in w.Overlay.OwnedHandles)
                _excluded.Add(h);
    }

    private void RestackAll()
    {
        if (_inUpdate || _paused) return;
        _inUpdate = true;
        try
        {
            foreach (var mw in _windows)
                if (Native.GetWindowRect(mw.Hwnd, out var r) && !Native.LooksMinimized(r))
                    mw.Overlay.RestackAbove(mw.Hwnd);
        }
        finally { _inUpdate = false; }
    }

    private void WinEventProc(IntPtr hHook, uint ev, IntPtr hwnd, int idObject,
        int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF) return;

        switch (ev)
        {
            case Native.EVENT_OBJECT_LOCATIONCHANGE:
                Interlocked.Increment(ref TotalLocationEvents);
                if (_excluded.Contains(hwnd)) { Interlocked.Increment(ref ExcludedEvents); return; }
                if (_paused || !_byHwnd.TryGetValue(hwnd, out var mw) || _inUpdate) return;
                if (Native.GetWindowRect(mw.Hwnd, out var r))
                {
                    _inUpdate = true;
                    try
                    {
                        Interlocked.Increment(ref ActedEvents);
                        mw.Overlay.UpdateBounds(r);
                        mw.Overlay.RestackAbove(mw.Hwnd);
                        if (!Native.LooksMinimized(r))
                        {
                            if (mw.IsCentered && _centered == mw) _centerSize = new Size(r.Width, r.Height);
                            else if (!mw.IsCentered && _ruleOf.TryGetValue(mw, out var ru)) ru.SetHome(r);
                        }
                    }
                    finally { _inUpdate = false; }
                }
                return;

            case Native.EVENT_OBJECT_NAMECHANGE:
                // Claude rewrites the console title constantly, wiping our label off
                // the taskbar button. Put it back. ApplyTitle no-ops when the prefix
                // is already present, which is what keeps our own WM_SETTEXT (which
                // raises this same event) from looping.
                if (!_paused && _byHwnd.TryGetValue(hwnd, out var nm)) nm.Tag?.ApplyTitle();
                return;

            case Native.EVENT_OBJECT_CREATE:
            case Native.EVENT_OBJECT_SHOW:
                TryHandleNewWindow(hwnd);
                return;

            case Native.EVENT_SYSTEM_FOREGROUND:
                if (_byHwnd.TryGetValue(hwnd, out var fm)) { _active = fm; ClearAttention(fm); }
                RestackAll();
                return;

            case Native.EVENT_OBJECT_DESTROY:
                // Window closed → stop managing it, but KEEP its rule so it
                // re-borders when reopened (title persistence).
                if (_byHwnd.TryGetValue(hwnd, out var gone)) DetachWindow(gone);
                return;

            case Native.EVENT_SYSTEM_MINIMIZESTART:
            case Native.EVENT_SYSTEM_MINIMIZEEND:
                if (!_paused && _byHwnd.TryGetValue(hwnd, out var m2) &&
                    Native.GetWindowRect(m2.Hwnd, out var mr)) m2.Overlay.UpdateBounds(mr);
                return;
        }
    }

    public void Dispose()
    {
        _pulse.Stop(); _pulse.Dispose();
        _idle.Stop(); _idle.Dispose();
        if (_hookObjects != IntPtr.Zero) Native.UnhookWinEvent(_hookObjects);
        if (_hookSystem != IntPtr.Zero) Native.UnhookWinEvent(_hookSystem);
        _hookObjects = _hookSystem = IntPtr.Zero;
        // Order matters: give every window its own icon and title back BEFORE we
        // exit, or the taskbar is left pointing at HICONs that die with this process.
        foreach (var w in _windows) { w.Tag?.Dispose(); w.Tag = null; }
        foreach (var w in _windows) w.Overlay.Dispose();
        _windows.Clear(); _byHwnd.Clear(); _ruleOf.Clear();
    }
}
