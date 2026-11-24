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

    /// <summary>Rectangles drawn by hand in the app that were left as strokes (corners rounded, sides leaning, one corner where the stroke closes).</summary>
    private static readonly double[][] DrawnRectangles =
    [
        [115.2, 33.5, 115.1, 35.4, 114.6, 41, 114.5, 43.4, 114.5, 58.4, 114.6, 60.1, 114.9, 61.7, 115, 63.2, 115, 69, 117.6, 68.9, 121, 68.6, 127.2, 68.1, 133.9, 67.3, 136.9, 67.1, 141.9, 66.9, 144.1, 66.6, 146.1, 66.5, 155.2, 66.5, 155.2, 66.2, 155.5, 66.2, 155.4, 56.4, 154.8, 46.1, 154.8, 44.8, 154.7, 44.4, 153.9, 42.8, 151.9, 38.1, 151.4, 36.6, 150.6, 34.8, 150.5, 33.5, 139.5, 33.5, 137, 33.4, 131.9, 32.8, 128.2, 32.6, 126.7, 32.5, 116.5, 32.5, 117.5, 32.5, 117.5, 32.8, 117.2, 32.8],
        [255, 33.7, 255, 67.6, 254.9, 70.5, 254.5, 74.7, 254.5, 78.8, 257.6, 77.9, 261.4, 77.2, 273.9, 75.4, 281.5, 74.5, 288.1, 73.9, 293.2, 73.6, 297.2, 73.5, 308.8, 73.5, 313.5, 74, 316.3, 74, 319.2, 74.2, 322.8, 74.2, 322.8, 73.5, 322.1, 66.7, 320.9, 52.2, 319.6, 37.8, 319.3, 33.8, 319.2, 29.9, 319, 28.6, 319, 27.5, 316.9, 22.8, 315.8, 19.8, 315, 19.8, 312.2, 21.4, 310.1, 22.4, 307.4, 23.4, 304.2, 24.5, 300.2, 25.4, 291.6, 27, 285.8, 27.9, 282.8, 28.7, 281.7, 28.8, 279.3, 29.4, 278.1, 29.6, 276.7, 29.7, 272.1, 29.8, 268.6, 30.2, 265.4, 30.2, 264, 30.5, 262.7, 30.5, 261.8, 30.7, 261.1, 30.7, 260.4, 30.9, 258.7, 31.2, 258.1, 31.2, 255.5, 31.2, 254.6, 31.5, 253.6, 31.5, 252.8, 31.8, 252.2, 31.8],
        [161.2, 116.5, 161.1, 119, 160.4, 122.6, 157.6, 136.1, 156.8, 140.8, 154.6, 156.3, 153.6, 162.1, 153.3, 164.8, 153.2, 169.1, 153.1, 170.8, 152.5, 174.7, 152.5, 174.5, 162, 174.5, 170, 174.6, 180.9, 175, 191.8, 175.5, 208, 176.4, 214, 176.6, 219.2, 177, 227.6, 177.7, 233.6, 178.5, 234.4, 178.7, 235.8, 178.8, 235.8, 173.6, 235.5, 159.9, 235.6, 149.1, 235.9, 138.9, 236.3, 131.5, 237, 126.6, 238.2, 120.9, 238.2, 120, 235.9, 119.9, 233.4, 119.6, 228.8, 118.8, 220.8, 117.2, 210, 115, 192.5, 111.2, 187.6, 110.3, 184, 109.9, 179.2, 109.6, 170, 109.5, 167.5, 109.4, 163.2, 109, 159.2, 109],
        [361.5, 108.2, 361.6, 110.9, 362.2, 115.9, 362.7, 119.1, 366.9, 138.6, 368.3, 146.5, 369.6, 155.5, 370.6, 163.9, 371.1, 170.9, 371.2, 176.6, 371.2, 184.2, 371.2, 186.6, 371, 188.6, 370.8, 190.1, 370.1, 193.2, 370, 193.8, 370, 193.5, 370.2, 193.5, 370.2, 193.2, 371.9, 192.6, 380.8, 189.9, 386.5, 188.6, 393, 187.6, 399.7, 186.8, 415.9, 185.4, 424.1, 185.1, 428.1, 185, 432.2, 185.2, 436, 185.9, 439.2, 186.6, 443.9, 187.4, 447.5, 187.8, 448.9, 187.7, 450.3, 187.2, 453.1, 186.1, 455.1, 185.4, 457.2, 184.6, 458.4, 184.2, 461.8, 182.8, 461.8, 182.5, 462.2, 182.5, 462.2, 181.9, 458.8, 176.9, 455.4, 171.5, 450.4, 162.8, 439.4, 142.1, 435.2, 133.6, 429.9, 122, 427.4, 115.6, 426.4, 113.2, 425.6, 111.3, 424.5, 106.7, 423.2, 103.8, 422.8, 103, 422.8, 102, 421.9, 102.1, 417.9, 103.7, 415.1, 104.6, 411.1, 105.7, 406.1, 106.7, 400.9, 107.3, 392, 107.9, 388.2, 108, 375.2, 107.7, 371.6, 107.5, 368, 107.5, 365.6, 107.9, 363.3, 108.2, 360.8, 108.2],
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void HandDrawnRectangleIsARectangle(int index)
    {
        double[] xy = DrawnRectangles[index];
        var points = Enumerable.Range(0, xy.Length / 2).Select(i => new Point2(xy[2 * i], xy[2 * i + 1])).ToList();
        double longSide = Math.Max(points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));

        var shape = ShapeRecognizer.Recognize(points, MinSize, 0.08 * longSide);

        Assert.NotNull(shape);
        Assert.Equal(RecognizedKind.Rectangle, shape.Kind);
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

    public static TheoryData<string> Handwriting =>
        ["ㅇ", "0", "2", "S", "zigzag", "arc", "8", "u", "n", "ㅣ", "6", "9", "a", "D", "almost 0"];

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
        // Closed-path near misses: the mouth or gap is within 15 % of the stroke length.
        "u" => [.. Polyline(new(10, 0), new(10, 32)), .. Arc(14, 32, 4, 8, 180, 0).Skip(1), .. Polyline(new(18, 32), new(18, 0)).Skip(1)],
        "n" => [.. Polyline(new(10, 40), new(10, 8)), .. Arc(15, 8, 5, 8, 180, 360).Skip(1), .. Polyline(new(20, 8), new(20, 40)).Skip(1)],
        "ㅣ" => Polyline(new(10, 0), new(10, 40), new(10, 0)),
        "6" => [.. Arc(22, 28, 20, 28, -80, -180), .. Arc(14, 28, 12, 12, 180, -170).Skip(1)],
        "9" => [.. Arc(14, 12, 12, 12, 0, -360), .. Polyline(new(26, 12), new(24, 40)).Skip(1)],
        "a" => [.. Arc(14, 26, 12, 12, -30, -330), .. Polyline(new(14 + 12 * Math.Cos(Math.PI / 6), 26 + 12 * Math.Sin(Math.PI / 6)), new(26, 12), new(28, 40)).Skip(1)],
        "D" => [.. Polyline(new(0, 0), new(0, 40)), .. Arc(0, 20, 22, 20, 90, -90).Skip(1)],
        // A tall oval whose end stops short of its start by about 9 pt.
        "almost 0" => Arc(12, 20, 10, 20, -90, 225),
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
    public void LongThinEllipseIsStillAnEllipse()
    {
        var shape = Recognize(Noisy(Arc(100, 100, 50, 15, -90, 270), 8));

        Assert.NotNull(shape);
        Assert.Equal(RecognizedKind.Ellipse, shape.Kind);
        Assert.InRange(Math.Max(shape.Points[1].X, shape.Points[1].Y), 47, 53);
        Assert.InRange(Math.Min(shape.Points[1].X, shape.Points[1].Y), 12, 18);
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
    public void DraggingATriangleTurnsAboutItsFarthestCorner()
    {
        Point2 fixedCorner = new(0, 60), grab = new(100, 0), third = new(0, 0);
        var shape = new RecognizedShape(RecognizedKind.Triangle, [third, grab, fixedCorner], 0);
        // Twice as far from the fixed corner and turned 90°.
        Point2 to = new(fixedCorner.X - 2 * (grab.Y - fixedCorner.Y), fixedCorner.Y + 2 * (grab.X - fixedCorner.X));

        var dragged = ShapeRecognizer.Dragged(shape, grab, to);

        Assert.Equal(RecognizedKind.Triangle, dragged.Kind);
        Assert.Equal(0, dragged.RotationRad);
        Assert.Equal(fixedCorner.X, dragged.Points[2].X, 6);
        Assert.Equal(fixedCorner.Y, dragged.Points[2].Y, 6);
        Assert.Equal(to.X, dragged.Points[1].X, 6);
        Assert.Equal(to.Y, dragged.Points[1].Y, 6);
        // (0,0) is (0,−60) from the fixed corner: doubled and turned it lands at (120, 60).
        Assert.Equal(120, dragged.Points[0].X, 6);
        Assert.Equal(60, dragged.Points[0].Y, 6);
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
