using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace BorderDock.Spike;

/// <summary>
/// Puts a managed window's identity onto its TASKBAR button. Collapsed, seven
/// Claude terminals are seven identical console buttons — the border and chip
/// only help while the window is on screen. So we push two things onto the
/// target window itself:
///
///   icon  → WM_SETICON with a solid swatch of the rule's color + initials
///   text  → WM_SETTEXT prefixing the rule's label onto the live title
///
/// Both poke a window we don't own, so both are best-effort and both are sent
/// with a timeout (a hung terminal must never freeze BorderDock's UI thread).
/// The title especially is a running fight: Claude rewrites the console title
/// on every turn, so BorderManager re-applies the prefix on NAMECHANGE. The
/// prefix check is what stops that from looping — our own SetWindowText raises
/// NAMECHANGE too, and the second pass sees the prefix already there and stops.
/// </summary>
internal sealed class TaskbarTag : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly string _prefix;

    private IntPtr _small, _big;          // ours — must DestroyIcon
    private IntPtr _prevSmall, _prevBig;  // theirs — handed back on dispose
    private bool _iconApplied;

    public TaskbarTag(IntPtr hwnd, Color color, string label)
    {
        _hwnd = hwnd;
        _prefix = $"{label.Trim()} · ";
        _small = MakeIcon(color, label, SystemInformation.SmallIconSize.Width);
        _big   = MakeIcon(color, label, SystemInformation.IconSize.Width);
    }

    /// <summary>Push the color icon onto the window, remembering what was there.</summary>
    public void ApplyIcon()
    {
        if (_iconApplied) return;
        if (_small != IntPtr.Zero) _prevSmall = Send(Native.WM_SETICON, (IntPtr)Native.ICON_SMALL, _small);
        if (_big   != IntPtr.Zero) _prevBig   = Send(Native.WM_SETICON, (IntPtr)Native.ICON_BIG,   _big);
        _iconApplied = true;
    }

    /// <summary>Re-prefix the label onto the live title. No-op when it's already
    /// there, which is both the cheap path and the loop guard.</summary>
    public void ApplyTitle()
    {
        string cur = Native.WindowTitle(_hwnd);
        if (cur == "(untitled)") return;
        if (cur.StartsWith(_prefix, StringComparison.Ordinal)) return;
        SetTitle(_prefix + cur);
    }

    /// <summary>Hand the window back its own icon and drop our title prefix.
    /// We strip the prefix off the CURRENT title rather than restoring the one we
    /// saw at attach time — by now Claude has rewritten it many times over.</summary>
    public void Remove()
    {
        string cur = Native.WindowTitle(_hwnd);
        if (cur.StartsWith(_prefix, StringComparison.Ordinal))
            SetTitle(cur[_prefix.Length..]);

        if (_iconApplied)
        {
            Send(Native.WM_SETICON, (IntPtr)Native.ICON_SMALL, _prevSmall);
            Send(Native.WM_SETICON, (IntPtr)Native.ICON_BIG,   _prevBig);
            _iconApplied = false;
        }
    }

    public void Dispose()
    {
        Remove();
        // Only safe once the window has stopped pointing at them.
        if (_small != IntPtr.Zero) { Native.DestroyIcon(_small); _small = IntPtr.Zero; }
        if (_big   != IntPtr.Zero) { Native.DestroyIcon(_big);   _big   = IntPtr.Zero; }
    }

    // ---- cross-process sends, always with a timeout --------------------------

    private const uint SendFlags = Native.SMTO_ABORTIFHUNG | Native.SMTO_NORMAL;
    private const uint SendTimeoutMs = 200;

    private IntPtr Send(uint msg, IntPtr wParam, IntPtr lParam)
    {
        Native.SendMessageTimeout(_hwnd, msg, wParam, lParam, SendFlags, SendTimeoutMs, out var result);
        return result;
    }

    private void SetTitle(string text) =>
        Native.SendMessageTimeout(_hwnd, Native.WM_SETTEXT, IntPtr.Zero, text,
            SendFlags, SendTimeoutMs, out _);

    // ---- the swatch icon ------------------------------------------------------

    /// <summary>A rounded square of the rule's color with the project's initials,
    /// so the button is identifiable by color at a glance and by letter up close.</summary>
    private static IntPtr MakeIcon(Color color, string label, int size)
    {
        if (size <= 0) size = 16;
        try
        {
            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                int radius = Math.Max(2, size / 5);
                using (var path = RoundedRect(new Rectangle(0, 0, size - 1, size - 1), radius))
                using (var brush = new SolidBrush(color))
                    g.FillPath(brush, path);

                string text = Initials(label);
                // Same contrast rule the title chip uses: white on dark, black on light.
                double lum = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
                var fg = lum < 0.55 ? Color.White : Color.Black;

                DrawFitted(g, text, size, fg);
            }
            return bmp.GetHicon();
        }
        catch { return IntPtr.Zero; }   // no icon is better than a crash
    }

    /// <summary>Shrink the font until the initials fit the swatch, then center them.</summary>
    private static void DrawFitted(Graphics g, string text, int size, Color fg)
    {
        float pt = size * 0.52f;
        SizeF measured = SizeF.Empty;
        Font? font = null;
        try
        {
            for (; pt >= 4f; pt -= 0.5f)
            {
                font?.Dispose();
                font = new Font("Segoe UI", pt, FontStyle.Bold, GraphicsUnit.Pixel);
                measured = g.MeasureString(text, font);
                if (measured.Width <= size - 2 && measured.Height <= size) break;
            }
            if (font is null) return;
            using var brush = new SolidBrush(fg);
            g.DrawString(text, font, brush,
                (size - measured.Width) / 2f, (size - measured.Height) / 2f);
        }
        finally { font?.Dispose(); }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Up to two letters: word initials when the label has several words,
    /// else the camelCase humps ("PaymentsApi" → PA), else the first two characters.</summary>
    private static string Initials(string label)
    {
        string name = (label ?? "").Trim();
        if (name.Length == 0) return "?";

        var words = name.Split(new[] { ' ', '-', '_', '.', '/', '\\' },
                               StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2)
            return $"{words[0][0]}{words[1][0]}".ToUpperInvariant();

        string w = words[0];
        for (int i = 1; i < w.Length; i++)
            if (char.IsUpper(w[i]) && char.IsLower(w[i - 1]))
                return $"{w[0]}{w[i]}".ToUpperInvariant();

        return (w.Length == 1 ? w : w[..2]).ToUpperInvariant();
    }
}
