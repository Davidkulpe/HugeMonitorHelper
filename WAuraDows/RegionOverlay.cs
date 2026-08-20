using System.Drawing.Drawing2D;

namespace WAuraDows;

/// <summary>
/// Border = ONE window covering the target (outset by w), clipped via a Region
/// to just the frame. The excluded center means clicks there pass through to the
/// app below; the frame itself stays hit-testable for the summon click.
///
///     +====================+   the whole window is here, but its Region
///     |  (center excluded  |   excludes the center, so only the frame
///     |   = pass-through)  |   paints AND only the frame catches clicks.
///     +====================+
///
/// Pros tested here: 1 HWND, 1 move per event, no inter-edge tearing.
/// Cons tested here: region recompute on resize, slightly fiddlier geometry.
/// </summary>
internal sealed class RegionOverlay : IBorderOverlay
{
    private readonly FrameForm _form;
    private readonly TitleChip _chip;
    private readonly Color _baseColor;
    private readonly int _w;
    private bool _visible;

    public event Action? SummonRequested;
    public event Action? ConfigureRequested;

    public RegionOverlay(Color color, int borderWidth, string title, float fontSize)
    {
        _w = borderWidth;
        _baseColor = color;
        _form = new FrameForm(color, borderWidth);
        _form.Clicked += () => SummonRequested?.Invoke();
        _ = _form.Handle;
        _chip = new TitleChip(color, title, fontSize);
        _chip.Clicked += () => SummonRequested?.Invoke();
        _chip.RightClicked += () => ConfigureRequested?.Invoke();
    }

    public IEnumerable<IntPtr> OwnedHandles
    {
        get { yield return _form.Handle; yield return _chip.Handle; }
    }

    private int _attnWidth;
    private Native.RECT _last;
    private bool _hasLast;
    private int Ew => _attnWidth > 0 ? _attnWidth : _w;

    public void UpdateBounds(in Native.RECT t)
    {
        if (Native.LooksMinimized(t)) { Hide(); return; }
        _last = t; _hasLast = true;
        _chip.UpdatePosition(t, _w);

        int w = Ew;
        int x = t.Left - w, y = t.Top - w;
        int cx = t.Width + 2 * w, cy = t.Height + 2 * w;

        _form.SetFrameSize(cx, cy, w);
        Native.SetWindowPos(_form.Handle, IntPtr.Zero, x, y, cx, cy,
            Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW);
        _visible = true;
    }

    public void Hide()
    {
        if (!_visible) return;
        Native.SetWindowPos(_form.Handle, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | 0x0080 /*SWP_HIDEWINDOW*/);
        _chip.Hide();
        _visible = false;
    }

    public void RestackAbove(IntPtr target)
    {
        IntPtr after = target;
        foreach (var h in OwnedHandles)
        {
            Native.SetWindowPos(h, after, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            after = h;
        }
    }

    public void Flash() => _form.Flash();

    public void SetAttention(Color? color, int? width)
    {
        _attnWidth = width ?? 0;
        _form.SetLiveColor(color ?? _baseColor);
        if (_hasLast) UpdateBounds(_last);
    }

    public void Dispose() { _form.Dispose(); _chip.Dispose(); }

    private sealed class FrameForm : Form
    {
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_LBUTTONDOWN   = 0x0201;
        private const int MA_NOACTIVATE    = 3;

        private readonly Color _base;
        private readonly int _w;
        private readonly System.Windows.Forms.Timer _flashTimer;

        public event Action? Clicked;

        public FrameForm(Color color, int borderWidth)
        {
            _base = color;
            _w = borderWidth;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // NOT TopMost — z-ordered just above its own target by the manager.
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

        /// <summary>Clip to a frame of thickness w: outer rect minus inner rect.</summary>
        public void SetFrameSize(int cx, int cy, int thickness)
        {
            int w = thickness;
            var path = new GraphicsPath();
            path.AddRectangle(new Rectangle(0, 0, cx, cy));
            path.AddRectangle(new Rectangle(w, w, Math.Max(0, cx - 2 * w), Math.Max(0, cy - 2 * w)));
            // Alternate fill mode = the even-odd hole between the two rects = the frame.
            path.FillMode = FillMode.Alternate;
            Region = new Region(path);
        }

        public void Flash()
        {
            BackColor = ControlPaint.Light(_base, 0.7f);
            _flashTimer.Stop();
            _flashTimer.Start();
        }

        public void SetLiveColor(Color c) { _flashTimer.Stop(); BackColor = c; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
            if (m.Msg == WM_LBUTTONDOWN)   { Clicked?.Invoke(); return; }
            base.WndProc(ref m);
        }
    }
}
