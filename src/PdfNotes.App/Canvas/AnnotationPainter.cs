using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.App.Services;
using PdfNotes.App.Themes;
using LineGeometry = PdfNotes.Core.Geometry.LineGeometry;

namespace PdfNotes.App.Canvas;

/// <summary>
/// Draws app annotations on the overlay layer, matching their PDF appearance (Pdf/Writing/AnnotationAppearance):
/// a stroke is one round-capped path, a single point is a dot of the stroke width, and opacity is applied once per
/// stroke so a highlighter's self-overlap does not darken while separate strokes do (spec 9.2).
/// </summary>
public static class AnnotationPainter
{
    private static readonly ConditionalWeakTable<StrokeAnnotation, Geometry> Geometries = new();
    private static readonly ConditionalWeakTable<LineAnnotation, LineDrawing> Lines = new();
    private static readonly ConditionalWeakTable<PdfTextHighlight, Geometry> Highlights = new();
    private static readonly ConditionalWeakTable<ShapeAnnotation, ShapeDrawing> Shapes = new();
    private static readonly ConditionalWeakTable<PdfTextMarkup, Geometry> Markups = new();

    private sealed record ShapeDrawing(Geometry Outline, Brush? Fill, Pen? Border);

    private sealed record LineDrawing(Geometry? Body, Geometry Heads, Pen? Pen, Brush Brush);

    /// <summary>Paints every annotation of <paramref name="page"/> in stacking order. <paramref name="transform"/> maps canonical points to the target space.</summary>
    public static void PaintPage(DrawingContext context, Page page, ViewTransform transform)
    {
        foreach (var annotation in page.Annotations) Paint(context, annotation, transform);
    }

