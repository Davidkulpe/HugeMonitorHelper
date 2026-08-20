namespace BorderDock.Spike;

/// <summary>
/// The live highlight shown while you drag the picker over windows: a bright,
/// click-through frame around whatever window is under the cursor. Click-through
/// (WS_EX_TRANSPARENT) so it never interferes with WindowFromPoint or the drag.
/// </summary>
internal sealed class PickHighlight : IDisposable
{
    private const int W = 4;
    private static readonly Color HiColor = Color.Gold;
    private readonly FrameForm _form = new();

    public IntPtr Handle => _form.Handle;

    public PickHighlight() => _ = _form.Handle;

    public void ShowAround(in Native.RECT t)
    {
        if (Native.LooksMinimized(t)) { Hide(); return; }
        int x = t.Left - W, y = t.Top - W, cx = t.Width + 2 * W, cy = t.Height + 2 * W;
        _form.SetFrame(cx, cy);
        Native.SetWindowPos(_form.Handle, Native.HWND_TOPMOST, x, y, cx, cy,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    public void Hide() =>
        Native.SetWindowPos(_form.Handle, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | 0x0080 /*HIDE*/);

    public void Dispose() => _form.Dispose();

    private sealed class FrameForm : Form
    {
        private const int WS_EX_TOPMOST     = 0x00000008;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW  = 0x00000080;
        private const int WS_EX_NOACTIVATE  = 0x08000000;

        public FrameForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = HiColor;
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // transparent = click-through; topmost so it shows over everything
                // during the pick (this one IS meant to float on top, briefly).
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void SetFrame(int cx, int cy)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddRectangle(new Rectangle(0, 0, cx, cy));
            path.AddRectangle(new Rectangle(W, W, Math.Max(0, cx - 2 * W), Math.Max(0, cy - 2 * W)));
            path.FillMode = System.Drawing.Drawing2D.FillMode.Alternate;
            Region = new Region(path);
        }
    }
}
