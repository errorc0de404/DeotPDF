using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Tools;

namespace PdfNotes.Editor.Services;

/// <summary>
/// Per-user app settings (spec 18) in %LOCALAPPDATA%\PdfNotes\settings.json. Not document state: changing them
/// never marks a document dirty or enters Undo. A missing or unreadable file falls back to defaults.
/// </summary>
public enum PageNumberSize { Small, Normal, Large }

/// <summary>The theme setting (spec 3.2, 2026-10-13): follow Windows (default), light or dark.</summary>
public enum ThemeChoice { System, Light, Dark }

/// <summary>Accent colors the user can choose (spec 3.1). Blue is the default.</summary>
public enum AccentColor { Blue, Green, Purple, Orange, Red, Gray }

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public TextRun DefaultText { get; set; } = PdfNotes.Core.Commands.TextEditing.Default;
    public TextRun?[] TextSlots { get; set; } = new TextRun?[5];

    /// <summary>Box fill/border of the last styled text box; new boxes start with it (spec 6.2, 2026-10-13 user request).</summary>
    public TextBoxSettings TextBox { get; set; } = TextBoxSettings.From(TextBoxStyle.Default);

    public sealed class TextBoxSettings
    {
        public string BackgroundColor { get; set; } = "#FFFFFF";
        public double BackgroundOpacity { get; set; }
        public bool BorderEnabled { get; set; }
        public string BorderColor { get; set; } = "#000000";
        public double BorderOpacity { get; set; } = 1;

        public static TextBoxSettings From(TextBoxStyle s) => new()
        {
            BackgroundColor = s.BackgroundColor.ToHex(), BackgroundOpacity = s.BackgroundOpacity, BorderEnabled = s.BorderEnabled,
            BorderColor = s.BorderColor.ToHex(), BorderOpacity = s.BorderOpacity
        };

        /// <summary>Validated style; damaged values fall back to the default box style.</summary>
        public TextBoxStyle ToStyle() =>
            RgbColor.TryParse(BackgroundColor, out var fill) && RgbColor.TryParse(BorderColor, out var border) &&
            double.IsFinite(BackgroundOpacity) && double.IsFinite(BorderOpacity)
                ? new(fill, Math.Clamp(BackgroundOpacity, 0, 1), BorderEnabled, border, Math.Clamp(BorderOpacity, 0, 1))
                : TextBoxStyle.Default;
    }

    public ViewMode ViewMode { get; set; } = ViewMode.Continuous;

    /// <summary>Recent documents, most recent first (spec 24.1): full paths, at most <see cref="MaxRecentFiles"/>.</summary>
    public List<string> RecentFiles { get; set; } = [];
    public const int MaxRecentFiles = 10;

    /// <summary>Where each document was left (spec 24.1): most recent first, at most <see cref="MaxPositions"/> documents.</summary>
    public List<DocumentPosition> Positions { get; set; } = [];
    public const int MaxPositions = 50;

    /// <summary>Page index, canonical point at the view center and zoom of a document by its full path.</summary>
    public sealed class DocumentPosition
    {
        public string Path { get; set; } = "";
        public int Page { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Zoom { get; set; } = 1;
    }

    /// <summary>Main window size, position and maximized state (spec 24.1); null until the window was closed once.</summary>
    public WindowSettings? Window { get; set; }

    public sealed class WindowSettings
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    /// <summary>Left page panel shown (spec 3.2-5: visible by default).</summary>
    public bool PagePanelVisible { get; set; } = true;

    /// <summary>Outline above the page list (spec 24.4): off by default, its height in DIP.</summary>
    public bool OutlineVisible { get; set; }
    public double OutlineHeight { get; set; } = 220;

    /// <summary>Last pen/highlighter settings and recent colors (spec 18: recent tool settings).</summary>
    public StyleSettings Pen { get; set; } = StyleSettings.From(StrokeStyle.DefaultPen);
    public StyleSettings Highlighter { get; set; } = StyleSettings.From(StrokeStyle.DefaultHighlighter);
    public List<string> RecentColors { get; set; } = [];

    /// <summary>Line and curve tools share one style (spec 8.1).</summary>
    public StyleSettings Line { get; set; } = StyleSettings.From(StrokeStyle.DefaultPen);
    public LineOptions LineOptions { get; set; } = LineOptions.Default;

    /// <summary>
    /// Highlighter (spec 9.1, D14g): <see cref="Highlighter"/> is the highlight kind's color, width and transparency for
    /// free strokes and glyph highlights alike; <see cref="HighlighterAuto"/> makes glyph highlights the glyph height
    /// (off: the width is their height). The separate glyph highlight tool's settings of before D14g are dropped.
    /// </summary>
    public bool HighlighterAuto { get; set; } = true;

    /// <summary>The eraser the toolbar button selects, and each eraser's diameter in points (spec 7.4).</summary>
    public ToolKind Eraser { get; set; } = ToolKind.AreaEraser;
    public double StrokeEraserPt { get; set; } = EraserSizes.DefaultStroke;
    public double AreaEraserPt { get; set; } = EraserSizes.DefaultArea;

    /// <summary>Shape tool (D11): the shape drawn next, the shape group button's last tool (line, curve or shape) and the style.</summary>
    public ShapeKind ShapeKind { get; set; } = ShapeKind.Rectangle;
    public ToolKind ShapeGroupTool { get; set; } = ToolKind.Line;
    public ShapeSettings Shape { get; set; } = ShapeSettings.From(ShapeStyle.Default);

    public sealed class ShapeSettings
    {
        public string FillColor { get; set; } = "#9EC5F0";
        public double FillOpacity { get; set; } = 0.7;
        public bool BorderEnabled { get; set; } = true;
        public string BorderColor { get; set; } = "#1E4F91";
        public double BorderWidthPt { get; set; } = 2;
        public double BorderOpacity { get; set; } = 1;
        public DashStyle BorderDash { get; set; }

        public static ShapeSettings From(ShapeStyle s) => new()
        {
            FillColor = s.FillColor.ToHex(), FillOpacity = s.FillOpacity, BorderEnabled = s.BorderEnabled, BorderColor = s.BorderColor.ToHex(),
            BorderWidthPt = s.BorderWidthPt, BorderOpacity = s.BorderOpacity, BorderDash = s.BorderDash
        };

        /// <summary>Validated style; damaged values fall back to the default shape style.</summary>
        public ShapeStyle ToStyle()
        {
            var d = ShapeStyle.Default;
            if (!RgbColor.TryParse(FillColor, out var fill) || !RgbColor.TryParse(BorderColor, out var border) || !Enum.IsDefined(BorderDash) ||
                !double.IsFinite(FillOpacity) || !double.IsFinite(BorderOpacity) || !double.IsFinite(BorderWidthPt)) return d;
            var style = new ShapeStyle(fill, Math.Clamp(FillOpacity, 0, 1), BorderEnabled, border,
                Math.Clamp(BorderWidthPt, InputLimits.MinLineWidthPt, InputLimits.MaxLineWidthPt), Math.Clamp(BorderOpacity, 0, 1), BorderDash);
            return !style.BorderEnabled && style.FillOpacity == 0 ? style with { BorderEnabled = true } : style;
        }
    }

    /// <summary>Settings window (spec 18.1): middle button erases while held in the pen and highlighter (7.5), on by default.</summary>
    public bool QuickErase { get; set; } = true;

    /// <summary>Settings window 필기 (spec 5.3, 18.1, D14g): drag selections to another page in the continuous view, off by default.</summary>
    public bool PageDrag { get; set; }

    /// <summary>Settings window 화면 (spec 18.1, D13d): theme (follow Windows by default), accent color (blue) and the page numbers of 4.5 (on).</summary>
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    public AccentColor Accent { get; set; } = AccentColor.Blue;
    public bool ShowPageNumbers { get; set; } = true;

    /// <summary>Switch between the 필기 and 페이지 ribbon tabs with the work (spec 3.2, 2026-10-14), on by default.</summary>
    public bool AutoSwitchTabs { get; set; } = true;

    /// <summary>Size of the page numbers on the pages (spec 4.5, 2026-10-15): small 11 / normal 14 (default) / large 18 DIP.</summary>
    public PageNumberSize PageNumberSize { get; set; } = PageNumberSize.Normal;

    /// <summary>Reading dark page (spec 24.12): page display inverted, off by default; never affects saved files.</summary>
    public bool DarkPages { get; set; }

    /// <summary>Pen favorites group on the 필기 tab (spec 24.7), shown by default; the quick menu row stays either way.</summary>
    public bool ShowFavorites { get; set; } = true;

    /// <summary>UI language pack (spec 18.1, D14e): the built-in Korean or a pack found in <see cref="LanguageFolders"/>; read at start.</summary>
    public string Language { get; set; } = PdfNotes.Editor.Localization.Strings.BuiltInLanguage;

    /// <summary>
    /// Check for a new version at start (roadmap 1.1, <see cref="Services.UpdateCheck"/>): null until the user was asked
    /// (on the first start of a release build), then their answer. <see cref="UpdateCheckedAt"/> is the last check (UTC).
    /// </summary>
    public bool? CheckUpdates { get; set; }
    public DateTime? UpdateCheckedAt { get; set; }

    /// <summary>Where language packs are looked for: the app folder's <c>Languages</c>, then the settings folder's (the user's copy wins).</summary>
    public static string[] LanguageFolders => [Path.Combine(AppContext.BaseDirectory, "Languages"), Path.Combine(Path.GetDirectoryName(DefaultPath)!, "Languages")];

    /// <summary>
    /// The highlighter kind (spec 9.1: null = highlight, underline or strikeout) and the underline and strikeout styles,
    /// each remembered on its own (red 1.5pt opaque by default).
    /// </summary>
    public TextMarkupKind? MarkKind { get; set; }
    public StyleSettings? Underline { get; set; }
    public StyleSettings? StrikeOut { get; set; }

    /// <summary>The one markup style of before D14g: read once to start both kinds from it, never written again.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StyleSettings? Markup { get; set; }

    /// <summary>Color of new sticky notes (spec 24.9).</summary>
    public string NoteColor { get; set; } = "#FFD54F";

    /// <summary>The highlighter's 글자에 맞추기 toggle (spec 9.1): highlight strokes that start on text become glyph highlights, on by default.</summary>
    public bool HighlighterSnap { get; set; } = true;

    /// <summary>Four pen favorites (spec 24.7); null is an empty slot.</summary>
    public FavoriteSettings?[] Favorites { get; set; } =
    [
        new() { Tool = ToolKind.Pen, Style = StyleSettings.From(new StrokeStyle(new(0, 0, 0), 2, 0)) },
        new() { Tool = ToolKind.Pen, Style = StyleSettings.From(new StrokeStyle(new(0xE5, 0x39, 0x35), 1, 0)) },
        new() { Tool = ToolKind.FreeHighlight, Style = StyleSettings.From(StrokeStyle.DefaultHighlighter) },
        null,
    ];

    /// <summary>A favorite: the pen or the highlighter with its kind (spec 24.7, D14g: highlight, underline or strikeout) and style.</summary>
    public sealed class FavoriteSettings
    {
        public ToolKind Tool { get; set; } = ToolKind.Pen;
        public TextMarkupKind? MarkKind { get; set; }
        public StyleSettings Style { get; set; } = StyleSettings.From(StrokeStyle.DefaultPen);
    }

    /// <summary>Automatic backup (spec 18.2): on by default, every 5 minutes, into the backup folder, 10 files per document.</summary>
    public bool AutoBackup { get; set; } = true;
    public int BackupIntervalMinutes { get; set; } = 5;
    public BackupMode BackupMode { get; set; } = BackupMode.Folder;

    /// <summary>Backup folder; null or empty uses <see cref="DefaultBackupFolder"/>.</summary>
    public string? BackupFolder { get; set; }
    public int BackupKeep { get; set; } = 10;

    public static readonly int[] BackupIntervals = [1, 3, 5, 10, 15, 30];
    public const int MinBackupKeep = 1, MaxBackupKeep = 50;

    /// <summary>Backups go next to the settings (%LOCALAPPDATA%\PdfNotes\Backups).</summary>
    public static string DefaultBackupFolder => Path.Combine(Path.GetDirectoryName(DefaultPath)!, "Backups");

    [JsonIgnore] public string EffectiveBackupFolder => string.IsNullOrWhiteSpace(BackupFolder) ? DefaultBackupFolder : BackupFolder;

    public sealed class StyleSettings
    {
        public string Color { get; set; } = "#000000";
        public double WidthPt { get; set; }
        public double Transparency { get; set; }

        public static StyleSettings From(StrokeStyle style) =>
            new() { Color = style.Color.ToHex(), WidthPt = style.WidthPt, Transparency = style.TransparencyPercent };

        /// <summary>Validated style; damaged values fall back to <paramref name="fallback"/>.</summary>
        public StrokeStyle ToStyle(StrokeStyle fallback, AnnotationKind kind) =>
            (RgbColor.TryParse(Color, out var color) ? new StrokeStyle(color, WidthPt, Transparency) : fallback).ClampFor(kind);
    }

    private static bool ValidText(TextRun? r) => r is not null && !string.IsNullOrWhiteSpace(r.FontFamily) && r.FontFamily.Length <= 256 && double.IsFinite(r.SizePt) && r.SizePt >= 1 && r.SizePt <= 400;

    public IReadOnlyList<RgbColor> GetRecentColors() =>
        (RecentColors ?? []).Select(text => RgbColor.TryParse(text, out var c) ? c : (RgbColor?)null)
            .OfType<RgbColor>().Distinct().Take(ColorPalette.MaxUser).ToList();

    /// <summary>Settings folder override for tests and validation runs (environment variable PDFNOTES_SETTINGS_DIR).</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetEnvironmentVariable("PDFNOTES_SETTINGS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfNotes"),
        "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new();
            if (!Enum.IsDefined(settings.ViewMode)) settings.ViewMode = ViewMode.Continuous;
            settings.Pen ??= StyleSettings.From(StrokeStyle.DefaultPen);
            settings.Highlighter ??= StyleSettings.From(StrokeStyle.DefaultHighlighter);
            settings.RecentColors ??= [];
            if (!Enum.IsDefined(settings.PageNumberSize)) settings.PageNumberSize = PageNumberSize.Normal;
            settings.Underline ??= settings.Markup ?? StyleSettings.From(StrokeStyle.DefaultMarkup);
            settings.StrikeOut ??= settings.Markup ?? StyleSettings.From(StrokeStyle.DefaultMarkup);
            settings.Markup = null;
            if (settings.MarkKind is { } mk && !Enum.IsDefined(mk)) settings.MarkKind = null;
            if (!RgbColor.TryParse(settings.NoteColor ?? "", out _)) settings.NoteColor = "#FFD54F";
            settings.Favorites = Enumerable.Range(0, 4).Select(i => settings.Favorites is { } f && i < f.Length && f[i] is { Style: not null } slot &&
                slot.Tool is ToolKind.Pen or ToolKind.FreeHighlight ? slot : null).ToArray();
            foreach (var slot in settings.Favorites.OfType<FavoriteSettings>())
                if (slot.Tool == ToolKind.Pen || slot.MarkKind is { } fk && !Enum.IsDefined(fk)) slot.MarkKind = null;
            if (!double.IsFinite(settings.OutlineHeight) || settings.OutlineHeight < 60 || settings.OutlineHeight > 4000) settings.OutlineHeight = 220;
            settings.RecentFiles = (settings.RecentFiles ?? []).Where(p => p is { Length: > 0 and < 1024 } && Path.IsPathFullyQualified(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRecentFiles).ToList();
            settings.Positions = (settings.Positions ?? []).Where(p => p is { Path.Length: > 0 and < 1024, Page: >= 0 } && double.IsFinite(p.X) && double.IsFinite(p.Y) &&
                double.IsFinite(p.Zoom) && p.Zoom > 0 && Path.IsPathFullyQualified(p.Path)).Take(MaxPositions).ToList();
            settings.Line ??= StyleSettings.From(StrokeStyle.DefaultPen);
            settings.LineOptions = settings.LineOptions?.Validated() ?? LineOptions.Default;
            settings.Shape ??= ShapeSettings.From(ShapeStyle.Default);
            settings.TextBox ??= TextBoxSettings.From(TextBoxStyle.Default);
            if (!Enum.IsDefined(settings.ShapeKind) || settings.ShapeKind == ShapeKind.Polyline) settings.ShapeKind = ShapeKind.Rectangle;
            if (settings.ShapeGroupTool is not (ToolKind.Line or ToolKind.Curve or ToolKind.Shape)) settings.ShapeGroupTool = ToolKind.Line;
            if (settings.Eraser is not (ToolKind.StrokeEraser or ToolKind.AreaEraser)) settings.Eraser = ToolKind.AreaEraser;
            settings.StrokeEraserPt = EraserSizes.Clamp(settings.StrokeEraserPt, Core.Commands.EraserMode.Stroke);
            settings.AreaEraserPt = EraserSizes.Clamp(settings.AreaEraserPt, Core.Commands.EraserMode.Area);
            settings.DefaultText = ValidText(settings.DefaultText) ? settings.DefaultText : PdfNotes.Core.Commands.TextEditing.Default;
            if (!BackupIntervals.Contains(settings.BackupIntervalMinutes)) settings.BackupIntervalMinutes = 5;
            if (!Enum.IsDefined(settings.BackupMode)) settings.BackupMode = BackupMode.Folder;
            if (!Enum.IsDefined(settings.Theme)) settings.Theme = ThemeChoice.System;
            if (!Enum.IsDefined(settings.Accent)) settings.Accent = AccentColor.Blue;
            settings.BackupKeep = Math.Clamp(settings.BackupKeep, MinBackupKeep, MaxBackupKeep);
            if (settings.BackupFolder is { } folder && (folder.Length > 1024 || !Path.IsPathFullyQualified(folder))) settings.BackupFolder = null;
            settings.TextSlots = Enumerable.Range(0, 5).Select(i => settings.TextSlots is { } slots && i < slots.Length && ValidText(slots[i]) ? slots[i] : null).ToArray();
            settings.written = settings.ToJson();
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>Writes atomically (temp file + move). Failures are ignored: settings are a convenience.</summary>
    public void Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var mine = ToJson();
            string temp = path + ".tmp";
            File.WriteAllText(temp, Merge(mine, written, ReadJson(path)).ToJsonString(Options));
            File.Move(temp, path, overwrite: true);
            written = mine;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>These settings as they were last read or written by this window; null for settings never read from a file.</summary>
    private System.Text.Json.Nodes.JsonObject? written;

    private System.Text.Json.Nodes.JsonObject ToJson() => JsonSerializer.SerializeToNode(this, Options)!.AsObject();

    private static System.Text.Json.Nodes.JsonObject? ReadJson(string path)
    {
        try { return File.Exists(path) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// What a save writes when several windows share the file (spec 4.6, D14g): the items this window changed since it
    /// last read or wrote them, and the file's current value of every other item, so another window's change is kept.
    /// </summary>
    internal static System.Text.Json.Nodes.JsonObject Merge(System.Text.Json.Nodes.JsonObject mine, System.Text.Json.Nodes.JsonObject? before,
        System.Text.Json.Nodes.JsonObject? onDisk)
    {
        var merged = (System.Text.Json.Nodes.JsonObject)mine.DeepClone();
        if (before is null || onDisk is null) return merged;
        foreach (var (key, value) in onDisk)
            if (mine.ContainsKey(key) && System.Text.Json.Nodes.JsonNode.DeepEquals(mine[key], before[key])) merged[key] = value?.DeepClone();
        return merged;
    }
}
