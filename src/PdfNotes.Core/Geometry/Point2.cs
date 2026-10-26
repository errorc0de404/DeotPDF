namespace PdfNotes.Core.Geometry;

/// <summary>Engine/UI independent point. The unit depends on the coordinate space that owns it.</summary>
public readonly record struct Point2(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    public double DistanceTo(Point2 other)
    {
        double dx = X - other.X, dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>Axis-aligned rectangle in a local coordinate space.</summary>
public readonly record struct Rect2(double X, double Y, double Width, double Height)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height);
}

/// <summary>
/// Text quad in PDF QuadPoints order relative to the glyph orientation:
/// upper-left, upper-right, lower-left, lower-right.
/// </summary>
public readonly record struct Quad(Point2 UpperLeft, Point2 UpperRight, Point2 LowerLeft, Point2 LowerRight)
{
    public bool IsFinite => UpperLeft.IsFinite && UpperRight.IsFinite && LowerLeft.IsFinite && LowerRight.IsFinite;

    public Quad Transform(Affine matrix) => new(matrix.Transform(UpperLeft), matrix.Transform(UpperRight),
        matrix.Transform(LowerLeft), matrix.Transform(LowerRight));
}
