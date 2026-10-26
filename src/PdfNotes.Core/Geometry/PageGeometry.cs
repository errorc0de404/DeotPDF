namespace PdfNotes.Core.Geometry;

public static class Units
{
    public const double PointsPerInch = 72;
    public const double DipsPerInch = 96;
    public const double DipsPerPoint = DipsPerInch / PointsPerInch;
    public const double PointsPerMillimeter = PointsPerInch / 25.4;

    public static double MillimetersToPoints(double mm) => mm * PointsPerMillimeter;
    public static double PointsToMillimeters(double pt) => pt / PointsPerMillimeter;
}

/// <summary>Page rotation in clockwise degrees, matching PDF /Rotate.</summary>
public static class PageRotation
{
    public static bool IsValid(int degrees) => degrees is 0 or 90 or 180 or 270;

    /// <summary>Normalizes a raw PDF /Rotate value exactly like PDFium: truncate to quarter turns, then wrap.</summary>
    public static int FromPdfValue(long raw)
    {
        long quarter = raw / 90 % 4;
        return (int)(quarter < 0 ? quarter + 4 : quarter) * 90;
    }

    public static int Add(int degrees, int delta)
    {
        if (!IsValid(degrees) || delta % 90 != 0) throw new ArgumentOutOfRangeException(nameof(delta));
        return ((degrees + delta) % 360 + 360) % 360;
    }
}

/// <summary>
/// Canonical page frame. App page coordinates are PDF points with the origin at the top-left of the
/// visible CropBox, +X right and +Y down, before /Rotate is applied. Display coordinates are the same
/// unit after the clockwise page rotation, origin at the top-left of the rotated page.
/// </summary>
public readonly record struct PageFrame
{
    public PageFrame(double widthPt, double heightPt, int rotation)
    {
        if (!double.IsFinite(widthPt) || widthPt <= 0) throw new ArgumentOutOfRangeException(nameof(widthPt));
        if (!double.IsFinite(heightPt) || heightPt <= 0) throw new ArgumentOutOfRangeException(nameof(heightPt));
        if (!PageRotation.IsValid(rotation)) throw new ArgumentOutOfRangeException(nameof(rotation));
        WidthPt = widthPt;
        HeightPt = heightPt;
        Rotation = rotation;
    }

    public double WidthPt { get; }
    public double HeightPt { get; }
    public int Rotation { get; }

    public double DisplayWidthPt => Rotation is 90 or 270 ? HeightPt : WidthPt;
    public double DisplayHeightPt => Rotation is 90 or 270 ? WidthPt : HeightPt;

    public Affine CanonicalToDisplay => Rotation switch
    {
        0 => Affine.Identity,
        90 => new(0, 1, -1, 0, HeightPt, 0),          // (x, y) -> (H - y, x)
        180 => new(-1, 0, 0, -1, WidthPt, HeightPt),  // (x, y) -> (W - x, H - y)
        270 => new(0, -1, 1, 0, 0, WidthPt),          // (x, y) -> (y, W - x)
        _ => throw new InvalidOperationException()
    };

    public Affine DisplayToCanonical => CanonicalToDisplay.Invert();
}

/// <summary>
/// Visible PDF page box in raw PDF user space (CropBox intersected with MediaBox) and its /UserUnit.
/// Canonical points are user units multiplied by UserUnit.
/// </summary>
public readonly record struct PdfPageBox
{
    public PdfPageBox(double left, double bottom, double right, double top, double userUnit = 1)
    {
        if (!double.IsFinite(left) || !double.IsFinite(bottom) || !double.IsFinite(right) || !double.IsFinite(top))
            throw new ArgumentException("Page box must be finite.");
        if (!double.IsFinite(userUnit) || userUnit <= 0) throw new ArgumentOutOfRangeException(nameof(userUnit));
        Left = Math.Min(left, right);
        Right = Math.Max(left, right);
        Bottom = Math.Min(bottom, top);
        Top = Math.Max(bottom, top);
        if (Right - Left <= 0 || Top - Bottom <= 0) throw new ArgumentException("Page box must have a positive area.");
        UserUnit = userUnit;
    }

    public double Left { get; }
    public double Bottom { get; }
    public double Right { get; }
    public double Top { get; }
    public double UserUnit { get; }

    public double WidthPt => (Right - Left) * UserUnit;
    public double HeightPt => (Top - Bottom) * UserUnit;

    /// <summary>PDF user space (origin bottom-left, +Y up) to canonical page points.</summary>
    public Affine PdfUserToCanonical => new(UserUnit, 0, 0, -UserUnit, -Left * UserUnit, Top * UserUnit);

    public Affine CanonicalToPdfUser => PdfUserToCanonical.Invert();

    public PageFrame Frame(int rotation) => new(WidthPt, HeightPt, rotation);
}

public static class ZoomLimits
{
    public const double Minimum = 0.10;
    public const double Maximum = 8.00;

    public static double Clamp(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, Minimum, Maximum) : 1;
}

/// <summary>
/// The single conversion path between canonical page points, WPF DIPs and device pixels for one page.
/// View state (zoom, DPI, scroll origin) never enters stored geometry.
/// </summary>
public sealed class ViewTransform
{
    public ViewTransform(PageFrame frame, double zoom, double dpiScale, Point2 pageOriginDip)
    {
        if (!double.IsFinite(zoom) || zoom <= 0) throw new ArgumentOutOfRangeException(nameof(zoom));
        if (!double.IsFinite(dpiScale) || dpiScale <= 0) throw new ArgumentOutOfRangeException(nameof(dpiScale));
        if (!pageOriginDip.IsFinite) throw new ArgumentOutOfRangeException(nameof(pageOriginDip));
        Frame = frame;
        Zoom = zoom;
        DpiScale = dpiScale;
        PageOriginDip = pageOriginDip;
        DipsPerPagePoint = zoom * Units.DipsPerPoint;
        PageToDip = frame.CanonicalToDisplay.Then(Affine.Scale(DipsPerPagePoint))
            .Then(Affine.Translation(pageOriginDip.X, pageOriginDip.Y));
        DipToPage = PageToDip.Invert();
        PageToDevice = PageToDip.Then(Affine.Scale(dpiScale));
        DeviceToPage = PageToDevice.Invert();
    }

    public PageFrame Frame { get; }
    public double Zoom { get; }
    public double DpiScale { get; }
    public Point2 PageOriginDip { get; }
    public double DipsPerPagePoint { get; }

    public Affine PageToDip { get; }
    public Affine DipToPage { get; }
    public Affine PageToDevice { get; }
    public Affine DeviceToPage { get; }

    /// <summary>Displayed page size in DIPs at the current zoom.</summary>
    public double PageWidthDip => Frame.DisplayWidthPt * DipsPerPagePoint;
    public double PageHeightDip => Frame.DisplayHeightPt * DipsPerPagePoint;

    /// <summary>Converts a screen-space length (for example a 4px threshold) to page points.</summary>
    public double DipLengthToPoints(double dips) => dips / DipsPerPagePoint;

    public double DevicePixelLengthToPoints(double pixels) => pixels / (DipsPerPagePoint * DpiScale);

    /// <summary>Converts a screen-space movement (arrow keys, drags) into a canonical page vector.</summary>
    public Point2 DipVectorToPage(Point2 dipVector) => DipToPage.TransformVector(dipVector);
}
