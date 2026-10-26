using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Tests.Core;

/// <summary>D07: Bézier fitting with fixed ends, kind conversion, arrow heads, bounds/hit and transforms of lines.</summary>
public sealed class LineGeometryTests
{
    private static readonly Point2[] Parabola = [new(0, 0), new(50, -60), new(100, 0)];
    private static readonly Point2[] SCurve = [new(0, 0), new(30, -80), new(70, 80), new(100, 0)];

    /// <summary>Samples a curve at uneven parameters, like input from a mouse that speeds up and slows down.</summary>
    private static Point2[] Sample(Point2[] polygon, int count = 60) =>
        Enumerable.Range(0, count).Select(i => (double)i / (count - 1)).Select(t => LineGeometry.Evaluate(polygon, t * t * (3 - 2 * t))).ToArray();

    private static LineAnnotation Line(LineKind kind, Point2[] polygon, bool startArrow = false, bool endArrow = false, double width = 2)
    {
        var line = LineGeometry.WithPolygon(new LineAnnotation(Guid.NewGuid(), false, Affine.Identity, LineKind.Straight, polygon[0], polygon[^1], [],
            width, DashStyle.Solid, startArrow, endArrow, RgbColor.Black, 1), polygon);
        Assert.Equal(kind, line.LineKind);
        return line;
    }

    private static double MaxDifference(Point2[] a, Point2[] b) =>
        Enumerable.Range(0, 201).Select(i => i / 200.0).Max(t => LineGeometry.Evaluate(a, t).DistanceTo(LineGeometry.Evaluate(b, t)));

    [Fact]
    public void QuadraticPathKeepsOneControlPointWithinTolerance()
    {
        var path = Sample(Parabola);
        var fit = LineGeometry.FitCurve(path, tolerance: 1);
        Assert.Equal(LineKind.Quadratic, fit.Kind);
        Assert.True(fit.MaxError < 0.25, $"error {fit.MaxError}");
        Assert.True(fit.Controls[0].DistanceTo(Parabola[1]) < 1, $"control {fit.Controls[0]}");
    }

    [Fact]
    public void SCurveExceedsQuadraticToleranceAndUsesTwoControlPoints()
    {
        var path = Sample(SCurve);
        var quadratic = LineGeometry.FitCurve(path, 1, forcedControls: 1);
        Assert.True(quadratic.MaxError > 1, "an S curve cannot be one quadratic");
        var fit = LineGeometry.FitCurve(path, 1);
        Assert.Equal(LineKind.Cubic, fit.Kind);
        Assert.True(fit.MaxError < 0.5, $"error {fit.MaxError}");
        Assert.Equal(LineKind.Cubic, LineGeometry.FitCurve(Sample(Parabola), 1, forcedControls: 2).Kind);
    }

    [Fact]
    public void FitKeepsTheExactEndPointsAndHandlesTwoPointInput()
    {
        var path = Sample(SCurve);
        var fit = LineGeometry.FitCurve(path, 1);
        var line = Line(LineKind.Straight, [path[0], path[^1]]) with { LineKind = fit.Kind, Controls = fit.Controls };
        Assert.Equal(path[0], line.Start);
        Assert.Equal(path[^1], line.End);
        var two = LineGeometry.FitCurve([new(0, 0), new(10, 0)], 1);
        Assert.Equal(LineKind.Quadratic, two.Kind);
        Assert.Equal(new Point2(5, 0), two.Controls[0]);
    }

    [Fact]
    public void RaisingTheDegreeKeepsTheShapeExactly()
    {
        var quadratic = Line(LineKind.Quadratic, Parabola);
        var cubic = LineGeometry.Convert(quadratic, LineKind.Cubic);
        Assert.Equal(LineKind.Cubic, cubic.LineKind);
        Assert.True(MaxDifference(LineGeometry.ControlPolygon(quadratic), LineGeometry.ControlPolygon(cubic)) < 1e-9);

        var straight = Line(LineKind.Straight, [new(3, 4), new(90, -20)]);
        foreach (var kind in new[] { LineKind.Quadratic, LineKind.Cubic })
            Assert.True(MaxDifference(LineGeometry.ControlPolygon(straight), LineGeometry.ControlPolygon(LineGeometry.Convert(straight, kind))) < 1e-9);
    }

    [Fact]
    public void LoweringTheDegreeKeepsEndPointsAndTheClosestShape()
    {
        var cubic = Line(LineKind.Cubic, SCurve);
        var quadratic = LineGeometry.Convert(cubic, LineKind.Quadratic);
        Assert.Equal(cubic.Start, quadratic.Start);
        Assert.Equal(cubic.End, quadratic.End);
        Assert.Single(quadratic.Controls);
        // An elevated quadratic reduces back to the original control point.
        var elevated = LineGeometry.Convert(Line(LineKind.Quadratic, Parabola), LineKind.Cubic);
        Assert.True(LineGeometry.Convert(elevated, LineKind.Quadratic).Controls[0].DistanceTo(Parabola[1]) < 1e-6);
        Assert.Empty(LineGeometry.Convert(cubic, LineKind.Straight).Controls);
    }

