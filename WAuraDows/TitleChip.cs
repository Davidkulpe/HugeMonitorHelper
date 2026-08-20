namespace WAuraDows;

/// <summary>
/// The top-left title tag for a managed window: a small colored chip showing the
/// window's name (custom, or the window title). Left-click summons; right-click
/// raises RightClicked so the manager can show the configure menu (name/color/font).
/// </summary>
internal sealed class TitleChip : IDisposable
{
    private readonly ChipForm _form;

    public event Action? Clicked;
    public event Action? RightClicked;

    public TitleChip(Color color, string text, float fontSize)
    {
        _form = new ChipForm(color, text, fontSize);
        _form.Clicked += () => Clicked?.Invoke();
        _form.RightClicked += () => RightClicked?.Invoke();
        _ = _form.Handle;
    }

    public IntPtr Handle => _form.Handle;

    /// <summary>Pin the chip to the window's top-left, sitting just above the top edge.</summary>
    public void UpdatePosition(in Native.RECT target, int borderWidth)
    {
        if (Native.LooksMinimized(target)) { Hide(); return; }
        int w = _form.DesiredWidth, h = _form.DesiredHeight;
        int x = target.Left - borderWidth;
        int y = target.Top - borderWidth - h; // sit above the top border like a tab
        Native.SetWindowPos(_form.Handle, IntPtr.Zero, x, y, w, h,
            Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW);
    }

    public void Hide() =>
        Native.SetWindowPos(_form.Handle, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | 0x0080 /*HIDE*/);

    public void Dispose() => _form.Dispose();

    private sealed class ChipForm : Form
    {
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_LBUTTONDOWN   = 0x0201;
        private const int WM_RBUTTONUP     = 0x0205;
        private const int MA_NOACTIVATE    = 3;

        private readonly string _text;
        private readonly Color _fg;
        public int DesiredWidth { get; }
        public int DesiredHeight { get; }

        public event Action? Clicked;
        public event Action? RightClicked;

        public ChipForm(Color color, string text, float fontSize)
        {
            _text = text.Length > 60 ? text[..60] + "…" : text;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // NOT TopMost — z-ordered just above its own target by the manager.
            BackColor = color;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            // Contrast: white text on dark chips, black on light ones.
            double lum = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
            _fg = lum < 0.55 ? Color.White : Color.Black;
            Font = new Font("Segoe UI", fontSize, FontStyle.Bold);
            using var g = CreateGraphics();
            var sz = g.MeasureString(_text, Font);
            DesiredWidth = (int)Math.Ceiling(sz.Width) + 16;
            DesiredHeight = (int)Math.Ceiling(sz.Height) + 6;
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

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics, _text, Font, new Point(8, 3), _fg);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
            if (m.Msg == WM_LBUTTONDOWN)   { Clicked?.Invoke(); return; }
            if (m.Msg == WM_RBUTTONUP)     { RightClicked?.Invoke(); return; }
            base.WndProc(ref m);
        }
    }
}
