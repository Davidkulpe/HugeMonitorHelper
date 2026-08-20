using System.Collections.Concurrent;
using System.Windows.Automation;

namespace WAuraDows;

/// <summary>
/// Reads a terminal window's visible text via UI Automation, so we can tell when
/// it's actively changing (Claude working / spinner animating) vs static (done /
/// waiting for input). Caches the text-pattern element per HWND so polling is cheap.
/// All UIA calls are wrapped — any failure just yields null (treated as "unknown").
/// </summary>
internal sealed class UiaText
{
    // Concurrent because the scan runs on a background thread while Forget() is
    // called from the UI thread as windows detach.
    private readonly ConcurrentDictionary<IntPtr, AutomationElement?> _textEl = new();

    /// <summary>A cheap signature of the window's current text (length + hash of a
    /// capped slice). Returns null if UIA can't read it. Changing signature = activity.</summary>
    public int? Snapshot(IntPtr hwnd)
    {
        try
        {
            var el = ResolveTextElement(hwnd);
            if (el is null) return null;
            if (el.TryGetCurrentPattern(TextPattern.Pattern, out var p) && p is TextPattern tp)
            {
                // Read the VISIBLE viewport, not DocumentRange (which starts at the top
                // of the scrollback). The live spinner/timer is on-screen at the bottom,
                // so visible text is what actually changes while Claude works.
                //
                // CRITICAL: read the FULL range (-1), not a capped slice. A wide terminal's
                // viewport is ~7-8k chars; a 4000-char cap truncated BEFORE reaching the
                // bottom status line — so the ticking "(1m 16s…)" timer never appeared in
                // the snapshot and a busy terminal looked frozen → false idle flash.
                var sb = new System.Text.StringBuilder();
                foreach (var r in tp.GetVisibleRanges())
                    sb.Append(r.GetText(-1));
                string text = sb.ToString();
                return HashCode.Combine(text.Length, text.GetHashCode());
            }
        }
        catch { _textEl.TryRemove(hwnd, out _); } // stale element (window changed) → re-resolve next time
        return null;
    }

    private AutomationElement? ResolveTextElement(IntPtr hwnd)
    {
        if (_textEl.TryGetValue(hwnd, out var cached) && cached is not null)
        {
            // Validate the cached element still answers; if not, re-resolve.
            try { _ = cached.Current.IsOffscreen; return cached; }
            catch { _textEl.TryRemove(hwnd, out _); }
        }

        AutomationElement? root;
        try { root = AutomationElement.FromHandle(hwnd); }
        catch { return null; }
        if (root is null) return null;

        // A terminal window has SEVERAL text elements: the tab title (short, mostly
        // static) and the terminal BODY (the live buffer with the spinner/timer).
        // We must track the body, so pick the text element with the MOST text.
        AutomationElement? best = null;
        int bestLen = -1;
        try
        {
            var all = root.FindAll(TreeScope.Subtree,
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
            foreach (AutomationElement el in all)
            {
                try
                {
                    if (el.TryGetCurrentPattern(TextPattern.Pattern, out var p) && p is TextPattern tp)
                    {
                        int len = (tp.DocumentRange.GetText(2000) ?? "").Length;
                        if (len > bestLen) { bestLen = len; best = el; }
                    }
                }
                catch { /* skip flaky element */ }
            }
        }
        catch { /* FindAll failed */ }

        if (best is null && root.TryGetCurrentPattern(TextPattern.Pattern, out _)) best = root;
        _textEl[hwnd] = best;
        return best;
    }

    public void Forget(IntPtr hwnd) => _textEl.TryRemove(hwnd, out _);
}
