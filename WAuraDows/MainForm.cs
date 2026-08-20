using System.Diagnostics;

namespace WAuraDows;

/// <summary>
/// WAuraDows main window + tray. Drag the target onto a window to add it (saved
/// as a title rule); borders + positions persist and restore on next launch.
/// Closing hides to tray; Quit (from the tray menu) really exits.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly Label _status = new() { Dock = DockStyle.Fill, Padding = new Padding(10), AutoSize = false };
    private readonly Button _target = new()
    {
        Text = "🎯  Drag me onto a window to add it",
        Height = 46, Dock = DockStyle.Top, Font = new Font("Segoe UI", 10f, FontStyle.Bold)
    };
    private readonly Button _toggle = new() { Text = "Render: 4-EDGE (click to switch)", Height = 36, Dock = DockStyle.Top };
    private readonly System.Windows.Forms.Timer _readout = new() { Interval = 400 };

    private readonly Config _config = Config.Load();
    private readonly BorderManager _manager;
    private readonly PickHighlight _highlight = new();
    private readonly GestureBridge _gestures = new();
    // Real (activatable) but off-screen owner so the context menu can take
    // foreground + mouse capture. An opacity-0 / topmost owner does NOT reliably
    // activate, which let clicks fall through to the borders underneath.
    private readonly Form _menuOwner = new()
    {
        FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
        StartPosition = FormStartPosition.Manual, Size = new Size(1, 1),
        Location = new Point(-5000, -5000),
    };
    private readonly NotifyIcon _tray;
    private ToolStripMenuItem? _gestureItem;
    private string _gestureInfo = "gestures: off";
    private bool _useRegion;
    private bool _picking;
    private bool _quitting;
    private IntPtr _candidate;

    public MainForm()
    {
        _manager = new BorderManager(_config);

        Text = "WAuraDows";
        Icon = LoadAppIcon();
        Width = 470; Height = 360;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;

        Controls.Add(_status);
        Controls.Add(_toggle);
        Controls.Add(_target);

        _target.MouseDown += OnPickStart;
        _target.MouseMove += OnPickMove;
        _target.MouseUp   += OnPickEnd;
        _toggle.Click += (_, _) => { _useRegion = !_useRegion; _manager.SetRenderMode(_useRegion);
            _toggle.Text = _useRegion ? "Render: REGION (click to switch)" : "Render: 4-EDGE (click to switch)"; };

        _gestures.Gesture += OnGesture;
        _gestures.Status += OnGestureStatus;
        _manager.ConfigureRequested += OnConfigure;

        _tray = BuildTray();
        _readout.Tick += (_, _) => RefreshStatus();
        _readout.Start();
        RefreshStatus();
    }

    // ---- right-click the title chip → configure (name / color / font) --------

    private void OnConfigure(ManagedWindow mw)
    {
        var rule = _manager.RuleFor(mw);
        if (rule is null) return;
        // The chip is a no-activate window, so our process is in the background.
        // Park the off-screen owner, force it foreground (the same AttachThreadInput
        // dance Summon uses), then show the settings dialog owned by it so it gets focus.
        _menuOwner.Show();
        WindowMover.ForceForeground(_menuOwner.Handle);
        using (var dlg = new ConfigForm(rule, mw, _manager))
            dlg.ShowDialog(_menuOwner);
        _menuOwner.Hide();
    }

    // ---- gestures ------------------------------------------------------------

    private void OnGesture(string g)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            switch (g)
            {
                case "OPEN_PALM":  _manager.GestureCenterActive(); break;
                case "FIST":       _manager.GestureFoldHome(); break;
                case "SWIPE_LEFT": _manager.GestureCycle(-1); break;
                case "SWIPE_RIGHT":_manager.GestureCycle(+1); break;
                case "POINT_LEFT": _manager.GestureFocusDirection(-1, 0); break;
                case "POINT_RIGHT":_manager.GestureFocusDirection(+1, 0); break;
                case "POINT_UP":   _manager.GestureFocusDirection(0, -1); break;
                case "POINT_DOWN": _manager.GestureFocusDirection(0, +1); break;
            }
            _gestureInfo = $"gestures: last → {g}";
            RefreshStatus();
        });
    }

    private void OnGestureStatus(string s)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (s.StartsWith("ERROR") || s == "stopped")
            {
                _gestureInfo = $"gestures: {s}";
                if (_gestureItem is not null) _gestureItem.Checked = false;
            }
            else _gestureInfo = $"gestures: {s}";
            RefreshStatus();
        });
    }

    private uint _shellHookMsg;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Listen for window attention flashes (Claude bell) via the shell hook.
        Native.RegisterShellHookWindow(Handle);
        _shellHookMsg = Native.RegisterWindowMessage("SHELLHOOK");
    }

    protected override void WndProc(ref Message m)
    {
        if (_shellHookMsg != 0 && m.Msg == (int)_shellHookMsg &&
            m.WParam.ToInt32() == Native.HSHELL_FLASH)
        {
            _manager.NotifyFlash(m.LParam); // the flashing window's handle
        }
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Persistence: re-border any open windows that match saved rules.
        _manager.RestoreOpenWindows();
        RefreshStatus();
    }

    // ---- tray ----------------------------------------------------------------

    private NotifyIcon BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Add window (drag the target)", null, (_, _) => ShowPanel());

        // Launch saved projects in their OWN console process (conhost, not a
        // Windows Terminal tab) so the window is identifiable by working dir and
        // the border attaches automatically. This exists because terminals started
        // inside shared WT can never be matched to a project (one process hosts
        // every window). Rebuilt on open so new rules appear without restart.
        var launch = new ToolStripMenuItem("Launch project");
        menu.Opening += (_, _) => RebuildLaunchMenu(launch);
        menu.Items.Add(launch);
        menu.Items.Add(new ToolStripSeparator());

        var pause = new ToolStripMenuItem("Pause borders") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => _manager.SetPaused(pause.Checked);
        menu.Items.Add(pause);

        Startup.MigrateLegacyEntry();   // adopt a pre-rename autostart entry before reading it
        var startup = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = Startup.IsEnabled() };
        startup.CheckedChanged += (_, _) => Startup.Set(startup.Checked);
        menu.Items.Add(startup);

        _gestureItem = new ToolStripMenuItem("Enable hand gestures (camera)") { CheckOnClick = true };
        _gestureItem.CheckedChanged += (_, _) =>
        {
            if (_gestureItem.Checked) { if (!_gestures.Start()) _gestureItem.Checked = false; }
            else { _gestures.Stop(); _gestureInfo = "gestures: off"; }
            RefreshStatus();
        };
        menu.Items.Add(_gestureItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open config file", null, (_, _) => OpenConfig());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { _quitting = true; Close(); });

        var tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "WAuraDows",
            ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => ShowPanel();
        return tray;
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            string p = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (System.IO.File.Exists(p)) return new Icon(p);
            var fromExe = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (fromExe is not null) return fromExe;
        }
        catch { }
        return SystemIcons.Application;
    }

    private void ShowPanel() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    private void RebuildLaunchMenu(ToolStripMenuItem launch)
    {
        launch.DropDownItems.Clear();
        foreach (var rule in _config.Rules.Where(r => !string.IsNullOrEmpty(r.PathKey)))
        {
            string path = rule.PathKey!;
            string name = string.IsNullOrWhiteSpace(rule.DisplayName)
                ? System.IO.Path.GetFileName(path.TrimEnd('\\', '/'))
                : rule.DisplayName!;
            var item = new ToolStripMenuItem($"{name}   ({path})");
            item.Click += (_, _) => LaunchProject(path);
            launch.DropDownItems.Add(item);
        }
        launch.Enabled = launch.DropDownItems.Count > 0;
        if (!launch.Enabled)
            launch.DropDownItems.Add(new ToolStripMenuItem("(no path-keyed projects yet)") { Enabled = false });
    }

    /// <summary>Open claude in its own conhost console at <paramref name="path"/> —
    /// continue the last session, or start fresh if there's nothing to continue.</summary>
    private void LaunchProject(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "conhost.exe",
                Arguments = "pwsh.exe -NoExit -Command \"claude -c; if ($LASTEXITCODE -ne 0) { claude }\"",
                WorkingDirectory = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { _status.Text = $"Launch failed: {ex.Message}"; }
    }

    private void OpenConfig()
    {
        _config.Save(); // ensure the file exists before opening
        try { Process.Start(new ProcessStartInfo(Config.Path_) { UseShellExecute = true }); } catch { }
    }

    // ---- drag-to-pick --------------------------------------------------------

    private void OnPickStart(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _picking = true; _candidate = IntPtr.Zero; _target.Capture = true;
        Cursor = Cursors.Cross; _target.Text = "… drag over a window, then release";
    }

    private void OnPickMove(object? s, MouseEventArgs e)
    {
        if (!_picking) return;
        IntPtr hwnd = WindowUnderCursor();
        _candidate = hwnd;
        if (hwnd != IntPtr.Zero && Native.GetWindowRect(hwnd, out var r))
        { _highlight.ShowAround(r); _status.Text = $"Release to add:\n{Native.WindowTitle(hwnd)}"; }
        else { _highlight.Hide(); _status.Text = "Release over a window to add it…"; }
    }

    private void OnPickEnd(object? s, MouseEventArgs e)
    {
        if (!_picking) return;
        _picking = false; _target.Capture = false; Cursor = Cursors.Default;
        _highlight.Hide(); _target.Text = "🎯  Drag me onto a window to add it";
        if (_candidate != IntPtr.Zero && !_manager.Add(_candidate))
            _status.Text = "Couldn't add that one (already added, or not a normal window).";
        _candidate = IntPtr.Zero;
        RefreshStatus();
    }

    private IntPtr WindowUnderCursor()
    {
        var p = Cursor.Position;
        IntPtr hwnd = Native.WindowFromPoint(new Native.POINT(p.X, p.Y));
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        IntPtr root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        if (root == Handle || root == _highlight.Handle || _manager.IsOurs(root)) return IntPtr.Zero;
        if (!Native.IsWindowVisible(root)) return IntPtr.Zero;
        return root;
    }

    // ---- status / lifecycle --------------------------------------------------

    private void RefreshStatus()
    {
        if (_picking) return;
        _status.Text =
            $"Managed windows: {_manager.Count}   |   saved rules: {_config.Rules.Count}\n" +
            $"Centered now: {_manager.CenteredTitle}\n" +
            $"Center size: {_manager.CenterSizeText}\n" +
            $"Render: {(_manager.UseRegion ? "single region window" : "four edge windows")}" +
            (_manager.Paused ? "   (PAUSED)" : "") + "\n" +
            $"{_gestureInfo}\n" +
            $"idle pulses: {_manager.IdlePulses}  ·  last waiting: {_manager.LastIdle}\n\n" +
            "Drag the 🎯 onto a window to add it (saved by title).\n" +
            "Click a border → center (half-screen); previous folds home.\n" +
            "Tray → Enable hand gestures: point=focus that direction,\n" +
            "open palm=center focused, fist=fold home, swipe=cycle.";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // X / minimize-style close → hide to tray instead of exiting.
        if (!_quitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _manager.SaveSnapshot();
        Native.DeregisterShellHookWindow(Handle);
        _menuOwner.Dispose();
        _gestures.Dispose();
        _highlight.Dispose();
        _manager.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }
}
