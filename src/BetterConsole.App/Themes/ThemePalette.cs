using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterConsole.App.Themes;

/// <summary>
/// The colours of one theme. Every property becomes two application resources:
/// <c>Color.&lt;Name&gt;</c> (Color) and <c>Brush.&lt;Name&gt;</c> (SolidColorBrush), used by every
/// style through DynamicResource, so switching themes repaints the whole window at once.
/// A custom theme is a JSON file in the <c>themes</c> folder with any subset of these names.
/// </summary>
public sealed class ThemePalette
{
    public string Name { get; set; } = "Custom";
    /// <summary>The built-in theme a custom theme starts from.</summary>
    public string? Base { get; set; }
    public bool IsDark { get; set; } = true;

    // Window surfaces, from the back to the front.
    public string Background { get; set; } = "#16181D";
    public string Panel { get; set; } = "#1C1F26";
    public string Surface { get; set; } = "#232731";
    public string SurfaceHover { get; set; } = "#2B3040";
    public string SurfaceActive { get; set; } = "#333A4D";
    public string Border { get; set; } = "#2E3340";
    public string BorderStrong { get; set; } = "#3D4456";

    public string Text { get; set; } = "#E6E8EE";
    public string TextSecondary { get; set; } = "#AEB4C2";
    public string TextMuted { get; set; } = "#7C8496";

    public string Accent { get; set; } = "#4F8CFF";
    public string AccentHover { get; set; } = "#6A9DFF";
    public string AccentSoft { get; set; } = "#22355A";
    public string OnAccent { get; set; } = "#FFFFFF";

    public string Danger { get; set; } = "#F2555A";
    public string DangerSoft { get; set; } = "#46252A";
    public string Warning { get; set; } = "#F2B33D";
    public string WarningSoft { get; set; } = "#45391F";
    public string Success { get; set; } = "#3CCB7F";
    public string SuccessSoft { get; set; } = "#1E3F2F";
    public string Info { get; set; } = "#5CC8F2";

    public string ConsoleBackground { get; set; } = "#121418";
    public string ConsoleText { get; set; } = "#D6D9E0";
    public string ConsoleSelection { get; set; } = "#33507F";
    public string ConsoleCommand { get; set; } = "#8FB4FF";
    public string ConsoleApp { get; set; } = "#9C8CFF";
    public string InputBackground { get; set; } = "#1A1D24";

    public string ScrollThumb { get; set; } = "#3A4050";
    public string ScrollThumbHover { get; set; } = "#4C5468";

    public string ChartGrid { get; set; } = "#2A2F3B";
    public string Chart1 { get; set; } = "#4F8CFF";
    public string Chart2 { get; set; } = "#F2B33D";
    public string Chart3 { get; set; } = "#3CCB7F";
    public string Chart4 { get; set; } = "#E25CC6";
    public string Chart5 { get; set; } = "#5CC8F2";
    public string Chart6 { get; set; } = "#F2555A";

    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    public ThemePalette Clone() => (ThemePalette)MemberwiseClone();

