namespace WAuraDows;

/// <summary>
/// A colored border drawn around one target window. The spike has two
/// implementations so we can compare them on tracking quality, tearing, and
/// click reliability:
///   - FourEdgeOverlay  : 4 separate thin top-level windows
///   - RegionOverlay    : 1 window clipped to a frame region
///
/// The eng review left this as the thing to settle empirically. This interface
/// is what lets the spike swap between them at runtime.
/// </summary>
internal interface IBorderOverlay : IDisposable
{
    /// <summary>Reposition the border to frame this target rect (physical px).</summary>
    void UpdateBounds(in Native.RECT target);

    /// <summary>Hide the border (e.g. target minimized) without destroying it.</summary>
    void Hide();

    /// <summary>Z-order this overlay's windows directly ABOVE the target (not global
    /// topmost), so a window in front of the target occludes the border too.</summary>
    void RestackAbove(IntPtr target);

    /// <summary>Brief brighten to acknowledge a summon click/hotkey.</summary>
    void Flash();

    /// <summary>Attention pulse: live border color AND width. Pass (null, null) to
    /// revert to the window's base color and base width.</summary>
    void SetAttention(Color? color, int? width);

    /// <summary>Every HWND this overlay owns — used by the tracker's exclusion set
    /// so the overlay's own moves don't feed back into the WinEvent hook.</summary>
    IEnumerable<IntPtr> OwnedHandles { get; }

    /// <summary>Raised when the user clicks the border (the summon gesture).</summary>
    event Action? SummonRequested;

    /// <summary>Raised when the user right-clicks the title chip (configure menu).</summary>
    event Action? ConfigureRequested;
}
