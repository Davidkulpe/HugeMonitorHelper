namespace BorderDock.Spike;

/// <summary>
/// One window under BorderDock's management: its color, its border+chip overlay,
/// and its "home" rect (where it sits when not centered).
/// </summary>
internal sealed class ManagedWindow
{
    public IntPtr Hwnd { get; }
    public Color Color { get; private set; }
    public string Title { get; }
    public IBorderOverlay Overlay { get; private set; }

    /// <summary>Swap the overlay when the render mode changes (4-edge ⇄ region).</summary>
    public void SetOverlay(IBorderOverlay overlay) => Overlay = overlay;

    /// <summary>Update the identity color after the user changes it in the config menu.</summary>
    public void SetColor(Color color) => Color = color;

    /// <summary>Where the window lives when it is NOT in the center slot. Captured
    /// at the moment it gets summoned, so it folds back to wherever you placed it.</summary>
    public Native.RECT Home;

    public bool IsCentered;

    /// <summary>Taskbar button identity (color icon + label prefix), null when the
    /// rule has it switched off. Owns unmanaged HICONs — dispose on detach.</summary>
    public TaskbarTag? Tag;

    // Idle detection (terminal "Claude is waiting") state:
    public int? LastTextSig;       // last UIA text signature
    public long LastChangeMs;      // when the text last changed
    public bool WasActive;         // saw activity since the last idle-pulse (so we pulse once per work→wait)

    public ManagedWindow(IntPtr hwnd, Color color, IBorderOverlay overlay)
    {
        Hwnd = hwnd;
        Color = color;
        Title = Native.WindowTitle(hwnd);
        Overlay = overlay;
        if (Native.GetWindowRect(hwnd, out var r)) Home = r;
    }
}
