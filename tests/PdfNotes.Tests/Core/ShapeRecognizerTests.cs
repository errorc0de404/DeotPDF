using PdfNotes.Core.Geometry;

namespace PdfNotes.Tests.Core;

/// <summary>1.4 plan 1.2: a held stroke fitted to a line, arrow, ellipse, rectangle or triangle — never handwriting.</summary>
public sealed class ShapeRecognizerTests
{
    private const double MinSize = 24, Tolerance = 4;

    private static RecognizedShape? Recognize(IReadOnlyList<Point2> points) => ShapeRecognizer.Recognize(points, MinSize, Tolerance);

    /// <summary>Deterministic jitter of up to ±1.5 pt on each coordinate.</summary>
    private static List<Point2> Noisy(IEnumerable<Point2> points, int seed)
    {
        var random = new Random(seed);
        return [.. points.Select(p => new Point2(p.X + (random.NextDouble() * 2 - 1) * 1.5, p.Y + (random.NextDouble() * 2 - 1) * 1.5))];
    }

    /// <summary>Points about every 2 pt along a polyline.</summary>
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

    /// <summary>Points about every 2 pt along an elliptic arc (angles in degrees, +Y down).</summary>
    private static List<Point2> Arc(double cx, double cy, double rx, double ry, double fromDeg, double toDeg)
    {
        double sweep = toDeg - fromDeg;
        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) * Math.PI / 180 * Math.Max(rx, ry) / 2));
        var result = new List<Point2>();
        for (int k = 0; k <= steps; k++)
        {
            double a = (fromDeg + sweep * k / steps) * Math.PI / 180;
            result.Add(new(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a)));
        }
        return result;
    }

    private static Point2 Rotate(Point2 p, Point2 about, double degrees)
    {
        double r = degrees * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r), x = p.X - about.X, y = p.Y - about.Y;
        return new(about.X + x * c - y * s, about.Y + x * s + y * c);
    }

    [Fact]
    public void CircleBecomesEllipse()
    {
        var shape = Recognize(Noisy(Arc(100, 100, 50, 50, -90, 270), 1));

        Assert.NotNull(shape);
        Assert.Equal(RecognizedKind.Ellipse, shape.Kind);
        Assert.InRange(shape.Points[0].DistanceTo(new(100, 100)), 0, 3);
        Assert.InRange(shape.Points[1].X, 47, 53);
        Assert.InRange(shape.Points[1].Y, 47, 53);
    }

    [Fact]
    public void RotatedRectangleKeepsItsAngle()
    {
        var center = new Point2(150, 150);
        var corners = new[] { new Point2(100, 120), new(200, 120), new(200, 180), new(100, 180), new(100, 120) }
            .Select(p => Rotate(p, center, 20)).ToArray();
        var shape = Recognize(Noisy(Polyline(corners), 2));

        Assert.NotNull(shape);
        Assert.Equal(RecognizedKind.Rectangle, shape.Kind);
        Assert.InRange(shape.RotationRad * 180 / Math.PI, 18, 22);
        Assert.InRange(shape.Points[0].DistanceTo(center), 0, 3);
        Assert.InRange(shape.Points[1].X, 47, 53);
        Assert.InRange(shape.Points[1].Y, 27, 33);
    }

    [Fact]
    public void ThreeCornersAreATriangle()
    {
        Point2[] vertices = [new(20, 20), new(130, 40), new(60, 120)];
        var shape = Recognize(Noisy(Polyline(vertices[0], vertices[1], vertices[2], vertices[0]), 3));

        Assert.NotNull(shape);
        Assert.Equal(RecognizedKind.Triangle, shape.Kind);
        Assert.Equal(3, shape.Points.Length);
        Assert.Equal(0, shape.RotationRad);
        foreach (var v in vertices)
            Assert.InRange(shape.Points.Min(p => p.DistanceTo(v)), 0, 3.999);
    }

    [Fact]
    public void StraightStrokeIsALine()
    {
        var shape = Recognize(Noisy(Polyline(new(0, 0), new(120, 30)), 4));

        Assert.NotNull(shape);
        Assert.Equal(RecognizedKind.Line, shape.Kind);
        Assert.InRange(shape.Points[0].DistanceTo(new(0, 0)), 0, 1.999);
        Assert.InRange(shape.Points[1].DistanceTo(new(120, 30)), 0, 1.999);
    }

    [Fact]
    public void LineWithAHookIsAnArrow()
    {
        Point2 start = new(10, 80), tip = new(130, 40);
        double back = Math.Atan2(start.Y - tip.Y, start.X - tip.X) * 180 / Math.PI;
        Point2 Barb(double degrees) => new(tip.X + 20 * Math.Cos((back + degrees) * Math.PI / 180), tip.Y + 20 * Math.Sin((back + degrees) * Math.PI / 180));
        var shape = Recognize(Noisy(Polyline(start, tip, Barb(35), tip, Barb(-35)), 5));

        Assert.NotNull(shape);
        Assert.Equal(RecognizedKind.Arrow, shape.Kind);
        Assert.InRange(shape.Points[0].DistanceTo(start), 0, 4);
        Assert.InRange(shape.Points[1].DistanceTo(tip), 0, 4);
    }

    public static TheoryData<string> Handwriting => ["ㅇ", "0", "2", "S", "zigzag", "arc", "8"];

    /// <summary>Glyph strokes about 40 pt high.</summary>
    private static List<Point2> Glyph(string name) => name switch
    {
        // A circle closed at its top, then a tail of over 20 % of its perimeter.
        "ㅇ" => [.. Arc(20, 28, 12, 12, -90, 270), .. Polyline(new(20, 16), new(32, 0)).Skip(1)],
        // A tall oval left open by a quarter turn.
        "0" => Arc(12, 20, 10, 20, -90, 180),
        "2" => [.. Arc(14, 12, 10, 10, 180, 390), .. Polyline(new(14 + 10 * Math.Cos(Math.PI / 6), 12 + 10 * Math.Sin(Math.PI / 6)), new(2, 40), new(26, 40)).Skip(1)],
        "S" => [.. Arc(14, 10, 10, 10, -30, -270), .. Arc(14, 30, 10, 10, -90, 150).Skip(1)],
        "zigzag" => Polyline(new(0, 0), new(8, 40), new(16, 0), new(24, 40), new(32, 0), new(40, 40)),
        "arc" => Arc(40, 40, 40, 40, 180, 360),
        "8" => [.. Arc(20, 10, 10, 10, 90, -270), .. Arc(20, 30, 10, 10, -90, 270).Skip(1)],
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Handwriting))]
    public void HandwritingIsNotAShape(string glyph)
    {
        Assert.Null(Recognize(Glyph(glyph)));
        for (int seed = 10; seed < 20; seed++) Assert.Null(Recognize(Noisy(Glyph(glyph), seed)));
    }

    [Fact]
    public void SmallStrokesAreNeverShapes()
    {
        Assert.Null(Recognize(Arc(50, 50, 10, 10, -90, 270)));
        Assert.Null(Recognize(Noisy(Arc(50, 50, 10, 10, -90, 270), 6)));
    }

    [Fact]
    public void SameInputSameResult()
    {
        var points = Noisy(Arc(100, 100, 60, 35, 0, 360), 7);

        var first = Recognize(points);
        var second = Recognize([.. points]);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second!.GetHashCode());
    }

    [Fact]
    public void DraggingScalesAndTurnsAboutTheFarSide()
    {
        var shape = new RecognizedShape(RecognizedKind.Rectangle, [new(100, 100), new(50, 30)], 0);
        Point2 grab = new(150, 130), opposite = new(50, 70);
        // Twice as far from the fixed corner and turned 90° (clockwise on the +Y-down page).
        Point2 to = new(opposite.X - 2 * (grab.Y - opposite.Y), opposite.Y + 2 * (grab.X - opposite.X));

        var dragged = ShapeRecognizer.Dragged(shape, grab, to);

        Assert.Equal(RecognizedKind.Rectangle, dragged.Kind);
        Assert.Equal(100, dragged.Points[1].X, 6);
        Assert.Equal(60, dragged.Points[1].Y, 6);
        Assert.Equal(Math.PI / 2, dragged.RotationRad, 6);
        // The corner opposite the grabbed one stays put: center + R(rotation)·(−halfW, −halfH).
        double c = Math.Cos(dragged.RotationRad), s = Math.Sin(dragged.RotationRad);
        Point2 center = dragged.Points[0], half = dragged.Points[1];
        Assert.Equal(opposite.X, center.X - half.X * c + half.Y * s, 6);
        Assert.Equal(opposite.Y, center.Y - half.X * s - half.Y * c, 6);
    }

    [Fact]
    public void DraggingALineMovesOnlyItsEnd()
    {
        var line = new RecognizedShape(RecognizedKind.Arrow, [new(0, 0), new(100, 0)], 0);

        var dragged = ShapeRecognizer.Dragged(line, new(100, 0), new(50, 80));

        Assert.Equal(RecognizedKind.Arrow, dragged.Kind);
        Assert.Equal(new Point2(0, 0), dragged.Points[0]);
        Assert.Equal(new Point2(50, 80), dragged.Points[1]);
    }
}
