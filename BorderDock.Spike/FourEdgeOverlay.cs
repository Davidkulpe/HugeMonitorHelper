using System.ComponentModel;

namespace BorderDock.Spike;

/// <summary>
/// Border = four thin top-level windows, one per edge, drawn just OUTSIDE the
/// target rect so the window body is never covered:
///
///     +--------------------+   ← top edge (full width + 2w)
///     |                    |
///  L  |   target window    |  R   ← left / right edges (target height)
///     |                    |
///     +--------------------+   ← bottom edge
///
/// Pros tested here: trivially clickable edges, dead-simple geometry.
/// Cons tested here: 4 HWNDs, possible inter-edge tearing on fast drags.
/// </summary>
internal sealed class FourEdgeOverlay : IBorderOverlay
{
    private readonly EdgeForm[] _edges;
    private readonly TitleChip _chip;
    private readonly Color _baseColor;
    private readonly int _w;
    private bool _visible;

    public event Action? SummonRequested;
    public event Action? ConfigureRequested;

    public FourEdgeOverlay(Color color, int borderWidth, string title, float fontSize)
    {
        _w = borderWidth;
        _baseColor = color;
        _edges = new EdgeForm[4];
        for (int i = 0; i < 4; i++)
        {
            _edges[i] = new EdgeForm(color);
            _edges[i].Clicked += () => SummonRequested?.Invoke();
            _ = _edges[i].Handle; // force HWND creation
        }
        _chip = new TitleChip(color, title, fontSize);
        _chip.Clicked += () => SummonRequested?.Invoke();
        _chip.RightClicked += () => ConfigureRequested?.Invoke();
    }

    public IEnumerable<IntPtr> OwnedHandles => _edges.Select(e => e.Handle).Append(_chip.Handle);

    private int _attnWidth;            // >0 while pulsing → fat border
    private Native.RECT _last;
    private bool _hasLast;
    private int Ew => _attnWidth > 0 ? _attnWidth : _w;

    public void UpdateBounds(in Native.RECT t)
    {
        if (Native.LooksMinimized(t)) { Hide(); return; }
        _last = t; _hasLast = true;
        _chip.UpdatePosition(t, _w);

        int w = Ew;
        int fullW = t.Width + 2 * w;
        // edge index: 0=top 1=bottom 2=left 3=right
        Span<(int x, int y, int cx, int cy)> r = stackalloc (int, int, int, int)[4];
        r[0] = (t.Left - w, t.Top - w, fullW, w);          // top
        r[1] = (t.Left - w, t.Bottom, fullW, w);           // bottom
        r[2] = (t.Left - w, t.Top, w, t.Height);           // left
        r[3] = (t.Right,    t.Top, w, t.Height);           // right

        // Atomic batch move of all 4 edges (eng-review decision: no trail).
        IntPtr hdwp = Native.BeginDeferWindowPos(4);
        const uint flags = Native.SWP_NOACTIVATE | Native.SWP_NOZORDER |
                           Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW;
        for (int i = 0; i < 4; i++)
            hdwp = Native.DeferWindowPos(hdwp, _edges[i].Handle, IntPtr.Zero,
                r[i].x, r[i].y, r[i].cx, r[i].cy, flags);
        Native.EndDeferWindowPos(hdwp);
        _visible = true;
    }

    public void Hide()
    {
        if (!_visible) return;
        foreach (var e in _edges)
            Native.SetWindowPos(e.Handle, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | 0x0080 /*SWP_HIDEWINDOW*/);
        _chip.Hide();
        _visible = false;
    }

    public void RestackAbove(IntPtr target)
    {
        // Stack each owned window just above the previous, all immediately above
        // the target → they ride directly on top of their own window, below
        // anything that covers it.
        IntPtr after = target;
        foreach (var h in OwnedHandles)
        {
            Native.SetWindowPos(h, after, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            after = h;
        }
    }

    public void Flash()
    {
        foreach (var e in _edges) e.Flash();
    }

    public void SetAttention(Color? color, int? width)
    {
        _attnWidth = width ?? 0;
        var c = color ?? _baseColor;
        foreach (var e in _edges) e.SetLiveColor(c);
        if (_hasLast) UpdateBounds(_last); // re-layout so the fat width takes effect now
    }

    public void Dispose()
    {
        foreach (var e in _edges) e.Dispose();
        _chip.Dispose();
    }

    /// <summary>One edge strip. No-activate so clicking it never steals focus.</summary>
    private sealed class EdgeForm : Form
    {
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_LBUTTONDOWN   = 0x0201;
        private const int MA_NOACTIVATE    = 3;

        private readonly Color _base;
        private readonly System.Windows.Forms.Timer _flashTimer;

        public event Action? Clicked;

        public EdgeForm(Color color)
        {
            _base = color;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // NOT TopMost — the BorderManager z-orders each edge just above its own
            // target window, so a window in front of the target covers the border too.
            BackColor = color;
            AutoScaleMode = AutoScaleMode.None;
            _flashTimer = new System.Windows.Forms.Timer { Interval = 130 };
            _flashTimer.Tick += (_, _) => { BackColor = _base; _flashTimer.Stop(); };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void Flash()
        {
            BackColor = ControlPaint.Light(_base, 0.7f);
            _flashTimer.Stop();
            _flashTimer.Start();
        }

        /// <summary>Set the current paint color (used by the attention pulse).</summary>
        public void SetLiveColor(Color c) { _flashTimer.Stop(); BackColor = c; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
            if (m.Msg == WM_LBUTTONDOWN)   { Clicked?.Invoke(); return; }
            base.WndProc(ref m);
        }
    }
}
