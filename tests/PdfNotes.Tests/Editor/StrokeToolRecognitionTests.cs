using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Tools;
using PdfNotes.TestSupport;
using PdfNotes.Tests.Core;

namespace PdfNotes.Tests.Editor;

/// <summary>1.4 plan 1.2: the pen or highlighter held still at the end of a stroke makes it a shape.</summary>
public sealed class StrokeToolRecognitionTests
{
    private readonly ManualClock clock = new();
    private readonly DocumentSession session = new(TestModels.Empty());
    private readonly FlatView view;
    private bool recognition = true;

    public StrokeToolRecognitionTests() => view = new FlatView(session.Current.Pages[0]);

    private IReadOnlyList<Annotation> Annotations => session.Current.Pages[0].Annotations;

    /// <summary>A screen point (the tests draw in DIP) on the page, in points.</summary>
    private Point2 Pt(Point2 dip) => view.PageTransform(0).DipToPage.Transform(dip);

    private double Pt(double dip) => view.PageTransform(0).DipLengthToPoints(dip);

    private StrokeTool Tool(AnnotationKind kind = AnnotationKind.Ink, StrokeStyle? style = null) =>
        new(view, () => session, () => style ?? StrokeStyle.DefaultPen, kind, () => true, clock, () => recognition);

    private static CanvasPointer At(Point2 p, PointerDevice device = PointerDevice.Pen) =>
        new(p, InputModifiers.None, Device: device, Pressure: device == PointerDevice.Pen ? 0.5 : null);

    /// <summary>Down at the first point, through the others, then held <paramref name="holdMs"/> before the release at the last.</summary>
    private void Draw(StrokeTool tool, IReadOnlyList<Point2> points, int holdMs, PointerDevice device = PointerDevice.Pen)
    {
        Assert.True(tool.PointerDown(At(points[0], device)));
        foreach (var p in points.Skip(1)) tool.PointerMove(At(p, device));
        clock.Advance(holdMs);
        tool.PointerUp(At(points[^1], device));
    }

    private static List<Point2> Noisy(IEnumerable<Point2> points, int seed, double amount = 1.5)
    {
        var random = new Random(seed);
        return [.. points.Select(p => new Point2(p.X + (random.NextDouble() * 2 - 1) * amount, p.Y + (random.NextDouble() * 2 - 1) * amount))];
    }

    /// <summary>Points about every 2 pt along a circle whose radius is scaled by <paramref name="radius"/> at each angle.</summary>
    private static List<Point2> Loop(Point2 center, Func<double, double> radius, double r)
    {
        int steps = (int)Math.Ceiling(2 * Math.PI * r / 2);
        return [.. Enumerable.Range(0, steps + 1).Select(k =>
        {
            double a = -Math.PI / 2 + 2 * Math.PI * k / steps;
            double rr = r * radius(a);
            return new Point2(center.X + rr * Math.Cos(a), center.Y + rr * Math.Sin(a));
        })];
    }

    private static Point2 Center(Rect2 box) => new(box.X + box.Width / 2, box.Y + box.Height / 2);

    private static List<Point2> Circle(Point2 center, double r) => Loop(center, _ => 1, r);

    private static List<Point2> Polyline(params Point2[] corners)
    {
        var result = new List<Point2> { corners[0] };
        for (int i = 1; i < corners.Length; i++)
        {
            Point2 a = corners[i - 1], b = corners[i];
            int steps = Math.Max(1, (int)Math.Ceiling(a.DistanceTo(b) / 2));
            for (int k = 1; k <= steps; k++) result.Add(new(a.X + (b.X - a.X) * k / steps, a.Y + (b.Y - a.Y) * k / steps));
        }
        return result;
    }

