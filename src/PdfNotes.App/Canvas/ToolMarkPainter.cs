using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using PdfNotes.App.Themes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Tools;
using PdfNotes.Text;

namespace PdfNotes.App.Canvas;

/// <summary>Paints the preview marks of the editor tools (<see cref="CanvasMark"/>) on the page overlay.</summary>
public static class ToolMarkPainter
{
    private static readonly ConditionalWeakTable<ICanvasPreview, PageOverlayPainter> Painters = new();

    /// <summary>The overlay painter of <paramref name="preview"/>; the same instance each time, so showing it again is a no-op.</summary>
    public static PageOverlayPainter For(ICanvasPreview preview) =>
        Painters.GetValue(preview, p => (context, index, page, transform) => Paint(context, p.Marks(index, page, transform), transform));

    public static void Paint(DrawingContext context, IEnumerable<CanvasMark> marks, ViewTransform transform)
    {
        foreach (var mark in marks)
        {
            switch (mark)
            {
                case AnnotationMark { Annotation: var annotation }: PaintAnnotation(context, annotation, transform); break;
                case EraserCircleMark circle: PaintCircle(context, circle, transform); break;
                case LassoMark lasso: PaintLasso(context, lasso, transform); break;
                case RubberBandMark band: PaintRubberBand(context, band, transform); break;
                case TextBoxHighlightMark highlight: PaintBoxHighlight(context, highlight, transform); break;
            }
        }
    }

    private static void PaintAnnotation(DrawingContext context, Annotation annotation, ViewTransform transform)
    {
        switch (annotation)
        {
            case StrokeAnnotation stroke: AnnotationPainter.PaintPreview(context, stroke, transform); break;
            case LineAnnotation line: AnnotationPainter.PaintPreview(context, line, transform); break;
            case ShapeAnnotation shape: AnnotationPainter.PaintPreview(context, shape, transform); break;
            case PdfTextHighlight highlight: AnnotationPainter.PaintPreview(context, highlight, transform); break;
            case PdfTextMarkup markup: AnnotationPainter.PaintPreview(context, markup, transform); break;
        }
    }

    // ----- eraser circle -----

    private static readonly Pen CircleOuter = Frozen(new Pen(Theme.Brush("Overlay.EraserHalo"), 3));
    private static readonly Pen CircleInner = Frozen(new Pen(Theme.Brush("Overlay.Eraser"), 1));
    private static readonly Brush CircleFill = Theme.Brush("Overlay.EraserFill");

    private static void PaintCircle(DrawingContext context, EraserCircleMark circle, ViewTransform transform)
    {
        var c = transform.PageToDip.Transform(circle.Center);
        double r = transform.DipsPerPagePoint * circle.DiameterPt / 2;
        context.DrawEllipse(CircleFill, CircleOuter, new Point(c.X, c.Y), r, r);
        context.DrawEllipse(null, CircleInner, new Point(c.X, c.Y), r, r);
    }

    // ----- lasso -----

    private static readonly Pen PathOuter = Frozen(new Pen(Theme.Brush("Overlay.Halo"), 3) { LineJoin = PenLineJoin.Round });
    private static readonly Pen PathInner = Frozen(new Pen(Theme.Brush("Overlay.Selection"), 1.2)
    {
        DashStyle = new System.Windows.Media.DashStyle([4, 3], 0), LineJoin = PenLineJoin.Round
    });
    private static readonly Brush LassoFill = Theme.Brush("Overlay.LassoFill");

    private static void PaintLasso(DrawingContext context, LassoMark lasso, ViewTransform transform)
    {
        var path = lasso.Path;
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var c = geometry.Open())
        {
            var first = transform.PageToDip.Transform(path[0]);
            c.BeginFigure(new Point(first.X, first.Y), true, true);
            c.PolyLineTo(path.Skip(1).Select(p => transform.PageToDip.Transform(p)).Select(p => new Point(p.X, p.Y)).ToArray(), true, true);
        }
        geometry.Freeze();
        context.DrawGeometry(LassoFill, PathOuter, geometry);
        context.DrawGeometry(null, PathInner, geometry);
    }

    // ----- free polygon rubber band -----

    private static readonly Pen Rubber = Frozen(new Pen(Theme.Brush("Overlay.Selection"), 1) { DashStyle = new System.Windows.Media.DashStyle([4, 3], 0) });
    // White halo under the rubber band so it shows on dark pages too (D13c, like the selection and lasso).
    private static readonly Pen RubberHalo = Frozen(new Pen(Theme.Brush("Overlay.Halo"), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    private static void PaintRubberBand(DrawingContext context, RubberBandMark band, ViewTransform transform)
    {
        var toDip = band.ToPage.Then(transform.PageToDip);
        var last = toDip.Transform(band.Last); var now = toDip.Transform(band.Pointer); var first = toDip.Transform(band.First);
        context.DrawLine(RubberHalo, new(last.X, last.Y), new(now.X, now.Y));
        context.DrawLine(Rubber, new(last.X, last.Y), new(now.X, now.Y));
        context.DrawEllipse(null, RubberHalo, new(first.X, first.Y), 4, 4);
        context.DrawEllipse(Theme.Brush("Overlay.Halo"), Rubber, new(first.X, first.Y), 4, 4);
    }

    // ----- text box highlight in progress -----

    private static void PaintBoxHighlight(DrawingContext context, TextBoxHighlightMark mark, ViewTransform transform)
    {
        var m = mark.Box.Transform.Then(transform.PageToDip);
        context.PushTransform(new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY)));
        foreach (var (h, lines) in TextLayout.HighlightLines(mark.Preview, TextLayout.Measure(mark.Box).Characters))
        {
            var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var r in lines) geometry.Children.Add(new RectangleGeometry(new Rect(r.X, r.Y, r.Width, r.Height)));
            context.DrawGeometry(WpfText.Brush(h.Color, h.Opacity), null, geometry);
        }
        context.Pop();
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
