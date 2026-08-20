namespace BorderDock.Spike;

/// <summary>Minimal modal text-input dialog (WinForms ships no InputBox).</summary>
internal static class InputBox
{
    public static bool Ask(string prompt, string title, string initial, out string value)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(380, 130),
            MinimizeBox = false,
            MaximizeBox = false,
            TopMost = true,
        };

        var label = new Label { Left = 12, Top = 12, Width = 356, Text = prompt, AutoSize = false, Height = 20 };
        var box = new TextBox { Left = 12, Top = 38, Width = 356, Text = initial };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 200, Top = 80, Width = 78 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 290, Top = 80, Width = 78 };

        form.Controls.AddRange(new Control[] { label, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        box.SelectAll();

        bool okPressed = form.ShowDialog() == DialogResult.OK;
        value = box.Text;
        return okPressed;
    }
}
