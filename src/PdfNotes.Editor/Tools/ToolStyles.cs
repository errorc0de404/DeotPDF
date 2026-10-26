using PdfNotes.Core.Models;

namespace PdfNotes.Editor.Tools;

/// <summary>The style bar's view of annotations (spec 3.1, 9.4, D14d): color, width and transparency of what it edits.</summary>
public static class ToolStyles
{
    /// <summary>True for annotations the style bar edits together (strokes, lines, glyph marks, shapes, notes).</summary>
    public static bool HasStrokeStyle(Annotation a) =>
        a is StrokeAnnotation or LineAnnotation or PdfTextHighlight or ShapeAnnotation or PdfTextMarkup or StickyNote;

    /// <summary>The bar values of <paramref name="a"/>; a shape shows its fill or its border (<paramref name="shapeFill"/>).</summary>
    public static StrokeStyle StyleOf(Annotation a, bool shapeFill) => a switch
    {
        StrokeAnnotation s => StrokeStyle.Of(s),
        LineAnnotation l => StrokeStyle.Of(l),
        PdfTextHighlight h => StrokeStyle.Of(h),
        PdfTextMarkup m => StrokeStyle.Of(m),
        StickyNote n => new(n.Color, StrokeStyle.DefaultPen.WidthPt, 0), // a note has a color only
        ShapeAnnotation s => ShapeStroke(s.Style, shapeFill),
        _ => throw new ArgumentException("No stroke style.", nameof(a))
    };

    /// <summary>The fill or the border of a shape style as the color/width/transparency the bar shows (width: the border).</summary>
    public static StrokeStyle ShapeStroke(ShapeStyle s, bool fill) => fill
        ? new(s.FillColor, s.BorderWidthPt, Math.Round(Opacity.ToTransparencyPercent(s.FillOpacity), 6))
        : new(s.BorderColor, s.BorderWidthPt, Math.Round(Opacity.ToTransparencyPercent(s.BorderOpacity), 6));

    /// <summary>
    /// Writes a bar change back to the fill or the border. Picking a color for a missing fill or border turns it on;
    /// a shape always keeps a visible fill or border.
    /// </summary>
    public static ShapeStyle WithStroke(ShapeStyle s, StrokeStyle stroke, bool fill)
    {
        stroke = stroke.ClampFor(AnnotationKind.Shape);
        var next = fill
            ? s with { FillColor = stroke.Color, FillOpacity = stroke.Color != s.FillColor && s.FillOpacity == 0 ? 1 : stroke.Opacity, BorderWidthPt = stroke.WidthPt }
            : s with { BorderColor = stroke.Color, BorderOpacity = stroke.Opacity, BorderWidthPt = stroke.WidthPt,
                BorderEnabled = s.BorderEnabled || stroke.Color != s.BorderColor };
        return !next.BorderEnabled && next.FillOpacity == 0 ? next with { BorderEnabled = true } : next;
    }

    /// <summary>Which values differ across a multi-selection: the bar shows them empty (mixed) until one is set for all.</summary>
    public static (bool Color, bool Width, bool Transparency) Mixed(IReadOnlyList<StrokeStyle> styles) =>
        styles.Count < 2 ? default
        : (styles.Any(s => s.Color != styles[0].Color), styles.Any(s => Math.Abs(s.WidthPt - styles[0].WidthPt) > 1e-6),
            styles.Any(s => Math.Abs(s.TransparencyPercent - styles[0].TransparencyPercent) > 1e-6));

    public static double ClampWidth(double width, AnnotationKind kind)
    {
        var (min, max) = StrokeStyle.WidthRange(kind);
        return Math.Clamp(width, min, max);
    }
}
