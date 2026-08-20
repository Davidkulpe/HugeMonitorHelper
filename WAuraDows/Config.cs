using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WAuraDows;

/// <summary>
/// Persisted config: the saved window rules + the sticky center size.
/// Stored at %APPDATA%\WAuraDows\config.json. Rules are keyed by the project
/// directory behind a window, falling back to title for pathless windows.
/// </summary>
internal sealed class Config
{
    public List<Rule> Rules { get; set; } = new();
    public int CenterWidth { get; set; }
    public int CenterHeight { get; set; }

    [JsonIgnore]
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WAuraDows");
    [JsonIgnore]
    public static string Path_ => System.IO.Path.Combine(Dir, "config.json");

    /// <summary>Where the config lived back when the app was called BorderDock.</summary>
    [JsonIgnore]
    private static string LegacyPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "BorderDock", "config.json");

    /// <summary>Carry a pre-rename config across to the new location, once. Copies
    /// rather than moves, so the old file survives untouched if anything goes wrong.</summary>
    private static void MigrateLegacyConfig()
    {
        try
        {
            if (File.Exists(Path_) || !File.Exists(LegacyPath)) return;
            Directory.CreateDirectory(Dir);
            File.Copy(LegacyPath, Path_);
        }
        catch { /* best-effort; worst case is starting with no rules */ }
    }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Config Load()
    {
        try
        {
            MigrateLegacyConfig();
            if (File.Exists(Path_))
            {
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path_), Opts) ?? new Config();
                cfg.Migrate();
                return cfg;
            }
        }
        catch { /* corrupt/missing → start empty (eng-review: don't crash) */ }
        return new Config();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* best-effort; a failed save shouldn't take down the app */ }
    }

    /// <summary>Smallest center slot we'll ever hand out. A degenerate rect captured
    /// while a window was mid-animation used to be tracked, saved, and reloaded as
    /// the center size — a 2x2 pixel center slot that shrank every summoned window
    /// to nothing. Anything below this is treated as "no saved size" and falls back
    /// to half the monitor.</summary>
    public const int MinCenterPx = 200;

    public static bool IsSaneCenter(Size s) => s.Width >= MinCenterPx && s.Height >= MinCenterPx;

    /// <summary>The saved center size, or null when absent OR nonsense.</summary>
    [JsonIgnore]   // computed — without this the serializer writes a junk {IsEmpty,...} blob
    public Size? CenterSize =>
        IsSaneCenter(new Size(CenterWidth, CenterHeight)) ? new Size(CenterWidth, CenterHeight) : null;

    /// <summary>One-time cleanups applied after load.</summary>
    private void Migrate()
    {
        // Drop a poisoned center size rather than carrying it forward.
        if (!IsSaneCenter(new Size(CenterWidth, CenterHeight))) { CenterWidth = CenterHeight = 0; }

        foreach (var r in Rules)
        {
            // A path-keyed rule has no business carrying a TitleMatch. What got saved
            // there was Claude's live task text at add time ("* Claude Code"), and
            // substring-matching that against every new window made one project's rule
            // adopt any terminal that happened to show similar text. The path is the key.
            if (!string.IsNullOrEmpty(r.PathKey)) r.TitleMatch = "";
        }
    }

    /// <summary>Resolve a live window to its saved rule. The STABLE key is the
    /// terminal's working directory (PathKey): a Claude terminal's title mutates
    /// constantly, but its project dir doesn't.
    ///
    /// Title matching is ONLY for rules that have no path at all — windows with no
    /// shell behind them (VLC, browsers) and legacy rules. A path-keyed rule that
    /// fails to resolve by path must NOT fall through to guessing by title: that is
    /// how one project's border ends up on another project's terminal.</summary>
    public Rule? Resolve(string? pathKey, string title)
    {
        if (!string.IsNullOrEmpty(pathKey))
        {
            var byPath = Rules.FirstOrDefault(r => r.MatchesPath(pathKey));
            if (byPath is not null) return byPath;
        }
        return BestMatch(title);
    }

    /// <summary>Title fallback: pathless rules only, longest match wins.</summary>
    public Rule? BestMatch(string title) =>
        Rules.Where(r => string.IsNullOrEmpty(r.PathKey) && r.Matches(title))
             .OrderByDescending(r => r.TitleMatch.Length)
             .FirstOrDefault();

    public sealed class Rule
    {
        public string? PathKey { get; set; }            // the terminal's working dir = the STABLE key
        public string TitleMatch { get; set; } = "";   // fallback key for PATHLESS rules only (VLC, browsers)
        public string? DisplayName { get; set; }       // custom chip label; null = folder name, else title
        public int ColorArgb { get; set; }
        public float FontSize { get; set; } = 9f;
        public bool AlwaysOnTop { get; set; }
        /// <summary>Stamp the label + color onto this window's TASKBAR button too,
        /// so it stays identifiable while collapsed. Defaults on; rules saved before
        /// this existed have no such key, so they keep the initializer's true.</summary>
        public bool TaskbarTag { get; set; } = true;
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }

        [JsonIgnore] public Color Color => Color.FromArgb(ColorArgb);
        [JsonIgnore] public float SafeFontSize => FontSize is >= 6f and <= 48f ? FontSize : 9f;
        [JsonIgnore] public Native.RECT Home =>
            new() { Left = Left, Top = Top, Right = Right, Bottom = Bottom };

        public void SetHome(in Native.RECT r) { Left = r.Left; Top = r.Top; Right = r.Right; Bottom = r.Bottom; }

        /// <summary>Shortest TitleMatch we'll act on. A two-character fragment is a
        /// substring of half the windows on the desktop.</summary>
        public const int MinTitleMatch = 4;

        /// <summary>Case-insensitive substring match against a live window title.</summary>
        public bool Matches(string title) =>
            TitleMatch.Length >= MinTitleMatch &&
            title.Contains(TitleMatch, StringComparison.OrdinalIgnoreCase);

        /// <summary>Exact (normalized) match against a terminal's working directory.</summary>
        public bool MatchesPath(string pathKey) =>
            !string.IsNullOrEmpty(PathKey) &&
            string.Equals(ProcessInfo.Normalize(PathKey), ProcessInfo.Normalize(pathKey),
                          StringComparison.OrdinalIgnoreCase);
    }
}
