namespace BorderDock.Spike;

/// <summary>
/// Low-level window move helpers shared by the center-slot swap logic.
/// Includes the foreground-steal dance (the #1 kill-test) and 2x-center geometry.
/// </summary>
internal static class WindowMover
{
    public static bool TryGetRect(IntPtr hwnd, out Native.RECT r) =>
        Native.GetWindowRect(hwnd, out r) && !Native.LooksMinimized(r);

    /// <summary>Move a window to an exact rect, restoring it first if minimized.</summary>
    public static bool MoveTo(IntPtr hwnd, in Native.RECT r, bool bringToFront)
    {
        if (!Native.IsWindow(hwnd)) return false;
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);

        bool ok = Native.SetWindowPos(hwnd, Native.HWND_TOP, r.Left, r.Top, r.Width, r.Height,
            Native.SWP_SHOWWINDOW);
        if (bringToFront) ForceForeground(hwnd);
        return ok;
    }

    /// <summary>The center slot: a rect of the given size centered on the window's
    /// monitor working area, clamped so it always fits.</summary>
    public static Native.RECT CenteredRect(IntPtr hwnd, Size size)
    {
        var wa = Screen.FromHandle(hwnd).WorkingArea;
        int w = Math.Min(size.Width, wa.Width);
        int h = Math.Min(size.Height, wa.Height);
        int x = wa.Left + (wa.Width - w) / 2;
        int y = wa.Top + (wa.Height - h) / 2;
        return new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    /// <summary>Default center size = half the window's monitor working area.</summary>
    public static Size DefaultCenterSize(IntPtr hwnd)
    {
        var wa = Screen.FromHandle(hwnd).WorkingArea;
        return new Size(wa.Width / 2, wa.Height / 2);
    }

    /// <summary>SetForegroundWindow under the foreground lock needs the attach dance.</summary>
    public static bool ForceForeground(IntPtr target)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == target) return true;

        uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
        uint thisThread = Native.GetCurrentThreadId();

        bool attached = false;
        if (fgThread != 0 && fgThread != thisThread)
            attached = Native.AttachThreadInput(thisThread, fgThread, true);

        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        Native.SetForegroundWindow(target);
        Native.BringWindowToTop(target);

        if (attached) Native.AttachThreadInput(thisThread, fgThread, false);
        return Native.GetForegroundWindow() == target;
    }
}
