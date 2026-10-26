namespace PdfNotes.Core.Geometry;

/// <summary>
/// 2D affine matrix using the PDF/WPF row-vector convention:
/// x' = x*M11 + y*M21 + OffsetX, y' = x*M12 + y*M22 + OffsetY.
/// The six values map directly to a PDF <c>[a b c d e f]</c> matrix and a WPF Matrix.
/// </summary>
public readonly record struct Affine(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
{
    public const double RigidTolerance = 1e-9;

    public static Affine Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public static Affine Translation(double x, double y) => new(1, 0, 0, 1, x, y);

    public static Affine Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    public static Affine Scale(double s) => new(s, 0, 0, s, 0, 0);

    /// <summary>Rotation about the origin. In the app's +Y-down page space a positive angle turns clockwise on screen.</summary>
    public static Affine Rotation(double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new(c, s, -s, c, 0, 0);
    }

    /// <summary>Rotation plus translation, the only transform form stored in the v1 schema.</summary>
    public static Affine Rigid(double radians, double offsetX, double offsetY) =>
        Rotation(radians) with { OffsetX = offsetX, OffsetY = offsetY };

    public double Determinant => M11 * M22 - M12 * M21;

    public bool IsFinite => double.IsFinite(M11) && double.IsFinite(M12) && double.IsFinite(M21) &&
        double.IsFinite(M22) && double.IsFinite(OffsetX) && double.IsFinite(OffsetY);

    public bool IsIdentity => this == Identity;

    /// <summary>Translation only (sticky notes, spec 24.9).</summary>
    public bool IsTranslation => IsFinite && M11 == 1 && M12 == 0 && M21 == 0 && M22 == 1;

    /// <summary>True for rotation + translation without scale, skew or mirroring.</summary>
    public bool IsRigid =>
        IsFinite &&
        Math.Abs(M11 * M11 + M12 * M12 - 1) <= RigidTolerance &&
        Math.Abs(M21 * M21 + M22 * M22 - 1) <= RigidTolerance &&
        Math.Abs(M11 * M21 + M12 * M22) <= RigidTolerance &&
        Math.Abs(Determinant - 1) <= RigidTolerance;

    public Point2 Transform(Point2 p) => new(p.X * M11 + p.Y * M21 + OffsetX, p.X * M12 + p.Y * M22 + OffsetY);

    /// <summary>Transforms a direction/length vector, ignoring translation.</summary>
    public Point2 TransformVector(Point2 v) => new(v.X * M11 + v.Y * M21, v.X * M12 + v.Y * M22);

    /// <summary>Returns the matrix that applies this transform first and <paramref name="next"/> second.</summary>
    public Affine Then(Affine next) => new(
        M11 * next.M11 + M12 * next.M21,
        M11 * next.M12 + M12 * next.M22,
        M21 * next.M11 + M22 * next.M21,
        M21 * next.M12 + M22 * next.M22,
        OffsetX * next.M11 + OffsetY * next.M21 + next.OffsetX,
        OffsetX * next.M12 + OffsetY * next.M22 + next.OffsetY);

    public Affine Invert()
    {
        double det = Determinant;
        if (!IsFinite || !double.IsFinite(det) || Math.Abs(det) < 1e-300)
            throw new InvalidOperationException("Matrix is not invertible.");
        return new(
            M22 / det, -M12 / det,
            -M21 / det, M11 / det,
            (OffsetY * M21 - OffsetX * M22) / det,
            (OffsetX * M12 - OffsetY * M11) / det);
    }

    public double[] ToArray() => [M11, M12, M21, M22, OffsetX, OffsetY];

    public static Affine FromArray(IReadOnlyList<double> values) => values.Count == 6
        ? new(values[0], values[1], values[2], values[3], values[4], values[5])
        : throw new ArgumentException("An affine matrix needs six values.", nameof(values));
}
