using PdfNotes.Core.Models;

namespace PdfNotes.Editor.Tools;

/// <summary>Canvas tools (spec 4.3 ToolState).</summary>
public enum ToolKind { Select, Pen, FreeHighlight, Text, Line, Curve, Lasso, StrokeEraser, AreaEraser, Shape, Note }

/// <summary>
/// Paint settings of a freehand tool or a selected stroke. <see cref="TransparencyPercent"/> is the UI value
/// (0 = opaque, 100 = invisible); the model stores opacity = 1 - T/100 (spec 15).
/// </summary>
public sealed record StrokeStyle(RgbColor Color, double WidthPt, double TransparencyPercent)
{
    public double Opacity => Core.Models.Opacity.FromTransparencyPercent(TransparencyPercent);

    /// <summary>Spec 7.2: 2pt, opaque, black.</summary>
    public static StrokeStyle DefaultPen { get; } = new(RgbColor.Black, InputLimits.DefaultPenWidthPt, 0);

    /// <summary>Spec 9.2: yellow, 14pt, 60% transparency.</summary>
    public static StrokeStyle DefaultHighlighter { get; } = new(new(0xFF, 0xE6, 0x00), InputLimits.DefaultFreeHighlightWidthPt, 60);

    public static StrokeStyle Of(StrokeAnnotation stroke) =>
        new(stroke.Color, stroke.WidthPt, Math.Round(Core.Models.Opacity.ToTransparencyPercent(stroke.Opacity), 6));

    /// <summary>A glyph highlight's style; its width is the manual height, or the first line's glyph height when automatic.</summary>
    public static StrokeStyle Of(PdfTextHighlight h) =>
        new(h.Color, h.ManualHeightPt ?? Math.Round(h.Quads[0].UpperLeft.DistanceTo(h.Quads[0].LowerLeft), 1),
            Math.Round(Core.Models.Opacity.ToTransparencyPercent(h.Opacity), 6));

    /// <summary>Spec 24.8: underline/strikeout red, 1.5pt, opaque.</summary>
    public static StrokeStyle DefaultMarkup { get; } = new(new(0xE5, 0x39, 0x35), 1.5, 0);

    public static StrokeStyle Of(PdfTextMarkup m) =>
        new(m.Color, m.WidthPt, Math.Round(Core.Models.Opacity.ToTransparencyPercent(m.Opacity), 6));

    public static StrokeStyle Of(LineAnnotation line) =>
        new(line.Color, line.WidthPt, Math.Round(Core.Models.Opacity.ToTransparencyPercent(line.Opacity), 6));

    /// <summary>UI width range: pen and line 0.5–40pt, highlighter 2–80pt.</summary>
    public static (double Min, double Max) WidthRange(AnnotationKind kind) => kind switch
    {
        AnnotationKind.FreeHighlight => (InputLimits.MinFreeHighlightWidthPt, InputLimits.MaxFreeHighlightWidthPt),
        AnnotationKind.PdfTextHighlight => (InputLimits.MinManualHighlightHeightPt, InputLimits.MaxManualHighlightHeightPt),
        AnnotationKind.TextMarkup => (ModelValidator.MinMarkupWidthPt, ModelValidator.MaxMarkupWidthPt),
        _ => (InputLimits.MinPenWidthPt, InputLimits.MaxPenWidthPt)
    };

    public StrokeStyle ClampFor(AnnotationKind kind)
    {
        var (min, max) = WidthRange(kind);
        double width = double.IsFinite(WidthPt) ? Math.Clamp(WidthPt, min, max) : (kind == AnnotationKind.FreeHighlight ? DefaultHighlighter : DefaultPen).WidthPt;
        double transparency = double.IsFinite(TransparencyPercent) ? Math.Clamp(TransparencyPercent, 0, 100) : 0;
        return this with { WidthPt = width, TransparencyPercent = transparency };
    }
}

public static class ColorPalette
{
    /// <summary>Basic palette shown for every freehand tool (spec 7.1, 9.1).</summary>
    public static IReadOnlyList<RgbColor> Basic { get; } =
    [
        new(0x00, 0x00, 0x00), new(0x60, 0x60, 0x60), new(0xE5, 0x39, 0x35), new(0xFB, 0x8C, 0x00), new(0xFF, 0xE6, 0x00),
        new(0x43, 0xA0, 0x47), new(0x1E, 0x88, 0xE5), new(0x28, 0x35, 0x93), new(0x8E, 0x24, 0xAA), new(0xFF, 0xFF, 0xFF),
    ];

    /// <summary>User colors (spec 3.4, D14g): a fixed list of the colors added with +, at most this many; none is pushed out.</summary>
    public const int MaxUser = 12;

    /// <summary>The user colors with <paramref name="added"/> at the end; unchanged when it is there already or the list is full.</summary>
    public static List<RgbColor> Add(IEnumerable<RgbColor> user, RgbColor added)
    {
        var list = user.ToList();
        if (!list.Contains(added) && list.Count < MaxUser) list.Add(added);
        return list;
    }

    /// <summary>The user colors without <paramref name="removed"/>, in the same order.</summary>
    public static List<RgbColor> Remove(IEnumerable<RgbColor> recent, RgbColor removed) => [.. recent.Where(c => c != removed)];
}
