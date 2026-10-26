using System.Collections.Immutable;
using PdfNotes.Core.Geometry;

namespace PdfNotes.Core.Models;

/// <summary>Every annotation kind of spec 5.1. The schema tag of each kind is fixed in SchemaV1.</summary>
public enum AnnotationKind { TextBox, Ink, FreeHighlight, Line, PdfTextHighlight, Image, Shape, TextMarkup, Note }

/// <summary>
/// Common annotation data. Geometry is in the annotation's local space and <see cref="Transform"/> maps it to
/// canonical page points. Normalization policy (v1): Transform is always rigid (rotation + translation).
/// Scaling is baked into geometry, stroke widths and font sizes when the edit is committed, so a scale is never
/// applied twice. PDF text highlights always use the identity transform.
/// </summary>
public abstract record Annotation(Guid Id, bool Locked, Affine Transform)
{
    public abstract AnnotationKind Kind { get; }
}

/// <summary>
/// Freehand polyline. Points are the committed (smoothed) centerline in local space. A piece left by the area
/// eraser (spec 7.4, schema v3) has no centerline: <see cref="Outline"/> holds its painted region instead.
/// </summary>
public abstract record StrokeAnnotation(Guid Id, bool Locked, Affine Transform, ImmutableArray<Point2> Points,
    double WidthPt, RgbColor Color, double Opacity) : Annotation(Id, Locked, Transform)
{
    /// <summary>
    /// Closed rings in local space filled with the even-odd rule, or default. When set, <see cref="Points"/> is empty
    /// and <see cref="WidthPt"/> only records the width the stroke was drawn with; it no longer changes the shape.
    /// </summary>
    public ImmutableArray<ImmutableArray<Point2>> Outline { get; init; }

    public bool IsOutlined => !Outline.IsDefaultOrEmpty;
}

public sealed record InkStroke(Guid Id, bool Locked, Affine Transform, ImmutableArray<Point2> Points,
    double WidthPt, RgbColor Color, double Opacity)
    : StrokeAnnotation(Id, Locked, Transform, Points, WidthPt, Color, Opacity)
{
    public override AnnotationKind Kind => AnnotationKind.Ink;
}

/// <summary>Freehand highlighter. A single stroke is composited once with its opacity (no self-overlap darkening).</summary>
public sealed record FreeHighlightStroke(Guid Id, bool Locked, Affine Transform, ImmutableArray<Point2> Points,
    double WidthPt, RgbColor Color, double Opacity)
    : StrokeAnnotation(Id, Locked, Transform, Points, WidthPt, Color, Opacity)
{
    public override AnnotationKind Kind => AnnotationKind.FreeHighlight;
}

public enum LineKind { Straight, Quadratic, Cubic }

public enum DashStyle { Solid, Dash, Dot, DashDot }

