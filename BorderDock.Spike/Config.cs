using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BorderDock.Spike;

/// <summary>
/// Persisted config: the saved window rules + the sticky center size.
/// Stored at %APPDATA%\BorderDock\config.json. Matching is by window TITLE
/// (substring) so many same-exe windows each get their own rule.
/// </summary>
internal sealed class Config
{
    public List<Rule> Rules { get; set; } = new();
    public int CenterWidth { get; set; }
    public int CenterHeight { get; set; }

    [JsonIgnore]
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BorderDock");
    [JsonIgnore]
    public static string Path_ => System.IO.Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Config Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(Path_), Opts) ?? new Config();
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

    public Size? CenterSize =>
        CenterWidth > 0 && CenterHeight > 0 ? new Size(CenterWidth, CenterHeight) : null;

    /// <summary>Resolve a live window to its saved rule. The STABLE key is the
    /// terminal's working directory (PathKey): a Claude terminal's title mutates
    /// constantly, but its project dir doesn't. We match on PathKey first (exact,
    /// unambiguous), and only fall back to title for windows with no shell behind
    /// them (VLC, browsers) or legacy rules saved before path-keying existed.</summary>
    public Rule? Resolve(string? pathKey, string title)
    {
        if (!string.IsNullOrEmpty(pathKey))
        {
            var byPath = Rules.FirstOrDefault(r => r.MatchesPath(pathKey));
            if (byPath is not null) return byPath;
        }
        return BestMatch(title);
    }

    /// <summary>Legacy/title fallback: longest matching TitleMatch wins.</summary>
    public Rule? BestMatch(string title) =>
        Rules.Where(r => r.Matches(title))
             .OrderByDescending(r => r.TitleMatch.Length)
             .FirstOrDefault();

    public sealed class Rule
    {
        public string? PathKey { get; set; }            // the terminal's working dir = the STABLE key
        public string TitleMatch { get; set; } = "";   // fallback key + display: the window title at add time
        public string? DisplayName { get; set; }       // custom chip label; null = folder name, else title
        public int ColorArgb { get; set; }
        public float FontSize { get; set; } = 9f;
        public bool AlwaysOnTop { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }

        [JsonIgnore] public Color Color => Color.FromArgb(ColorArgb);
        [JsonIgnore] public float SafeFontSize => FontSize is >= 6f and <= 48f ? FontSize : 9f;
        [JsonIgnore] public Native.RECT Home =>
            new() { Left = Left, Top = Top, Right = Right, Bottom = Bottom };

        public void SetHome(in Native.RECT r) { Left = r.Left; Top = r.Top; Right = r.Right; Bottom = r.Bottom; }

        /// <summary>Case-insensitive substring match against a live window title.</summary>
        public bool Matches(string title) =>
            !string.IsNullOrEmpty(TitleMatch) &&
            title.Contains(TitleMatch, StringComparison.OrdinalIgnoreCase);

        /// <summary>Exact (normalized) match against a terminal's working directory.</summary>
        public bool MatchesPath(string pathKey) =>
            !string.IsNullOrEmpty(PathKey) &&
            string.Equals(ProcessInfo.Normalize(PathKey), ProcessInfo.Normalize(pathKey),
                          StringComparison.OrdinalIgnoreCase);
    }
}