    private static List<Point2> Arc(double cx, double cy, double rx, double ry, double fromDeg, double toDeg)
    {
        double sweep = toDeg - fromDeg;
        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) * Math.PI / 180 * Math.Max(rx, ry) / 2));
        return [.. Enumerable.Range(0, steps + 1).Select(k =>
        {
            double a = (fromDeg + sweep * k / steps) * Math.PI / 180;
            return new Point2(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a));
        })];
    }

    [Fact]
    public void HoldingAtTheEndOfACircleMakesAnEllipse()
    {
        Draw(Tool(), Noisy(Circle(new(200, 200), 50), 1), StrokeTool.HoldTime.Milliseconds);

        var shape = Assert.IsType<ShapeAnnotation>(Assert.Single(Annotations));
        Assert.Equal(ShapeKind.Ellipse, shape.ShapeKind);
        Assert.InRange(shape.Transform.Transform(Center(shape.Box)).DistanceTo(Pt(new Point2(200, 200))), 0, 3);
        Assert.InRange(shape.Box.Width / 2, Pt(46), Pt(54));
        Assert.True(session.Undo());
        Assert.IsType<InkStroke>(Assert.Single(Annotations)); // the first Undo gives the hand stroke back
        Assert.True(session.Undo());
        Assert.Empty(Annotations);
    }

    [Fact]
    public void ReleasingBeforeTheHoldKeepsTheInk()
    {
        Draw(Tool(), Noisy(Circle(new(200, 200), 50), 1), StrokeTool.HoldTime.Milliseconds - 1);
        Assert.IsType<InkStroke>(Assert.Single(Annotations));
    }

    [Fact]
    public void MovingFartherThanThreeDipRestartsTheHold()
    {
        var tool = Tool();
        var points = Noisy(Circle(new(200, 200), 50), 1);
        tool.PointerDown(At(points[0]));
        foreach (var p in points.Skip(1)) tool.PointerMove(At(p));
        clock.Advance(300);
        tool.PointerMove(At(new(points[^1].X + 4, points[^1].Y)));
        clock.Advance(300);
        tool.PointerUp(At(new(points[^1].X + 4, points[^1].Y)));
        Assert.IsType<InkStroke>(Assert.Single(Annotations));
    }

    [Fact]
    public void SmallMovesWithinThreeDipStillHold()
    {
        var tool = Tool();
        var points = Noisy(Circle(new(200, 200), 50), 1);
        tool.PointerDown(At(points[0]));
        foreach (var p in points.Skip(1)) tool.PointerMove(At(p));
        clock.Advance(300);
        tool.PointerMove(At(new(points[^1].X + 2, points[^1].Y + 1)));
        clock.Advance(200);
        tool.PointerUp(At(new(points[^1].X + 2, points[^1].Y + 1)));
        Assert.IsType<ShapeAnnotation>(Assert.Single(Annotations));
    }

    [Fact]
    public void MovingAfterRecognitionStretchesTheShape()
    {
        var tool = Tool();
        var points = Noisy(Circle(new(200, 200), 50), 1);
        tool.PointerDown(At(points[0]));
        foreach (var p in points.Skip(1)) tool.PointerMove(At(p));
        clock.Advance(500);
        var grab = points[^1]; // near the top of the circle
        var outward = new Point2(grab.X, grab.Y - 30);
        tool.PointerMove(At(outward));
        tool.PointerUp(At(outward));

        var shape = Assert.IsType<ShapeAnnotation>(Assert.Single(Annotations));
        Assert.True(shape.Box.Width / 2 > Pt(60) && shape.Box.Height / 2 > Pt(60), $"{shape.Box}");
    }

    [Fact]
    public void ARecognizedLineFollowsThePenEnd()
    {
        var tool = Tool();
        var points = Noisy(Polyline(new(100, 100), new(220, 130)), 2);
        tool.PointerDown(At(points[0]));
        foreach (var p in points.Skip(1)) tool.PointerMove(At(p));
        clock.Advance(500);
        tool.PointerMove(At(new(260, 200)));
        tool.PointerUp(At(new(260, 200)));

        var line = Assert.IsType<LineAnnotation>(Assert.Single(Annotations));
        Assert.Equal(LineKind.Straight, line.LineKind);
        Assert.False(line.StartArrow || line.EndArrow);
        Assert.InRange(line.Start.DistanceTo(Pt(new Point2(100, 100))), 0, 3);
        Assert.InRange(line.End.DistanceTo(Pt(new Point2(260, 200))), 0, 1e-9);
    }

    [Fact]
    public void HandwritingHeldStaysInk()
    {
        // Task 3's "2", about 40 pt high.
        List<Point2> two = [.. Arc(114, 112, 10, 10, 180, 390),
            .. Polyline(new(114 + 10 * Math.Cos(Math.PI / 6), 112 + 10 * Math.Sin(Math.PI / 6)), new(102, 140), new(126, 140)).Skip(1)];
        Draw(Tool(), two, 600);
        Assert.IsType<InkStroke>(Assert.Single(Annotations));
    }

    [Fact]
    public void TurnedOffNeverRecognizes()
    {
        recognition = false;
        Draw(Tool(), Noisy(Circle(new(200, 200), 50), 1), 600);
        Draw(Tool(AnnotationKind.FreeHighlight), Noisy(Polyline(new(100, 300), new(250, 300)), 6), 600);
        Assert.Collection(Annotations, a => Assert.IsType<InkStroke>(a), a => Assert.True(((FreeHighlightStroke)a).Points.Length > 2));
    }

    [Fact]
    public void PenStyleBecomesTheBorder()
    {
        var red = new RgbColor(0xE5, 0x39, 0x35);
        Draw(Tool(style: new StrokeStyle(red, 3, 40)), Noisy(Circle(new(200, 200), 50), 3), 500);

        var shape = Assert.IsType<ShapeAnnotation>(Assert.Single(Annotations));
        Assert.True(shape.Style.BorderEnabled);
        Assert.Equal(red, shape.Style.BorderColor);
        Assert.Equal(3, shape.Style.BorderWidthPt);
        Assert.Equal(0.6, shape.Style.BorderOpacity, 9);
        Assert.Equal(DashStyle.Solid, shape.Style.BorderDash);
        Assert.Equal(0, shape.Style.FillOpacity);
    }

    [Fact]
    public void ARectangleTurnedTwentyDegreesKeepsItsTurn()
    {
        var c = new Point2(200, 200);
        double r = 20 * Math.PI / 180;
        Point2 Turn(double x, double y) => new(c.X + x * Math.Cos(r) - y * Math.Sin(r), c.Y + x * Math.Sin(r) + y * Math.Cos(r));
        Draw(Tool(), Noisy(Polyline(Turn(-50, -30), Turn(50, -30), Turn(50, 30), Turn(-50, 30), Turn(-50, -30)), 4), 500);

        var shape = Assert.IsType<ShapeAnnotation>(Assert.Single(Annotations));
        Assert.Equal(ShapeKind.Rectangle, shape.ShapeKind);
        Assert.InRange(shape.Transform.Transform(Center(shape.Box)).DistanceTo(Pt(c)), 0, 3);
        var axis = shape.Transform.TransformVector(new(1, 0));
        double turned = Math.Atan2(axis.Y, axis.X) * 180 / Math.PI;
        Assert.True(Math.Abs(turned - 20) < 3 || Math.Abs(turned - 20 - 90) < 3 || Math.Abs(turned - 20 + 90) < 3, $"{turned}");
    }

    [Fact]
    public void ATriangleIsAThreeCornerPolygon()
    {
        Draw(Tool(), Noisy(Polyline(new(200, 100), new(270, 220), new(130, 220), new(200, 100)), 5), 500);

        var shape = Assert.IsType<ShapeAnnotation>(Assert.Single(Annotations));
        Assert.Equal(ShapeKind.Polygon, shape.ShapeKind);
        Assert.Equal(3, shape.Points.Length);
    }

    [Fact]
    public void HighlighterHoldStraightensOnlyLines()
    {
        var marker = Tool(AnnotationKind.FreeHighlight, StrokeStyle.DefaultHighlighter);
        Draw(marker, Noisy(Polyline(new(100, 300), new(260, 310)), 6), 500);
        var straight = Assert.IsType<FreeHighlightStroke>(Assert.Single(Annotations));
        Assert.Equal(2, straight.Points.Length);
        Assert.Equal(StrokeStyle.DefaultHighlighter.WidthPt, straight.WidthPt);

        Draw(marker, Noisy(Circle(new(200, 500), 50), 7), 500);
        var round = Assert.IsType<FreeHighlightStroke>(Annotations[^1]);
        Assert.True(round.Points.Length > 2);
        Assert.Equal(2, Annotations.Count);
    }

    [Fact]
    public void AFingerDrawingTakesTheSamePath()
    {
        Draw(Tool(), Noisy(Circle(new(200, 200), 50), 1), 500, PointerDevice.Touch);
        Assert.IsType<ShapeAnnotation>(Assert.Single(Annotations));
    }

    [Fact]
    public void AHeldPenDrawingToolNeverOpensTheMenu() => Assert.False(TouchRules.PenHolds(Tool(), new(200, 200)));

    [Fact]
    public void CancelDropsTheStrokeAndItsHold()
    {
        var tool = Tool();
        var points = Noisy(Circle(new(200, 200), 50), 1);
        tool.PointerDown(At(points[0]));
        foreach (var p in points.Skip(1)) tool.PointerMove(At(p));
        tool.Cancel();
        clock.Advance(500);
        Assert.Equal(0, clock.Pending);
        Assert.Empty(Annotations);
        Assert.False(tool.IsActive);
    }

    /// <summary>Task 3 review: a hand circle wobbles at low frequency (radius ±6 %, 2–3 lobes) and is still an ellipse.</summary>
    [Theory]
    [InlineData(40, 2), InlineData(40, 3), InlineData(150, 2), InlineData(150, 3)]
    public void AWobblyHandCircleIsStillAnEllipse(double across, int lobes)
    {
        for (int seed = 0; seed < 10; seed++)
        {
            var center = new Point2(250, 100 + seed * 160 % 600);
            double phase = seed * 0.7;
            double dips = view.PageTransform(0).DipsPerPagePoint; // the sizes and the ±1.5 pt jitter are page points
            var loop = Noisy(Loop(center, a => 1 + 0.06 * Math.Sin(lobes * a + phase), across / 2 * dips), 100 + seed, 1.5 * dips);
            Draw(Tool(), loop, 500);
            var shape = Assert.IsType<ShapeAnnotation>(Annotations[^1]);
            Assert.Equal(ShapeKind.Ellipse, shape.ShapeKind);
        }
    }

    private sealed class FlatView(Page page) : ICanvasView
    {
        private readonly ViewTransform transform = new(page.Setup.Frame, 1, 1, default);
        public PageAnchor? HitTestPage(Point2 viewportPoint) => new(0, transform.DipToPage.Transform(viewportPoint));
        public ViewTransform PageTransform(int index) => transform;
        public double DpiScale => 1;
        public Point2 Offset => default;
        public void SetHorizontalOffset(double value) { }
        public void SetVerticalOffset(double value) { }
        public void SetPreview(int pageIndex, ICanvasPreview? preview, bool mark = false) { }
        public void RefreshPreview() { }
        public void RefreshOverlays() { }
        public void RefreshOverlay(int pageIndex) { }
    }
}
