using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Tests.Core;

public class NewPageSpecTests
{
    [Fact]
    public void DefaultIsOneBlankA4PortraitGeneratedPage()
    {
        var model = NewPageSpec.Default.CreateDocument();
        var setup = Assert.Single(model.Pages).Setup;
        Assert.Equal(595.2755905511812, setup.WidthPt, 9);  // 210mm
        Assert.Equal(841.8897637795276, setup.HeightPt, 9); // 297mm
        Assert.Equal(0, setup.Rotation);
        Assert.Equal(PageSourceKind.Generated, setup.Source.Kind);
        Assert.Equal(PageBackground.Blank, setup.Background);
        Assert.Null(ModelValidator.ValidateDocument(model));
    }

    [Theory]
    [InlineData(PaperSize.A4, PageOrientation.Landscape, 297, 210)]
    [InlineData(PaperSize.A5, PageOrientation.Portrait, 148, 210)]
    [InlineData(PaperSize.Letter, PageOrientation.Portrait, 215.9, 279.4)]
    [InlineData(PaperSize.Letter, PageOrientation.Landscape, 279.4, 215.9)]
    public void PresetsAndOrientation(PaperSize size, PageOrientation orientation, double widthMm, double heightMm)
    {
        var spec = NewPageSpec.FromPreset(size, orientation, PageBackground.Grid);
        Assert.Equal((widthMm, heightMm), (spec.WidthMm, spec.HeightMm));
        var setup = spec.ToPageSetup();
        Assert.Equal(Units.MillimetersToPoints(widthMm), setup.WidthPt, 12);
        Assert.Equal(Units.MillimetersToPoints(heightMm), setup.HeightPt, 12);
        Assert.Equal(PageBackgroundKind.Grid, setup.Background.Kind);
        Assert.Equal(5, setup.Background.SpacingMm);
    }

    [Theory]
    [InlineData(10, 10, true)]
    [InlineData(2000, 2000, true)]
    [InlineData(9.99, 100, false)]
    [InlineData(100, 2000.01, false)]
    [InlineData(0, 100, false)]
    [InlineData(-50, 100, false)]
    [InlineData(double.NaN, 100, false)]
    [InlineData(100, double.PositiveInfinity, false)]
    public void CustomSizeRangeIs10To2000Millimetres(double widthMm, double heightMm, bool valid)
    {
        var spec = new NewPageSpec(widthMm, heightMm, PageBackground.Lined);
        Assert.Equal(valid, spec.Validate() is null);
        if (valid) Assert.Null(ModelValidator.ValidateDocument(spec.CreateDocument()));
        else Assert.Throws<ArgumentException>(() => spec.ToPageSetup());
    }

    [Fact]
    public void LinedBackgroundUsesEightMillimetreSpacing()
    {
        var setup = NewPageSpec.FromPreset(PaperSize.A4, PageOrientation.Portrait, PageBackground.Lined).ToPageSetup();
        Assert.Equal(8, setup.Background.SpacingMm);
        Assert.Equal(PageBackground.DefaultLineColor, setup.Background.LineColor);
    }
}
