using System.IO;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Rendering;
using PdfNotes.Pdf.Writing;

namespace PdfNotes.Tests.Pdf;

/// <summary>D03: opening/refusing files, rendering regions and rotations, generated note backgrounds.</summary>
public sealed class PdfOpenAndRenderTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private readonly string mixed;

    public PdfOpenAndRenderTests()
    {
        Directory.CreateDirectory(folder);
        mixed = Path.Combine(folder, "mixed.pdf");
        ProbeSamplePdf.CreateMixed(mixed);
    }

    public void Dispose() => Directory.Delete(folder, true);

    private static PdfOpenFailure OpenFailure(string path) =>
        Assert.Throws<PdfOpenException>(() => PdfSourceDocument.Open(path).Dispose()).Failure;

    [Fact]
    public void MixedPdfBecomesModelWithCanonicalSizesRotationsAndSourceIndices()
    {
        using var source = PdfSourceDocument.Open(mixed);
        var model = source.CreateModel();
        Assert.Null(ModelValidator.ValidateDocument(model));
        Assert.Equal(ProbeSamplePdf.MixedPages.Length, model.Pages.Count);
        for (int i = 0; i < model.Pages.Count; i++)
        {
            var spec = ProbeSamplePdf.MixedPages[i];
            var setup = model.Pages[i].Setup;
            double w = (spec.Crop?.Width ?? spec.Width) * spec.UserUnit, h = (spec.Crop?.Height ?? spec.Height) * spec.UserUnit;
            Assert.Equal(w, setup.WidthPt, 6);
            Assert.Equal(h, setup.HeightPt, 6);
            Assert.Equal(spec.Rotate, setup.Rotation);
            Assert.Equal(PageSource.Pdf(i), setup.Source);
        }
        Assert.Equal(new FileInfo(mixed).Length, source.FileLength);
    }

    [Fact]
    public void UnsupportedInputsAreRefusedWithSpecificReasons()
    {
        Assert.Equal(PdfOpenFailure.NotFound, OpenFailure(Path.Combine(folder, "missing.pdf")));

        string text = Path.Combine(folder, "not.pdf");
        File.WriteAllText(text, "hello, this is not a PDF");
        Assert.Equal(PdfOpenFailure.NotPdf, OpenFailure(text));

        string truncated = Path.Combine(folder, "truncated.pdf");
        File.WriteAllBytes(truncated, File.ReadAllBytes(mixed).AsSpan(0, 400).ToArray());
        Assert.Equal(PdfOpenFailure.Corrupt, OpenFailure(truncated));

        string password = Path.Combine(folder, "password.pdf");
        ProbeSamplePdf.CreatePasswordProtected(mixed, password);
        Assert.Equal(PdfOpenFailure.PasswordProtected, OpenFailure(password));

        string encrypted = Path.Combine(folder, "owner.pdf");
        ProbeSamplePdf.CreateOwnerEncrypted(mixed, encrypted);
        Assert.Equal(PdfOpenFailure.Encrypted, OpenFailure(encrypted));

        string signed = Path.Combine(folder, "signed.pdf");
        ProbeSamplePdf.CreateSigned(mixed, signed);
        Assert.Equal(PdfOpenFailure.Signed, OpenFailure(signed));

        using (new FileStream(mixed, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(PdfOpenFailure.AccessDenied, OpenFailure(mixed));
    }

    [Fact]
    public void OpenedSourceKeepsRenderingAfterTheFileIsReplacedOrDeleted()
    {
        string copy = Path.Combine(folder, "copy.pdf");
        File.Copy(mixed, copy);
        using var source = PdfSourceDocument.Open(copy);
        File.WriteAllText(copy, "replaced");
        File.Delete(copy);
        using var renderer = new PageRenderer(source);
        var page = renderer.Render(source.CreateModel().Pages[0].Setup, 0.5, null, default);
        Assert.Contains(page.Bgra, b => b < 128); // the original text is still drawn
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void RegionRenderMatchesTheSameAreaOfAFullRender(int pageIndex)
    {
        using var source = PdfSourceDocument.Open(mixed);
        using var renderer = new PageRenderer(source);
        var setup = source.CreateModel().Pages[pageIndex].Setup;
        const double ppp = 1.5;
        var full = renderer.Render(setup, ppp, null, default);
        Assert.Equal(PageRenderer.PixelSize(setup, ppp), (full.Width, full.Height));
        // A region with an odd offset around the first dark (text) pixel, so it must contain content.
        int first = Array.FindIndex(full.Bgra, b => b < 100);
        int cx = first % full.Stride / 4, cy = first / full.Stride;
        var region = new PixelRect(Math.Clamp(cx - 151, 0, full.Width - 300), Math.Clamp(cy - 97, 0, full.Height - 200), 300, 200);
        var tile = renderer.Render(setup, ppp, region, default);
        Assert.Equal((300, 200), (tile.Width, tile.Height));
        long difference = 0;
        for (int y = 0; y < region.Height; y++)
            for (int x = 0; x < region.Width * 4; x++)
                difference += Math.Abs(tile.Bgra[y * tile.Stride + x] - full.Bgra[(y + region.Y) * full.Stride + region.X * 4 + x]);
        Assert.InRange(difference / (double)(region.Area * 4), 0, 0.5);
        Assert.Contains(tile.Bgra, b => b < 128); // the region is not blank
    }

    [Fact]
    public void PixelSizeMatchesPdfiumPageSizeIncludingUserUnit()
    {
        using var source = PdfSourceDocument.Open(mixed);
        var model = source.CreateModel();
        for (int i = 0; i < model.Pages.Count; i++)
        {
            var native = source.Engine.Render(i, 0.4, userUnit: source.Pages[i].Box.UserUnit);
            var (w, h) = PageRenderer.PixelSize(model.Pages[i].Setup, 0.4);
            Assert.InRange(Math.Abs(native.Width - w), 0, 1);
            Assert.InRange(Math.Abs(native.Height - h), 0, 1);
        }
    }

    [Fact]
    public void ModelRotationDifferentFromSourceRotatesTheRender()
    {
        using var source = PdfSourceDocument.Open(mixed);
        using var renderer = new PageRenderer(source);
        var setup = source.CreateModel().Pages[0].Setup;
        var upright = renderer.Render(setup, 0.5, null, default);
        var turned = renderer.Render(setup with { Rotation = 90 }, 0.5, null, default);
        Assert.Equal((upright.Height, upright.Width), (turned.Width, turned.Height));
        // Pixel (x, y) of the upright page appears at (H - 1 - y, x) after a clockwise quarter turn.
        long difference = 0, count = 0;
        for (int y = 0; y < upright.Height; y += 3)
            for (int x = 0; x < upright.Width; x += 3)
            {
                int tx = upright.Height - 1 - y, ty = x;
                difference += Math.Abs(upright.Bgra[y * upright.Stride + x * 4] - turned.Bgra[ty * turned.Stride + tx * 4]);
                count++;
            }
        Assert.InRange(difference / (double)count, 0, 6);
    }

    [Theory]
    [InlineData(PageBackgroundKind.Grid)]
    [InlineData(PageBackgroundKind.Lined)]
    [InlineData(PageBackgroundKind.Blank)]
    public void GeneratedBackgroundIsRealPageContentAtTheSpecifiedSpacing(PageBackgroundKind kind)
    {
        var background = kind switch { PageBackgroundKind.Grid => PageBackground.Grid, PageBackgroundKind.Lined => PageBackground.Lined, _ => PageBackground.Blank };
        var setup = NewPageSpec.FromPreset(PaperSize.A5, PageOrientation.Portrait, background).ToPageSetup();
        using var renderer = new PageRenderer((PdfSourceDocument?)null);
        const double ppp = 4;
        var page = renderer.Render(setup, ppp, null, default);
        Assert.Equal(PageRenderer.PixelSize(setup, ppp), (page.Width, page.Height));
        byte Pixel(double xPt, double yPt) => page.Bgra[(int)(yPt * ppp) * page.Stride + (int)(xPt * ppp) * 4];
        double step = Units.MillimetersToPoints(kind == PageBackgroundKind.Grid ? 5 : 8);
        double between = step * 2.5, onLine = step * 3;
        Assert.Equal(255, Pixel(between, between)); // paper stays white
        bool horizontal = kind != PageBackgroundKind.Blank, vertical = kind == PageBackgroundKind.Grid;
        Assert.Equal(horizontal, Pixel(between, onLine) < 245);
        Assert.Equal(vertical, Pixel(onLine, between) < 245);
        // Count the drawn horizontal lines down a column between vertical lines: one per spacing step.
        int x = (int)(between * ppp), lines = 0;
        bool inLine = false;
        for (int y = 0; y < page.Height; y++)
        {
            bool dark = page.Bgra[y * page.Stride + x * 4] < 245;
            if (dark && !inLine) lines++;
            inLine = dark;
        }
        Assert.Equal(horizontal ? GeneratedPageWriter.Positions(setup.HeightPt, step).Count() : 0, lines);
        Assert.Equal(horizontal ? (int)Math.Ceiling(setup.HeightPt / step - 1e-6) - 1 : 0, lines); // edges excluded
    }

    [Fact]
    public void GeneratedPageRotationIsWrittenAsPageRotation()
    {
        var setup = NewPageSpec.FromPreset(PaperSize.A4, PageOrientation.Portrait, PageBackground.Lined).ToPageSetup() with { Rotation = 90 };
        using var renderer = new PageRenderer((PdfSourceDocument?)null);
        var page = renderer.Render(setup, 0.5, null, default);
        Assert.Equal(PageRenderer.PixelSize(setup, 0.5), (page.Width, page.Height));
        Assert.True(page.Width > page.Height);
        // Ruled lines are horizontal on the unrotated page, so they become vertical on screen.
        double step = Units.MillimetersToPoints(8) * 0.5;
        int column = (int)Math.Round(page.Width - 1 - 3 * step), row = page.Height / 2;
        Assert.Contains(Enumerable.Range(column - 1, 3), x => page.Bgra[row * page.Stride + x * 4] < 245);
    }
}