/// <summary>Straight line or Bézier curve. Controls holds 0, 1 or 2 points for Straight, Quadratic, Cubic.</summary>
public sealed record LineAnnotation(Guid Id, bool Locked, Affine Transform, LineKind LineKind, Point2 Start, Point2 End,
    ImmutableArray<Point2> Controls, double WidthPt, DashStyle Dash, bool StartArrow, bool EndArrow,
    RgbColor Color, double Opacity) : Annotation(Id, Locked, Transform)
{
    public override AnnotationKind Kind => AnnotationKind.Line;

    public static int ControlCount(LineKind kind) => kind switch
    {
        LineKind.Straight => 0,
        LineKind.Quadratic => 1,
        LineKind.Cubic => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public enum HighlightHeightMode { Auto, Manual }

/// <summary>Highlight attached to extracted PDF glyph quads (canonical page points, one quad per line/run).</summary>
public sealed record PdfTextHighlight(Guid Id, bool Locked, ImmutableArray<Quad> Quads, string? SourceTextHint,
    HighlightHeightMode HeightMode, double? ManualHeightPt, RgbColor Color, double Opacity)
    : Annotation(Id, Locked, Affine.Identity)
{
    public override AnnotationKind Kind => AnnotationKind.PdfTextHighlight;
}

public enum TextMarkupKind { Underline, StrikeOut }

/// <summary>
/// Underline or strikeout bound to extracted PDF glyph quads (spec 24.8, schema v6), like <see cref="PdfTextHighlight"/>:
/// one quad per line in canonical page points, never moved on its own. The line follows each quad's direction: at its
/// lower edge (underline) or across its middle (strikeout), <see cref="WidthPt"/> thick.
/// </summary>
public sealed record PdfTextMarkup(Guid Id, bool Locked, ImmutableArray<Quad> Quads, string? SourceTextHint, TextMarkupKind MarkupKind,
    double WidthPt, RgbColor Color, double Opacity) : Annotation(Id, Locked, Affine.Identity)
{
    public override AnnotationKind Kind => AnnotationKind.TextMarkup;
}

/// <summary>
/// Sticky note (spec 24.9, schema v6): an icon of <see cref="IconPt"/> points at the local origin (its top-left corner)
/// placed by <see cref="Annotation.Transform"/>, and plain text shown when it is opened. The icon keeps its size.
/// </summary>
public sealed record StickyNote(Guid Id, bool Locked, Affine Transform, string Text, RgbColor Color) : Annotation(Id, Locked, Transform)
{
    public const double IconPt = 20;
    public override AnnotationKind Kind => AnnotationKind.Note;

    /// <summary>An empty note is dropped when it is closed, like an empty text box (spec 24.9).</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Text);
}

public enum TextAlignment { Left, Center, Right }

/// <summary>Character formatting over [Start, Start+Length) in UTF-16 code units, on grapheme cluster boundaries.</summary>
public sealed record TextRun(int Start, int Length, string FontFamily, double SizePt, RgbColor Color,
    bool Bold, bool Italic, bool Underline, bool Strikethrough);

/// <summary>Highlight bound to a text range of its text box (UTF-16 code units, grapheme boundaries).</summary>
public sealed record TextHighlightRange(int Start, int Length, HighlightHeightMode HeightMode, double? ManualHeightPt,
    RgbColor Color, double Opacity);

/// <summary>Box fill and border. Opacity 0 means transparent; the border is a 1pt solid line when enabled.</summary>
public sealed record TextBoxStyle(RgbColor BackgroundColor, double BackgroundOpacity, bool BorderEnabled,
    RgbColor BorderColor, double BorderOpacity)
{
    public static TextBoxStyle Default { get; } = new(RgbColor.White, 0, false, RgbColor.Black, 1);
}

/// <summary>
/// Text box. The local origin is the box's top-left corner; height is derived from layout and not stored.
/// Formatting runs partition the whole text in order.
/// </summary>
public sealed record TextBoxAnnotation(Guid Id, bool Locked, Affine Transform, string Text, ImmutableArray<TextRun> Runs,
    TextAlignment Alignment, double WidthPt, TextBoxStyle BoxStyle, ImmutableArray<TextHighlightRange> Highlights)
    : Annotation(Id, Locked, Transform)
{
    /// <summary>Scale of fixed padding, border and layout minimums; 1 for legacy/new boxes (schema v4).</summary>
    public double LayoutScale { get; init; } = 1;
    public override AnnotationKind Kind => AnnotationKind.TextBox;

    /// <summary>Spec 6.2: empty or whitespace/tab/newline-only text is empty content and must never be saved.</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Text);
}

public enum ShapeKind { Rectangle, Ellipse, Triangle, Polygon, Polyline }

/// <summary>
/// Fill and border of a shape (spec 8 preface). Fill opacity 0 means no fill; a disabled border draws nothing.
/// The border is centered on the outline with round joins and caps; its dash pattern scales with its width like lines.
/// </summary>
public sealed record ShapeStyle(RgbColor FillColor, double FillOpacity, bool BorderEnabled, RgbColor BorderColor, double BorderWidthPt,
    double BorderOpacity, DashStyle BorderDash)
{
    /// <summary>New shapes: light blue fill at 30% transparency with a 2pt dark blue solid border.</summary>
    public static ShapeStyle Default { get; } = new(new(0x9E, 0xC5, 0xF0), 0.7, true, new(0x1E, 0x4F, 0x91), 2, 1, DashStyle.Solid);
}

/// <summary>
/// Vector shape (D11, schema v5). Rectangle, ellipse and triangle fill <see cref="Box"/> (local space, positive size;
/// <see cref="Points"/> empty). Polygon (closed, at least 3 points) and polyline (open, at least 2 points — other
/// apps' PolyLine) use <see cref="Points"/> with an empty box. Scaling is baked into the box, points and border width.
/// </summary>
public sealed record ShapeAnnotation(Guid Id, bool Locked, Affine Transform, ShapeKind ShapeKind, Rect2 Box, ImmutableArray<Point2> Points, ShapeStyle Style)
    : Annotation(Id, Locked, Transform)
{
    public override AnnotationKind Kind => AnnotationKind.Shape;

    public bool UsesBox => ShapeKind is ShapeKind.Rectangle or ShapeKind.Ellipse or ShapeKind.Triangle;
    public bool IsClosed => ShapeKind != ShapeKind.Polyline;
}

/// <summary>Image placed at <see cref="LocalBounds"/> in local space; pixels come from the referenced resource.</summary>
public sealed record ImageAnnotation(Guid Id, bool Locked, Affine Transform, Guid ResourceId, Rect2 LocalBounds)
    : Annotation(Id, Locked, Transform)
{
    public override AnnotationKind Kind => AnnotationKind.Image;
}
