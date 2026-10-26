using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;

namespace PdfNotes.Editor.Tools;

/// <summary>Line settings besides color/width/transparency. <see cref="CurveControls"/>: 0 = automatic, 1 or 2 = forced.</summary>
public sealed record LineOptions(DashStyle Dash, bool StartArrow, bool EndArrow, int CurveControls)
{
    public static LineOptions Default { get; } = new(DashStyle.Solid, false, false, 0);

    public LineOptions Validated() => Enum.IsDefined(Dash) && CurveControls is >= 0 and <= 2 ? this : Default;
}

/// <summary>Color, transparency and height of glyph highlights. <see cref="Auto"/>: the glyph height; otherwise <see cref="Style"/>'s width in points.</summary>
public sealed record TextHighlightStyle(StrokeStyle Style, bool Auto)
{
    /// <summary>Spec 9.2: yellow, 60% transparency, automatic (glyph) height; manual default 12pt.</summary>
    public static TextHighlightStyle Default { get; } = new(new(new(0xFF, 0xE6, 0x00), 12, 60), true);

    public HighlightHeightMode Mode => Auto ? HighlightHeightMode.Auto : HighlightHeightMode.Manual;
    public double? ManualHeight => Auto ? null : Style.WidthPt;
}

/// <summary>Eraser diameters in page points (spec 7.4): area 20pt, stroke 10pt by default, both 2–100pt.</summary>
public static class EraserSizes
{
    public const double Min = 2, Max = 100, DefaultStroke = 10, DefaultArea = 20;

    public static double Default(EraserMode mode) => mode == EraserMode.Stroke ? DefaultStroke : DefaultArea;

    public static double Clamp(double value, EraserMode mode) => double.IsFinite(value) ? Math.Clamp(value, Min, Max) : Default(mode);
}
