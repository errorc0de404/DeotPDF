using System.Windows.Input;
using PdfNotes.App.Tools;
using PdfNotes.Core.Geometry;

namespace PdfNotes.Tests.App;

/// <summary>D11: rotation snapping to right angles and the resize cursor direction of selection handles (spec 5.3).</summary>
public sealed class SelectionInputTests
{
    private static double Rad(double degrees) => degrees * Math.PI / 180;
    private static double Final(double original, double delta, bool shift = false, bool alt = false) =>
        (original + SelectTool.SnapRotation(Rad(original), Rad(delta), shift, alt) * 180 / Math.PI - original) + original;

    [Theory]
    [InlineData(0, 88, 90)]
    [InlineData(0, 92, 90)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 359, 360)]
    [InlineData(0, 3, 0)]
    [InlineData(0, -3, 0)]
    [InlineData(10, 168, 180)] // final 178°
    [InlineData(-30, -61, -90)] // final -91°
    [InlineData(45, 222.5, 270)] // final 267.5°
    public void NearRightAnglesSnap(double original, double delta, double expected) =>
        Assert.Equal(expected, Final(original, delta), 9);

    [Theory]
    [InlineData(0, 3.5)]
    [InlineData(0, 86)]
    [InlineData(0, -3.5)]
    [InlineData(20, 50)] // final 70°
    [InlineData(0, 45)]
    public void OtherAnglesStayFree(double original, double delta) =>
        Assert.Equal(original + delta, Final(original, delta), 9);

    [Fact]
    public void ShiftKeepsFifteenDegreeStepsAndAltTurnsSnappingOff()
    {
        Assert.Equal(45, Final(0, 47, shift: true), 9);
        Assert.Equal(90, Final(0, 88, shift: true), 9); // 90° is a 15° step as well
        Assert.Equal(30, Final(10, 22, shift: true), 9); // the final angle (32°) is stepped, not the change
        Assert.Equal(88, Final(0, 88, alt: true), 9);
        Assert.Equal(2, Final(0, 2, alt: true), 9);
        Assert.Equal(45, Final(0, 46, shift: true, alt: true), 9); // Shift wins
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 0.2)]
    [InlineData(0, 1)]
    [InlineData(0.1, -1)]
    [InlineData(1, 1)]
    [InlineData(-1, -1)]
    [InlineData(1, -1)]
    [InlineData(-1, 1)]
    public void ResizeCursorFollowsTheHandleDirection(double x, double y)
    {
        var expected = Math.Abs(y) < .3 ? Cursors.SizeWE : Math.Abs(x) < .3 ? Cursors.SizeNS : x * y > 0 ? Cursors.SizeNWSE : Cursors.SizeNESW;
        Assert.Same(expected, SelectionCursors.Resize(new Point2(x, y)));
    }
}
