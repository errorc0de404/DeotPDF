using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

public sealed class ImagePdfTests
{
    public static byte[] Sample(string media = "image/png")
    {
        byte[] pixels = new byte[8 * 4 * 4];
        for (int y = 0; y < 4; y++) for (int x = 0; x < 8; x++)
        {
            int i = (y * 8 + x) * 4;
            pixels[i] = y < 2 ? (byte)0 : (byte)255;
            pixels[i + 2] = y < 2 ? (byte)255 : (byte)0;
            pixels[i + 3] = x < 4 ? (byte)0 : y < 2 ? (byte)255 : (byte)128;
        }
        var bitmap = BitmapSource.Create(8, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 32);
        BitmapEncoder encoder = media switch { "image/jpeg" => new JpegBitmapEncoder(), "image/bmp" => new BmpBitmapEncoder(), _ => new PngBitmapEncoder() };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream(); encoder.Save(buffer); return buffer.ToArray();
    }
    private static DocumentModel Model(byte[] payload, string media, int rotation = 0)
    {
        var model = DocumentModel.Create([new(400, 500, rotation, PageSource.Generated, PageBackground.Blank)]);
        var resource = Resource.Create(Guid.NewGuid(), media, payload);
        var image = new ImageAnnotation(Guid.NewGuid(), false, Affine.Rigid(.3, 70, 80), resource.Id, new(0, 0, 160, 80));
        var s = new DocumentSession(model);
        s.Execute(DocumentEdits.AddAnnotation(model, model.Pages[0].Id, image, resource));
        return s.Current;
    }
    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/bmp")]
    public void RealImageBytesAndAppearanceRoundTripAndRepeatedSaveStayBounded(string media)
    {
        var model = Model(Sample(media), media, 90);
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        EditablePdfWriter.Verify(pdf, model);
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(1, read.RestoredCount); Assert.Equal(0, read.ReadOnlyCount);
        Assert.Equal(model.Resources.Values.Single().Payload.ToArray(), read.Model.Resources.Values.Single().Payload.ToArray());
        int firstLength = pdf.Length;
        var lengths = new List<int> { firstLength };
        for (int i = 0; i < 5; i++)
        {
            pdf = EditablePdfWriter.Write(pdf, PdfPageGeometryReader.Read(pdf), read.Model);
            read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
            Assert.Equal(1, read.RestoredCount); Assert.Single(read.Model.Resources);
            lengths.Add(pdf.Length);
        }
        // PDFsharp adds document bookkeeping on the first rewrite; subsequent writes must stay stable.
        Assert.True(lengths.Skip(1).Max() - lengths.Skip(1).Min() < 100, string.Join(",", lengths));
        Assert.InRange(lengths.Max() - firstLength, 0, 2000);
    }
    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public void PngOrientationTransparencyAndNoDoubleDisplay(int rotation)
    {
        var model = Model(Sample(), "image/png", rotation);
        var image = (ImageAnnotation)model.AllAnnotations.Single();
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        using var full = new PdfiumDocument(pdf);
        using var background = new PdfiumDocument(read.BackgroundPdf);
        var raster = full.Render(0, 2, annotations: true);
        var blank = background.Render(0, 2, annotations: true);
        (byte B, byte G, byte R) Pixel(RenderedPage rendered, Point2 local)
        {
            var p = model.Pages[0].Setup.Frame.CanonicalToDisplay.Transform(image.Transform.Transform(local));
            int index = (int)(p.Y * 2) * rendered.Stride + (int)(p.X * 2) * 4;
            return (rendered.Bgra[index], rendered.Bgra[index + 1], rendered.Bgra[index + 2]);
        }
        var transparent = Pixel(raster, new(20, 20));
        Assert.True(transparent.R > 245 && transparent.G > 245 && transparent.B > 245);
        var red = Pixel(raster, new(130, 20));
        Assert.True(red.R > 240 && red.G < 20 && red.B < 20, red.ToString());
        var blue = Pixel(raster, new(130, 60));
        Assert.True(blue.B > 245 && blue.R is > 110 and < 145 && blue.G is > 110 and < 145, blue.ToString());
        Assert.Equal(((byte)255, (byte)255, (byte)255), Pixel(blank, new(130, 20)));
    }
    [Fact]
    public void MissingOriginalResourceKeepsAppearanceReadOnlyAndResavePreservesIt()
    {
        var model = Model(Sample(), "image/png");
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        using (var stream = new MemoryStream(pdf)) using (var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify))
        {
            doc.Internals.Catalog.Elements.Remove("/PdfNotesResources");
            using var output = new MemoryStream(); doc.Save(output, false); pdf = output.ToArray();
        }
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(1, read.ReadOnlyCount); Assert.Empty(read.Model.AllAnnotations);
        byte[] saved = EditablePdfWriter.Write(pdf, PdfPageGeometryReader.Read(pdf), read.Model);
        var again = EditablePdfReader.Read(saved, PdfPageGeometryReader.Read(saved));
        Assert.Equal(1, again.ReadOnlyCount);
        using var a = new PdfiumDocument(pdf); using var b = new PdfiumDocument(saved);
        Assert.Equal(a.Render(0, annotations: true).Bgra, b.Render(0, annotations: true).Bgra);
    }
    [Fact]
    public void ModifiedRasterFingerprintDoesNotHideChangedAppearance()
    {
        var model = Model(Sample(), "image/png");
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        using (var stream = new MemoryStream(pdf)) using (var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify))
        {
            var item = doc.Pages[0].Elements.GetArray("/Annots")!.Elements[0];
            var annot = (PdfDictionary)((PdfReference)item).Value;
            var image = annot.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")!.Elements.GetDictionary("/Image")!;
            image.Elements.SetInteger("/Width", 9);
            using var output = new MemoryStream(); doc.Save(output, false); pdf = output.ToArray();
        }
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(1, read.ReadOnlyCount); Assert.Empty(read.Model.AllAnnotations);
    }
}
