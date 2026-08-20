namespace BorderDock.Spike;

/// <summary>
/// Per-window settings window (replaces the right-click popup menu, which couldn't
/// reliably take foreground from a no-activate chip). A normal modal form — clicks
/// just work. Changes apply live so you see the border update. Keyed by full path.
/// </summary>
internal sealed class ConfigForm : Form
{
    private static readonly float[] Sizes = { 8, 9, 10, 12, 14, 16, 20, 24 };

    private readonly Config.Rule _rule;
    private readonly ManagedWindow _mw;
    private readonly BorderManager _manager;

    public ConfigForm(Config.Rule rule, ManagedWindow mw, BorderManager manager)
    {
        _rule = rule; _mw = mw; _manager = manager;

        Text = "BorderDock — window settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(420, 316);
        TopMost = true;

        int y = 12;

        bool keyedByPath = !string.IsNullOrEmpty(rule.PathKey);
        Add(new Label { Left = 12, Top = y, Width = 396, Height = 16,
            Text = keyedByPath ? "Saved under (working dir = the key):" : "Saved under (window title = the key):",
            ForeColor = Color.DimGray });
        y += 18;
        Add(new Label { Left = 12, Top = y, Width = 396, Height = 32, Text = keyedByPath ? rule.PathKey! : rule.TitleMatch,
            AutoEllipsis = true, Font = new Font("Segoe UI", 8f) });
        y += 40;

        Add(new Label { Left = 12, Top = y + 3, Width = 90, Text = "Label text:" });
        var label = new TextBox { Left = 106, Top = y, Width = 302,
            Text = rule.DisplayName ?? Native.WindowTitle(mw.Hwnd) };
        void ApplyLabel()
        {
            _rule.DisplayName = string.IsNullOrWhiteSpace(label.Text) ? null : label.Text.Trim();
            _manager.RefreshAppearance(_mw);
        }
        label.Leave += (_, _) => ApplyLabel();
        label.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { ApplyLabel(); e.SuppressKeyPress = true; } };
        Add(label);
        y += 32;

        Add(new Label { Left = 12, Top = y + 3, Width = 90, Text = "Color:" });
        var swatch = new Panel { Left = 106, Top = y, Width = 60, Height = 24, BackColor = rule.Color, BorderStyle = BorderStyle.FixedSingle };
        var colorBtn = new Button { Left = 172, Top = y - 1, Width = 120, Height = 26, Text = "Change color…" };
        colorBtn.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = _rule.Color, FullOpen = true };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _rule.ColorArgb = dlg.Color.ToArgb();
                swatch.BackColor = dlg.Color;
                _manager.RefreshAppearance(_mw);
            }
        };
        Add(swatch); Add(colorBtn);
        y += 34;

        Add(new Label { Left = 12, Top = y + 3, Width = 90, Text = "Font size:" });
        var font = new ComboBox { Left = 106, Top = y, Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var s in Sizes) font.Items.Add($"{s:0}");
        font.SelectedIndex = Math.Max(0, Array.FindIndex(Sizes, s => Math.Abs(s - rule.SafeFontSize) < 0.1f));
        font.SelectedIndexChanged += (_, _) => { _rule.FontSize = Sizes[font.SelectedIndex]; _manager.RefreshAppearance(_mw); };
        Add(font);
        y += 34;

        var onTop = new CheckBox { Left = 106, Top = y, Width = 200, Text = "Keep this window on top", Checked = rule.AlwaysOnTop };
        onTop.CheckedChanged += (_, _) => _manager.SetAlwaysOnTop(_mw, onTop.Checked);
        Add(onTop);
        y += 26;

        var taskbar = new CheckBox { Left = 106, Top = y, Width = 302,
            Text = "Show label + color on the taskbar button", Checked = rule.TaskbarTag };
        taskbar.CheckedChanged += (_, _) => _manager.SetTaskbarTag(_mw, taskbar.Checked);
        Add(taskbar);
        y += 34;

        var reset = new Button { Left = 12, Top = y, Width = 130, Height = 28, Text = "Reset label" };
        reset.Click += (_, _) => { _rule.DisplayName = null; label.Text = Native.WindowTitle(_mw.Hwnd); _manager.RefreshAppearance(_mw); };
        var remove = new Button { Left = 150, Top = y, Width = 130, Height = 28, Text = "Remove border" };
        remove.Click += (_, _) => { _manager.RemoveRuleFor(_mw); Close(); };
        var close = new Button { Left = 318, Top = y, Width = 90, Height = 28, Text = "Close", DialogResult = DialogResult.OK };
        Add(reset); Add(remove); Add(close);
        AcceptButton = close;
    }

    private void Add(Control c) => Controls.Add(c);
}