    [Fact]
    public void DegenerateTangentsNeverProduceNaN()
    {
        var p = new Point2(40, 40);
        // Every control point on the ends: no direction at all.
        foreach (var polygon in new[] { new[] { p, p }, [p, p, p], [p, p, p, p] })
        {
            var line = Line((LineKind)(polygon.Length - 2), polygon, true, true);
            foreach (var head in LineGeometry.ArrowHeads(line)) Assert.All(head, q => Assert.True(q.IsFinite));
            Assert.True(LineGeometry.Bounds(line).IsFinite);
            Assert.Null(LineGeometry.Body(line));
            Assert.True(LineGeometry.Hit(line, p, 1));
        }
        // A control point on the end: the direction comes from the next distinct control point.
        var cubic = Line(LineKind.Cubic, [new(0, 0), new(0, 50), new(100, 0), new(100, 0)], endArrow: true);
        var direction = LineGeometry.EndDirection(LineGeometry.ControlPolygon(cubic), atEnd: true);
        var expected = new Point2(100 / Math.Sqrt(100 * 100 + 50 * 50), -50 / Math.Sqrt(100 * 100 + 50 * 50));
        Assert.True(direction.DistanceTo(expected) < 1e-12, $"{direction}");
    }

    [Fact]
    public void ArrowHeadsScaleWithWidthAndTheBodyEndsInsideThem()
    {
        var line = Line(LineKind.Straight, [new(0, 0), new(100, 0)], endArrow: true, width: 2);
        var head = LineGeometry.ArrowHeads(line).Single();
        Assert.Equal(new Point2(100, 0), head[0]);
        Assert.Equal(100 - 2 * LineGeometry.ArrowLengthPerWidth, head[1].X, 9);
        Assert.Equal(2 * LineGeometry.ArrowHalfWidthPerWidth, Math.Abs(head[1].Y), 9);
        var wide = LineGeometry.ArrowHeads(line with { WidthPt = 4 }).Single();
        Assert.Equal(2 * (head[0].X - head[1].X), wide[0].X - wide[1].X, 9);
        var body = LineGeometry.Body(line)!;
        Assert.Equal(100 - LineGeometry.ArrowLength(2) * LineGeometry.ArrowTrimFraction, body[^1].X, 6);
        Assert.Equal(0, body[0].X);
        var bounds = LineGeometry.Bounds(line);
        Assert.True(bounds.Height >= 4 * LineGeometry.ArrowHalfWidthPerWidth - 1e-9, "the head is inside the painted bounds");
    }

    [Fact]
    public void HitTestUsesTheCurveWidthAndArrowHeads()
    {
        var curve = Line(LineKind.Quadratic, Parabola, endArrow: true, width: 4);
        var onCurve = LineGeometry.Evaluate(Parabola, .5);
        Assert.True(LineGeometry.Hit(curve, onCurve, 0));
        Assert.True(LineGeometry.Hit(curve, new(onCurve.X, onCurve.Y + 1.9), 0));
        Assert.False(LineGeometry.Hit(curve, new(onCurve.X, onCurve.Y + 10), 1));
        Assert.False(LineGeometry.Hit(curve, Parabola[1], 1), "the control point is not on the curve");
        var head = LineGeometry.ArrowHeads(curve).Single();
        var inside = new Point2((head[0].X + head[1].X + head[2].X) / 3, (head[0].Y + head[1].Y + head[2].Y) / 3);
        Assert.True(LineGeometry.Hit(curve, inside, 0));
    }

    [Fact]
    public void FlatteningStaysWithinTolerance()
    {
        foreach (var polygon in new[] { Parabola, SCurve })
        {
            var polyline = LineGeometry.Flatten(polygon, 0.05);
            double worst = Enumerable.Range(0, 2001).Select(i => LineGeometry.Evaluate(polygon, i / 2000.0))
                .Max(p => StrokeGeometry.DistanceToPolyline(polyline, p));
            Assert.True(worst <= 0.05, $"{worst}");
        }
    }

    [Fact]
    public void UniformScaleIsBakedIntoPointsAndWidthOnce()
    {
        var line = Line(LineKind.Cubic, SCurve, width: 3) with { Transform = Affine.Rigid(.3, 10, 20) };
        var scaled = (LineAnnotation)AnnotationGeometry.Similarity(line, new(0, 0), scale: 2, angle: .2, move: new(5, 6));
        Assert.True(scaled.Transform.IsRigid);
        Assert.Equal(6, scaled.WidthPt, 12);
        var expected = Affine.Scale(2).Then(Affine.Rotation(.2)).Then(Affine.Translation(5, 6));
        foreach (var (before, after) in LineGeometry.ControlPolygon(line).Zip(LineGeometry.ControlPolygon(scaled)))
        {
            var want = expected.Transform(line.Transform.Transform(before));
            Assert.True(scaled.Transform.Transform(after).DistanceTo(want) < 1e-9);
        }
        Assert.Null(ModelValidator.ValidateAnnotation(scaled, _ => false));
    }

    [Fact]
    public void DashPatternsAreDefinedForEveryStyle()
    {
        Assert.Empty(LineGeometry.DashPattern(DashStyle.Solid));
        foreach (var dash in new[] { DashStyle.Dash, DashStyle.Dot, DashStyle.DashDot })
        {
            var pattern = LineGeometry.DashPattern(dash);
            Assert.True(pattern.Length % 2 == 0 && pattern.All(v => v >= 0) && pattern.Sum() > 0);
        }
    }
}
