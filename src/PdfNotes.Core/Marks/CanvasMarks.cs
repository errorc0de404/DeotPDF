using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Marks;

// Tool previews as plain data: the editor's tools make them, the painters (Render.Skia) draw them. They live in Core
// so the painters do not depend on the editor.

/// <summary>One element of a tool preview.</summary>
public abstract record CanvasMark;

/// <summary>An annotation drawn with its preview look (an unfinished stroke, line, shape or glyph mark).</summary>
public sealed record AnnotationMark(Annotation Annotation) : CanvasMark;

/// <summary>
/// A pen or highlighter stroke being drawn (roadmap P5d). <see cref="Settled"/> pieces never change once given, so a
/// painter may keep what it drew of them and add only new ones; <see cref="Tail"/> is the end that still moves. The pieces
/// are opaque and overlap by one point; the whole is shown at <see cref="Opacity"/>. <see cref="Key"/> names the stroke.
/// </summary>
public sealed record StrokeProgressMark(Guid Key, IReadOnlyList<StrokeAnnotation> Settled, StrokeAnnotation? Tail, double Opacity) : CanvasMark;

/// <summary>The eraser circle: center in page points and diameter in page points.</summary>
public sealed record EraserCircleMark(Point2 Center, double DiameterPt) : CanvasMark;

/// <summary>The lasso path in page points, closed back to its start.</summary>
public sealed record LassoMark(IReadOnlyList<Point2> Path) : CanvasMark;

/// <summary>
/// The free polygon's rubber band from its last corner to the pointer and the marker on its first corner. Points are in
/// the shape's frame; <see cref="ToPage"/> maps them to page points.
/// </summary>
public sealed record RubberBandMark(Affine ToPage, Point2 Last, Point2 Pointer, Point2 First) : CanvasMark;

/// <summary>Text box highlights in progress: <see cref="Preview"/> holds only the new range, laid out like <see cref="Box"/>.</summary>
public sealed record TextBoxHighlightMark(TextBoxAnnotation Box, TextBoxAnnotation Preview) : CanvasMark;