    public static IEnumerable<PropertyInfo> ColorProperties() =>
        typeof(ThemePalette).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.Name is not (nameof(Name) or nameof(Base)));

    public static readonly ThemePalette Dark = new() { Name = "Dark", IsDark = true, IsBuiltIn = true };

    public static readonly ThemePalette Midnight = new()
    {
        Name = "Midnight", IsDark = true, IsBuiltIn = true,
        Background = "#0E1322", Panel = "#121A2E", Surface = "#18223B", SurfaceHover = "#1F2B4A", SurfaceActive = "#283659",
        Border = "#223051", BorderStrong = "#2F4170",
        Text = "#E4E9F7", TextSecondary = "#A8B3D1", TextMuted = "#7482A8",
        Accent = "#7AA2FF", AccentHover = "#94B5FF", AccentSoft = "#24345E", OnAccent = "#0B1020",
        ConsoleBackground = "#0A0F1C", ConsoleText = "#D3DAEE", ConsoleSelection = "#2E4A85", ConsoleCommand = "#9DB8FF", ConsoleApp = "#B39CFF",
        InputBackground = "#0F1628", ScrollThumb = "#2A3A63", ScrollThumbHover = "#3A4E80", ChartGrid = "#1E2A47",
        Chart1 = "#7AA2FF", Chart2 = "#FFC46B", Chart3 = "#5BE0A3", Chart4 = "#F07ADB", Chart5 = "#6FD6FF", Chart6 = "#FF6F7D",
    };

    public static readonly ThemePalette Graphite = new()
    {
        Name = "Graphite", IsDark = true, IsBuiltIn = true,
        Background = "#1A1A1A", Panel = "#202020", Surface = "#282828", SurfaceHover = "#323232", SurfaceActive = "#3B3B3B",
        Border = "#333333", BorderStrong = "#454545",
        Text = "#EDEDED", TextSecondary = "#B5B5B5", TextMuted = "#858585",
        Accent = "#2EC4A6", AccentHover = "#45D6B9", AccentSoft = "#1B3D37", OnAccent = "#0E1A17",
        ConsoleBackground = "#141414", ConsoleText = "#DADADA", ConsoleSelection = "#2C5148", ConsoleCommand = "#6FE3CB", ConsoleApp = "#C3A6FF",
        InputBackground = "#1D1D1D", ScrollThumb = "#404040", ScrollThumbHover = "#555555", ChartGrid = "#2E2E2E",
        Chart1 = "#2EC4A6", Chart2 = "#F2B33D", Chart3 = "#7FB2FF", Chart4 = "#E880C7", Chart5 = "#9AD45A", Chart6 = "#F2555A",
    };

    public static readonly ThemePalette Light = new()
    {
        Name = "Light", IsDark = false, IsBuiltIn = true,
        Background = "#F3F4F7", Panel = "#FBFBFD", Surface = "#FFFFFF", SurfaceHover = "#EEF1F6", SurfaceActive = "#E2E7F0",
        Border = "#DDE1E8", BorderStrong = "#C7CDD8",
        Text = "#1C2130", TextSecondary = "#4A5367", TextMuted = "#7A8397",
        Accent = "#2563EB", AccentHover = "#1D4FD7", AccentSoft = "#DCE7FD", OnAccent = "#FFFFFF",
        Danger = "#D92D35", DangerSoft = "#FBE3E4", Warning = "#B7791F", WarningSoft = "#FBF0D9", Success = "#15935A", SuccessSoft = "#DDF3E8", Info = "#0E7FB0",
        ConsoleBackground = "#FFFFFF", ConsoleText = "#1F2533", ConsoleSelection = "#BFD3FB", ConsoleCommand = "#1D4FD7", ConsoleApp = "#6D3FD6",
        InputBackground = "#FFFFFF", ScrollThumb = "#C9CED8", ScrollThumbHover = "#AEB5C3", ChartGrid = "#E6E9EF",
        Chart1 = "#2563EB", Chart2 = "#D97706", Chart3 = "#16A34A", Chart4 = "#C026D3", Chart5 = "#0891B2", Chart6 = "#DC2626",
    };

    public static IReadOnlyList<ThemePalette> BuiltIn { get; } = [Dark, Light, Midnight, Graphite];

    /// <summary>The palette of compact mode (not a theme to choose): flat, neutral greys and few tints.</summary>
    public static readonly ThemePalette Compact = new()
    {
        Name = "Compact", IsDark = true, IsBuiltIn = true,
        Background = "#1E1E1E", Panel = "#252526", Surface = "#2A2A2C", SurfaceHover = "#37373A", SurfaceActive = "#434346",
        Border = "#3A3A3C", BorderStrong = "#4E4E52",
        Text = "#D8D8D8", TextSecondary = "#B4B4B4", TextMuted = "#8A8A8A",
        Accent = "#3D8EF0", AccentHover = "#5A9FF2", AccentSoft = "#24405F", OnAccent = "#FFFFFF",
        Danger = "#F05252", DangerSoft = "#4A2323", Warning = "#D6A520", WarningSoft = "#433716", Success = "#43B95B", SuccessSoft = "#1F3A26", Info = "#4FB8EE",
        ConsoleBackground = "#181818", ConsoleText = "#D0D0D0", ConsoleSelection = "#24405F", ConsoleCommand = "#8CBEF5", ConsoleApp = "#B79CF0",
        InputBackground = "#1F1F1F", ScrollThumb = "#454548", ScrollThumbHover = "#58585C", ChartGrid = "#333335",
        Chart1 = "#3D8EF0", Chart2 = "#D6A520", Chart3 = "#43B95B", Chart4 = "#C77DD9", Chart5 = "#4FB8EE", Chart6 = "#F05252",
    };

    /// <summary>Compact mode when Windows' apps are light.</summary>
    public static readonly ThemePalette CompactLight = new()
    {
        Name = "Compact", IsDark = false, IsBuiltIn = true,
        Background = "#F2F2F2", Panel = "#F8F8F8", Surface = "#FFFFFF", SurfaceHover = "#E9E9E9", SurfaceActive = "#DCDCDC",
        Border = "#D6D6D6", BorderStrong = "#BEBEBE",
        Text = "#1E1E1E", TextSecondary = "#454545", TextMuted = "#737373",
        Accent = "#0063C7", AccentHover = "#0070DD", AccentSoft = "#D3E5F8", OnAccent = "#FFFFFF",
        Danger = "#C4281C", DangerSoft = "#FBE4E2", Warning = "#9A6200", WarningSoft = "#FBF0D4", Success = "#17803A", SuccessSoft = "#DDF2E2", Info = "#0A6CB0",
        ConsoleBackground = "#FFFFFF", ConsoleText = "#1E1E1E", ConsoleSelection = "#BFD8F5", ConsoleCommand = "#0451A5", ConsoleApp = "#7238C9",
        InputBackground = "#FFFFFF", ScrollThumb = "#C4C4C4", ScrollThumbHover = "#A9A9A9", ChartGrid = "#E4E4E4",
        Chart1 = "#0063C7", Chart2 = "#C27400", Chart3 = "#17803A", Chart4 = "#A934C2", Chart5 = "#0A8AB0", Chart6 = "#C4281C",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>Loads a custom theme; colours it leaves out come from its base theme.</summary>
    public static ThemePalette? Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var root = doc.RootElement;
            string? baseName = root.TryGetProperty("base", out var b) ? b.GetString() : null;
            var basePalette = BuiltIn.FirstOrDefault(t => t.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase)) ?? Dark;
            var p = basePalette.Clone();
            p.IsBuiltIn = false;
            p.Name = Path.GetFileNameWithoutExtension(path);
            p.Base = basePalette.Name;
            foreach (var prop in root.EnumerateObject())
            {
                var target = typeof(ThemePalette).GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (target == null || !target.CanWrite) continue;
                if (target.PropertyType == typeof(string) && prop.Value.ValueKind == JsonValueKind.String) target.SetValue(p, prop.Value.GetString());
                else if (target.PropertyType == typeof(bool) && prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) target.SetValue(p, prop.Value.GetBoolean());
            }
            return p;
        }
        catch
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