    public static void Paint(DrawingContext context, Annotation annotation, ViewTransform transform)
    {
        switch (annotation)
        {
            case TextBoxAnnotation text:
                var m = text.Transform.Then(transform.PageToDip);
                context.PushTransform(new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY)));
                context.DrawDrawing(PdfNotes.Text.WpfText.Measure(text).Drawing);
                context.Pop();
                break;
            case StrokeAnnotation stroke:
                PaintStroke(context, stroke, Geometries.GetValue(stroke, BuildGeometry), transform);
                break;
            case LineAnnotation line:
                PaintLine(context, line, Lines.GetValue(line, BuildLine), transform);
                break;
            case PdfTextHighlight highlight:
                PaintTextHighlight(context, highlight, Highlights.GetValue(highlight, BuildHighlight), transform);
                break;
            case ShapeAnnotation shape:
                PaintShape(context, shape, Shapes.GetValue(shape, BuildShape), transform);
                break;
            case PdfTextMarkup markup:
                PaintFill(context, Markups.GetValue(markup, m => BuildQuads(TextSelection.PaintedQuads(m))), markup.Color, markup.Opacity, transform);
                break;
            case StickyNote note:
                PaintNote(context, note, transform);
                break;
            // Other kinds get their painter in the stage that implements them; the reader never restores them before that.
        }
    }

    public static void PaintImage(DrawingContext context, ImageAnnotation image, Resource resource, ViewTransform transform)
    {
        var m = image.Transform.Then(transform.PageToDip);
        var b = image.LocalBounds;
        context.PushTransform(new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY)));
        context.DrawImage(ImageAssets.Display(resource), new Rect(b.X, b.Y, b.Width, b.Height));
        context.Pop();
    }

    /// <summary>A PDF glyph highlight (spec 9.2): all its line quads as one nonzero fill under a single opacity, like the PDF appearance.</summary>
    public static void PaintPreview(DrawingContext context, PdfTextHighlight highlight, ViewTransform transform) =>
        PaintTextHighlight(context, highlight, BuildHighlight(highlight), transform);

    private static void PaintTextHighlight(DrawingContext context, PdfTextHighlight highlight, Geometry geometry, ViewTransform transform)
    {
        var m = transform.PageToDip;
        context.PushTransform(new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY)));
        context.PushOpacity(highlight.Opacity);
        var brush = new SolidColorBrush(Color.FromRgb(highlight.Color.R, highlight.Color.G, highlight.Color.B));
        brush.Freeze();
        context.DrawGeometry(brush, null, geometry);
        context.Pop();
        context.Pop();
    }

    private static Geometry BuildHighlight(PdfTextHighlight highlight)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var c = geometry.Open())
            foreach (var quad in TextSelection.PaintedQuads(highlight))
            {
                var ring = TextSelection.Ring(quad);
                c.BeginFigure(new Point(ring[0].X, ring[0].Y), true, true);
                c.PolyLineTo(ring.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(), false, false);
            }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>An underline/strikeout while it is dragged (spec 24.8).</summary>
    public static void PaintPreview(DrawingContext context, PdfTextMarkup markup, ViewTransform transform) =>
        PaintFill(context, BuildQuads(TextSelection.PaintedQuads(markup)), markup.Color, markup.Opacity, transform);

    private static void PaintFill(DrawingContext context, Geometry geometry, RgbColor color, double opacity, ViewTransform transform)
    {
        var m = transform.PageToDip;
        context.PushTransform(new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY)));
        context.PushOpacity(opacity);
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        context.DrawGeometry(brush, null, geometry);
        context.Pop();
        context.Pop();
    }

    private static Geometry BuildQuads(IEnumerable<Quad> quads)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var c = geometry.Open())
            foreach (var quad in quads)
            {
                var ring = TextSelection.Ring(quad);
                c.BeginFigure(new Point(ring[0].X, ring[0].Y), true, true);
                c.PolyLineTo(ring.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(), false, false);
            }
        geometry.Freeze();
        return geometry;
    }

    private static readonly Geometry NoteShape = Freeze(new RectangleGeometry(new Rect(0, 0, StickyNote.IconPt, StickyNote.IconPt), 3, 3));
    private static readonly Geometry NoteLines = Freeze(Geometry.Parse("M5,6.5 L15,6.5 M5,10 L15,10 M5,13.5 L11,13.5"));

    private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

    /// <summary>Sticky note icon (spec 24.9): the same rounded square and lines as its PDF appearance, in page points.</summary>
    public static void PaintNote(DrawingContext context, StickyNote note, ViewTransform transform)
    {
        var m = note.Transform.Then(transform.PageToDip);
        context.PushTransform(new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY)));
        var fill = new SolidColorBrush(Color.FromRgb(note.Color.R, note.Color.G, note.Color.B)); fill.Freeze();
        var edge = new SolidColorBrush(Color.FromRgb((byte)(note.Color.R / 2), (byte)(note.Color.G / 2), (byte)(note.Color.B / 2))); edge.Freeze();
        context.DrawGeometry(fill, new Pen(edge, 1), NoteShape);
        context.DrawGeometry(null, new Pen(edge, 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, NoteLines);
        context.Pop();
    }

    /// <summary>Selection that cannot be moved or resized (glyph highlights): the frame only, no handles.</summary>
    public static void PaintSelectionOutline(DrawingContext context, Point2[] corners)
    {
        if (corners.Length != 4) return;
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(new Point(corners[0].X, corners[0].Y), false, true);
            c.PolyLineTo(corners.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(), true, true);
        }
        context.DrawGeometry(null, SelectionPenOuter, geometry); context.DrawGeometry(null, SelectionPenInner, geometry);
    }

    /// <summary>Selected PDF text (select tool): translucent blue line quads in viewport DIPs.</summary>
    public static void PaintTextSelection(DrawingContext context, IEnumerable<Quad> quads, ViewTransform transform)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var c = geometry.Open())
            foreach (var quad in quads)
            {
                var ring = TextSelection.Ring(quad).Select(transform.PageToDip.Transform).ToArray();
                c.BeginFigure(new Point(ring[0].X, ring[0].Y), true, true);
                c.PolyLineTo(ring.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(), false, false);
            }
        geometry.Freeze();
        context.DrawGeometry(TextSelectionBrush, null, geometry);
    }

    private static readonly Brush TextSelectionBrush = Theme.Brush("Overlay.TextSelection");

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    public static void PaintSelectionFrame(DrawingContext context, Point2[] handles, Point2[]? frameCorners = null)
    {
        if (handles.Length != 5) return;
        var corners = frameCorners ?? handles.Take(4).ToArray();
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(new Point(corners[0].X, corners[0].Y), false, true);
            c.PolyLineTo(corners.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(), true, true);
        }
        context.DrawGeometry(null, SelectionPenOuter, geometry); context.DrawGeometry(null, SelectionPenInner, geometry);
        var top = new Point((corners[0].X + corners[1].X) / 2, (corners[0].Y + corners[1].Y) / 2);
        context.DrawLine(SelectionPenInner, top, new(handles[4].X, handles[4].Y));
        foreach (var p in handles.Take(4).Where(p => p.X != double.MaxValue)) context.DrawRectangle(HandleFill, SelectionPenInner, new Rect(p.X - 4, p.Y - 4, 8, 8));
        context.DrawEllipse(HandleFill, SelectionPenInner, new(handles[4].X, handles[4].Y), 5, 5);
    }

    /// <summary>Paints a shape that is not in the model yet (creation preview) without caching.</summary>
    public static void PaintPreview(DrawingContext context, ShapeAnnotation shape, ViewTransform transform) =>
        PaintShape(context, shape, BuildShape(shape), transform);

    /// <summary>
    /// Same outline and order as the PDF appearance (ShapeGeometry): the even-odd fill with its own opacity, then the
    /// border with its own opacity, round joins and caps, and the line dash pattern.
    /// </summary>
    private static void PaintShape(DrawingContext context, ShapeAnnotation shape, ShapeDrawing drawing, ViewTransform transform)
    {
        var m = shape.Transform.Then(transform.PageToDip);
        context.PushTransform(new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY)));
        if (drawing.Fill is not null)
        {
            context.PushOpacity(shape.Style.FillOpacity);
            context.DrawGeometry(drawing.Fill, null, drawing.Outline);
            context.Pop();
        }
        if (drawing.Border is not null)
        {
            context.PushOpacity(shape.Style.BorderOpacity);
            context.DrawGeometry(null, drawing.Border, drawing.Outline);
            context.Pop();
        }
        context.Pop();
    }

    private static ShapeDrawing BuildShape(ShapeAnnotation shape)
    {
        var outline = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var c = outline.Open())
        {
            if (ShapeGeometry.Vertices(shape) is { } vertices)
            {
                c.BeginFigure(new Point(vertices[0].X, vertices[0].Y), true, shape.IsClosed);
                c.PolyLineTo(vertices.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(), true, true);
            }
            else
            {
                var e = ShapeGeometry.EllipseCubics(shape.Box);
                c.BeginFigure(new Point(e[0].X, e[0].Y), true, true);
                for (int i = 1; i < e.Length; i += 3)
                    c.BezierTo(new(e[i].X, e[i].Y), new(e[i + 1].X, e[i + 1].Y), new(e[i + 2].X, e[i + 2].Y), true, true);
            }
        }
        outline.Freeze();
        var style = shape.Style;
        Brush? fill = null;
        if (ShapeGeometry.HasFill(shape)) fill = Frozen(new SolidColorBrush(Color.FromRgb(style.FillColor.R, style.FillColor.G, style.FillColor.B)));
        Pen? border = null;
        if (style.BorderEnabled)
        {
            var dashes = LineGeometry.DashPattern(style.BorderDash);
            border = new Pen(Frozen(new SolidColorBrush(Color.FromRgb(style.BorderColor.R, style.BorderColor.G, style.BorderColor.B))), style.BorderWidthPt)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, DashCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
                DashStyle = dashes.Length == 0 ? DashStyles.Solid : new System.Windows.Media.DashStyle(dashes, 0)
            };
            border.Freeze();
        }
        return new(outline, fill, border);
    }

    /// <summary>Paints a line that is not in the model yet (creation or handle preview) without caching.</summary>
    public static void PaintPreview(DrawingContext context, LineAnnotation line, ViewTransform transform) =>
        PaintLine(context, line, BuildLine(line), transform);

    /// <summary>
    /// Same geometry as the PDF appearance (LineGeometry): trimmed body with round caps and dash pattern, filled heads.
    /// Opacity is one layer over body and heads, like the PDF transparency group.
    /// </summary>
    private static void PaintLine(DrawingContext context, LineAnnotation line, LineDrawing drawing, ViewTransform transform)
    {
        var m = line.Transform.Then(transform.PageToDip);
        context.PushTransform(new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY)));
        bool layered = line.Opacity < 1;
        if (layered) context.PushOpacity(line.Opacity);
        if (drawing.Body is not null) context.DrawGeometry(null, drawing.Pen, drawing.Body);
        context.DrawGeometry(drawing.Brush, null, drawing.Heads);
        if (layered) context.Pop();
        context.Pop();
    }

    private static LineDrawing BuildLine(LineAnnotation line)
    {
        var brush = new SolidColorBrush(Color.FromRgb(line.Color.R, line.Color.G, line.Color.B));
        brush.Freeze();
        Geometry? body = null;
        Pen? pen = null;
        if (LineGeometry.Body(line) is { } p)
        {
            var path = new StreamGeometry();
            using (var c = path.Open())
            {
                c.BeginFigure(new Point(p[0].X, p[0].Y), false, false);
                if (p.Length == 2) c.LineTo(new(p[1].X, p[1].Y), true, false);
                else if (p.Length == 3) c.QuadraticBezierTo(new(p[1].X, p[1].Y), new(p[2].X, p[2].Y), true, false);
                else c.BezierTo(new(p[1].X, p[1].Y), new(p[2].X, p[2].Y), new(p[3].X, p[3].Y), true, false);
            }
            path.Freeze();
            body = path;
            var dashes = LineGeometry.DashPattern(line.Dash);
            pen = new Pen(brush, line.WidthPt)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, DashCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
                DashStyle = dashes.Length == 0 ? DashStyles.Solid : new System.Windows.Media.DashStyle(dashes, 0)
            };
            pen.Freeze();
        }
        var heads = new StreamGeometry();
        using (var c = heads.Open())
            foreach (var head in LineGeometry.ArrowHeads(line))
            {
                c.BeginFigure(new Point(head[0].X, head[0].Y), true, true);
                c.PolyLineTo([new Point(head[1].X, head[1].Y), new Point(head[2].X, head[2].Y)], false, false);
            }
        heads.Freeze();
        return new(body, heads, pen, brush);
    }

    /// <summary>
    /// Handles of a single selected line (spec 8.2): squares on the end points, circles on the control points, and
    /// thin guides from each end to its control point. <paramref name="handles"/> are viewport DIPs, ends first.
    /// </summary>
    public static void PaintLineHandles(DrawingContext context, Point2[] handles)
    {
        if (handles.Length < 2) return;
        var toPoint = (Point2 p) => new Point(p.X, p.Y);
        if (handles.Length == 3) { DrawGuide(handles[0], handles[2]); DrawGuide(handles[2], handles[1]); }
        else if (handles.Length == 4) { DrawGuide(handles[0], handles[2]); DrawGuide(handles[3], handles[1]); }
        foreach (var p in handles.Skip(2)) context.DrawEllipse(HandleFill, SelectionPenSolid, toPoint(p), 4.5, 4.5);
        foreach (var p in handles.Take(2)) context.DrawRectangle(HandleFill, SelectionPenSolid, new Rect(p.X - 4, p.Y - 4, 8, 8));

        void DrawGuide(Point2 a, Point2 b)
        {
            context.DrawLine(SelectionPenOuter, toPoint(a), toPoint(b));
            context.DrawLine(SelectionPenInner, toPoint(a), toPoint(b));
        }
    }

    /// <summary>Paints a stroke that is not in the model yet (live preview) without caching its geometry.</summary>
    public static void PaintPreview(DrawingContext context, StrokeAnnotation stroke, ViewTransform transform) =>
        PaintStroke(context, stroke, BuildGeometry(stroke), transform);

    private static void PaintStroke(DrawingContext context, StrokeAnnotation stroke, Geometry geometry, ViewTransform transform)
    {
        var m = stroke.Transform.Then(transform.PageToDip);
        context.PushTransform(new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY)));
        bool layered = stroke.Opacity < 1;
        if (layered) context.PushOpacity(stroke.Opacity);
        var brush = new SolidColorBrush(Color.FromRgb(stroke.Color.R, stroke.Color.G, stroke.Color.B));
        brush.Freeze();
        if (stroke.IsOutlined) context.DrawGeometry(brush, null, geometry); // area-eraser piece: filled region (D08)
        else if (stroke.Points.Length == 1)
        {
            double r = stroke.WidthPt / 2;
            context.DrawEllipse(brush, null, new Point(stroke.Points[0].X, stroke.Points[0].Y), r, r);
        }
        else
        {
            var pen = new Pen(brush, stroke.WidthPt)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            context.DrawGeometry(null, pen, geometry);
        }
        if (layered) context.Pop();
        context.Pop();
    }

    private static Geometry BuildGeometry(StrokeAnnotation stroke)
    {
        if (!stroke.IsOutlined) return BuildGeometry(stroke.Points);
        var outline = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var c = outline.Open())
            foreach (var ring in stroke.Outline)
            {
                c.BeginFigure(new Point(ring[0].X, ring[0].Y), true, true);
                var rest = new Point[ring.Length - 1];
                for (int i = 1; i < ring.Length; i++) rest[i - 1] = new Point(ring[i].X, ring[i].Y);
                c.PolyLineTo(rest, false, false);
            }
        outline.Freeze();
        return outline;
    }

    private static Geometry BuildGeometry(IReadOnlyList<Point2> points)
    {
        var geometry = new StreamGeometry();
        if (points.Count > 1)
        {
            using var c = geometry.Open();
            c.BeginFigure(new Point(points[0].X, points[0].Y), false, false);
            var rest = new Point[points.Count - 1];
            for (int i = 1; i < points.Count; i++) rest[i - 1] = new Point(points[i].X, points[i].Y);
            c.PolyLineTo(rest, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Selection mark: dashed rectangle around the painted bounds of a stroke.</summary>
    public static void PaintSelection(DrawingContext context, StrokeAnnotation stroke, ViewTransform transform)
    {
        var b = AnnotationGeometry.LocalBounds(stroke);
        var m = stroke.Transform.Then(transform.PageToDip);
        Point2[] corners = [m.Transform(new(b.X, b.Y)), m.Transform(new(b.X + b.Width, b.Y)),
            m.Transform(new(b.X + b.Width, b.Y + b.Height)), m.Transform(new(b.X, b.Y + b.Height))];
        double pad = 3;
        var rect = new Rect(new Point(corners.Min(p => p.X) - pad, corners.Min(p => p.Y) - pad),
            new Point(corners.Max(p => p.X) + pad, corners.Max(p => p.Y) + pad));
        context.DrawRectangle(null, SelectionPenOuter, rect);
        context.DrawRectangle(null, SelectionPenInner, rect);
    }

    private static readonly Brush HandleFill = Theme.Brush("Overlay.Halo");
    private static readonly Pen SelectionPenOuter = Frozen(new Pen(Theme.Brush("Overlay.Halo"), 3));
    private static readonly Pen SelectionPenInner = Frozen(new Pen(Theme.Brush("Overlay.Selection"), 1.2)
    {
        DashStyle = new System.Windows.Media.DashStyle([4, 3], 0)
    });

    private static readonly Pen SelectionPenSolid = Frozen(new Pen(Theme.Brush("Overlay.Selection"), 1.2));

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
