using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfSharp.Pdf;

namespace PdfNotes.Tests.Pdf;

/// <summary>
/// A scanned page's image in a compression PDFsharp cannot decode (CCITT fax, also behind Flate; JBIG2). Saving such a
/// PDF threw while fingerprinting the page background (found with a real lecture PDF, 2026-11-15): the stored bytes are
/// hashed instead.
/// </summary>
public sealed class UndecodableStreamTests : IDisposable
{
    private readonly string folder = TestFolders.New();

    public void Dispose() => Directory.Delete(folder, true);

    private string Scan(string name, Action<PdfDictionary> filter)
    {
        string path = Path.Combine(folder, name);
        using var document = new PdfDocument();
        var page = document.AddPage();
        var image = new PdfDictionary(document);
        image.Elements.SetName("/Type", "/XObject");
        image.Elements.SetName("/Subtype", "/Image");
        image.Elements.SetInteger("/Width", 8);
        image.Elements.SetInteger("/Height", 8);
        image.Elements.SetInteger("/BitsPerComponent", 1);
        image.Elements.SetName("/ColorSpace", "/DeviceGray");
        image.CreateStream([0x00, 0x10, 0x01, 0x00, 0x10, 0x01, 0x00, 0x10, 0x01]);
        filter(image);
        document.Internals.AddObject(image);
        var xobjects = new PdfDictionary(document);
        xobjects.Elements["/Im0"] = image.Reference!;
        page.Resources.Elements["/XObject"] = xobjects;
        document.Save(path);
        return path;
    }

    [Theory]
    [InlineData("/CCITTFaxDecode")]
    [InlineData("/JBIG2Decode")]
    public async Task APdfWithAnImagePdfSharpCannotDecodeIsSavedAndOpensAgain(string filter)
    {
        string path = Scan("scan.pdf", image => image.Elements.SetName("/Filter", filter));
        string copy = Path.Combine(folder, "copy.pdf");
        var stroke = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(10, 10), new(60, 40)], 2, new RgbColor(255, 0, 0), 1);
        using (var document = await OpenDocument.OpenPdfAsync(path))
        {
            var model = document.Session.Current;
            document.Session.Execute(DocumentEdits.AddAnnotation(model, model.Pages[0].Id, stroke, label: "undo.pen"));
            await document.WriteCopyAsync(copy);
        }
        // The copy opens as the app's own file (its background fingerprint holds), with the stroke, and saves again.
        using var reopened = await OpenDocument.OpenPdfAsync(copy);
        Assert.Equal(stroke.Id, Assert.Single(reopened.Session.Current.Pages[0].Annotations).Id);
        Assert.False(reopened.Session.Current.Pages[0].Annotations[0].Locked);
        await reopened.WriteCopyAsync(Path.Combine(folder, "again.pdf"));
    }
}
