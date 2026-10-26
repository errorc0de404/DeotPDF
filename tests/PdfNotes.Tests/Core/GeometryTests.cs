using System.Windows;
using System.Windows.Media;
using PdfNotes.Core.Geometry;

namespace PdfNotes.Tests.Core;

public class GeometryTests
{
    private const double Tolerance = 1e-6; // spec D02: numeric round trip within 1e-6pt

    public static TheoryData<int> Rotations => [0, 90, 180, 270];

    private static readonly PdfPageBox[] Boxes =
    [
        new(0, 0, 612, 792),
        new(40, 50, 590, 740, 2),               // offset CropBox + UserUnit 2
        new(-100.25, 33.5, 500.75, 900.125, 1.5) // negative/odd offset
    ];

    private static IEnumerable<Point2> SamplePoints(PageFrame frame) =>
    [
        new(0, 0), new(frame.WidthPt, 0), new(0, frame.HeightPt), new(frame.WidthPt, frame.HeightPt),
        new(frame.WidthPt / 3, frame.HeightPt * 0.7), new(-25.5, 1e-3), new(frame.WidthPt + 123.456, -77.7)
    ];

    [Theory]
    [MemberData(nameof(Rotations))]
    public void PdfUserCanonicalDipAndDeviceRoundTripWithinOneMicroPoint(int rotation)
    {
        foreach (var box in Boxes)
        {
            var frame = box.Frame(rotation);
            foreach (double zoom in new[] { 0.10, 1.00, 8.00 })
            foreach (double dpi in new[] { 1.0, 2.0 })
            {
                var view = new ViewTransform(frame, zoom, dpi, new(37.25, -1234.5));
                foreach (var point in SamplePoints(frame))
                {
                    var dip = view.PageToDip.Transform(point);
                    var device = view.PageToDevice.Transform(point);
                    AssertClose(point, view.DipToPage.Transform(dip));
                    AssertClose(point, view.DeviceToPage.Transform(device));
                    var user = box.CanonicalToPdfUser.Transform(point);
                    AssertClose(point, box.PdfUserToCanonical.Transform(user));
                    // Device pixels are DIPs multiplied by the DPI scale, nothing else.
                    Assert.Equal(dip.X * dpi, device.X, 9);
                    Assert.Equal(dip.Y * dpi, device.Y, 9);
                }
            }
        }
    }

    [Fact]
    public void RotationMapsPageCornersLikeAClockwiseTurnedSheet()
    {
        const double w = 100, h = 200;
        // Corner order: top-left, top-right, bottom-left of the unrotated (canonical) page.
        Point2 tl = new(0, 0), tr = new(w, 0), bl = new(0, h);
        var r90 = new PageFrame(w, h, 90);
        Assert.Equal((200.0, 100.0), (r90.DisplayWidthPt, r90.DisplayHeightPt));
        Assert.Equal(new Point2(h, 0), r90.CanonicalToDisplay.Transform(tl));   // left edge becomes the top edge
        Assert.Equal(new Point2(h, w), r90.CanonicalToDisplay.Transform(tr));
        Assert.Equal(new Point2(0, 0), r90.CanonicalToDisplay.Transform(bl));
        var r180 = new PageFrame(w, h, 180);
        Assert.Equal(new Point2(w, h), r180.CanonicalToDisplay.Transform(tl));
        Assert.Equal(new Point2(0, 0), r180.CanonicalToDisplay.Transform(new(w, h)));
        var r270 = new PageFrame(w, h, 270);
        Assert.Equal(new Point2(0, w), r270.CanonicalToDisplay.Transform(tl));
        Assert.Equal(new Point2(0, 0), r270.CanonicalToDisplay.Transform(tr));
        Assert.Equal(new Point2(h, w), r270.CanonicalToDisplay.Transform(bl));
    }

    [Fact]
    public void CropBoxOffsetAndUserUnitDefineCanonicalOriginAndScale()
    {
        var box = new PdfPageBox(40, 50, 590, 740, 2);
        Assert.Equal(1100, box.WidthPt);
        Assert.Equal(1380, box.HeightPt);
        Assert.Equal(new Point2(0, 0), box.PdfUserToCanonical.Transform(new(40, 740)));   // top-left of CropBox
        Assert.Equal(new Point2(1100, 1380), box.PdfUserToCanonical.Transform(new(590, 50)));
        Assert.Equal(new Point2(20, 40), box.PdfUserToCanonical.Transform(new(50, 720)));
        // Reversed corner order in a PDF array is normalized.
        Assert.Equal(box, new PdfPageBox(590, 740, 40, 50, 2));
    }

    [Fact]
    public void ScreenLengthsConvertThroughZoomAndDpiOnly()
    {
        var view = new ViewTransform(new PageFrame(612, 792, 90), 8, 2, new(0, 0));
        Assert.Equal(612 * 8 * 96 / 72.0, view.PageHeightDip, 9);
        Assert.Equal(0.375, view.DipLengthToPoints(4), 12);            // 4px threshold at 800%
        Assert.Equal(0.1875, view.DevicePixelLengthToPoints(4), 12);   // same 4 device px at 200% DPI
        // One DIP to the right on a 90° page moves along canonical +Y (the left edge is now the top).
        var vector = view.DipVectorToPage(new(1, 0));
        Assert.Equal(0, vector.X, 12);
        Assert.Equal(-view.DipLengthToPoints(1), vector.Y, 12);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [InlineData(45, 0)]
    [InlineData(135, 90)]
    [InlineData(-45, 0)]
    [InlineData(-180, 180)]
    public void RawPdfRotationNormalizesLikePdfium(int raw, int expected) => Assert.Equal(expected, PageRotation.FromPdfValue(raw));

    [Fact]
    public void AffineUsesTheSameRowVectorConventionAsWpfMatrix()
    {
        var a = Affine.Rigid(0.7, 12, -3).Then(Affine.Scale(1.5, 0.5));
        var b = new Affine(0.2, -1.1, 3.3, 0.9, 5, 6);
        var wa = new Matrix(a.M11, a.M12, a.M21, a.M22, a.OffsetX, a.OffsetY);
        var wb = new Matrix(b.M11, b.M12, b.M21, b.M22, b.OffsetX, b.OffsetY);
        var point = new Point(3.25, -8.5);
        var expected = Matrix.Multiply(wa, wb).Transform(point); // WPF: wa first, then wb
        var actual = a.Then(b).Transform(new(point.X, point.Y));
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
        var inverse = a.Invert();
        wa.Invert();
        Assert.Equal(wa.M11, inverse.M11, 12);
        Assert.Equal(wa.OffsetX, inverse.OffsetX, 9);
        Assert.Equal(wa.OffsetY, inverse.OffsetY, 9);
    }

    [Fact]
    public void RigidDetectionRejectsScaleSkewAndMirror()
    {
        Assert.True(Affine.Rigid(1.234, -50, 70).IsRigid);
        Assert.True(Affine.Identity.IsRigid);
        Assert.False(Affine.Scale(2).IsRigid);
        Assert.False(Affine.Scale(-1, 1).IsRigid);
        Assert.False(new Affine(1, 0, 0.2, 1, 0, 0).IsRigid);
        Assert.False(Affine.Identity.Then(new Affine(1, 0, 0, 1, double.NaN, 0)).IsRigid);
    }

    private static void AssertClose(Point2 expected, Point2 actual)
    {
        Assert.True(Math.Abs(expected.X - actual.X) <= Tolerance && Math.Abs(expected.Y - actual.Y) <= Tolerance,
            $"Expected {expected}, got {actual}.");
    }
}
