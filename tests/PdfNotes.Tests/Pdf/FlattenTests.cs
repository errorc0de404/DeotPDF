using System.Collections.Immutable;
using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfNotes.Tests.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

/// <summary>D12 (spec 14.5): Flatten export merges appearances into the page, removes all app data, keeps source text and vectors.</summary>
public sealed class FlattenTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    public FlattenTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    private static PdfDictionary? Resolve(PdfItem item) => item is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value as PdfDictionary : item as PdfDictionary;
    private static string Text(byte[] pdf, int page) { using var d = new PdfiumDocument(pdf); return string.Concat(d.ExtractText(page).Select(c => c.Text)); }

    /// <summary>Mean BGRA channel difference of the two files' rendered pages (annotations drawn).</summary>
    private static double Difference(byte[] a, byte[] b, int page)
    {
        using var x = new PdfiumDocument(a); using var y = new PdfiumDocument(b);
        var p = x.Render(page, 1.5, annotations: true); var q = y.Render(page, 1.5, annotations: true);
        Assert.Equal((p.Width, p.Height), (q.Width, q.Height));
        return p.Bgra.Zip(q.Bgra, (m, n) => Math.Abs(m - n)).Average();
    }

    private static double Difference(byte[] a, byte[] b) { using var x = new PdfiumDocument(a); return Enumerable.Range(0, x.PageCount).Max(i => Difference(a, b, i)); }

    /// <summary>A source PDF page with every annotation kind, an eraser fragment and shapes, shrunk to 80% and copied.</summary>
    private (PdfSourceDocument Source, DocumentModel Model, Resource Image) Everything()
    {
        string path = Path.Combine(folder, "source.pdf"); ProbeSamplePdf.Create(path, 2);
        var source = PdfSourceDocument.Open(path); var model = source.CreateModel();
        var all = TestModels.AllKinds(out var resource).AllAnnotations.ToImmutableList();
        var fragment = TestModels.Ink() with { Points = [], Outline = [[new(350, 350), new(400, 350), new(400, 400), new(350, 400)]] };
        var annotations = all.Add(fragment).AddRange(ShapeTests.AllShapes().Select(s => s with { Id = Guid.NewGuid() }));
        model = model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = annotations }), Resources = model.Resources.Add(resource.Id, resource) };
        var s = new DocumentSession(model); var first = model.Pages[0].Id;
        s.Execute(PageTransforms.Shrink(s.Current, [first], 80));
        var (copy, ids) = PageClipboard.Capture(s.Current, [first], PageCopyMode.WithAnnotations).Paste(s.Current, 2); s.Execute(copy);
        s.Execute(PageEdits.RotatePages(s.Current, ids, true));
        s.Execute(PageTransforms.Resize(s.Current, [model.Pages[1].Id], 500, 300, PageResizeMode.ClipContent));
        return (source, s.Current, resource);
    }

    [Fact]
    public void FlattenMatchesTheEditableLookAndRemovesEveryEditData()
    {
        var (source, model, image) = Everything();
        using var _ = source;
        byte[] editable = EditablePdfWriter.Write(source.Bytes, source.Pages, model);
        var result = FlattenWriter.Write(source.Bytes, source.Pages, model);
        byte[] flat = result.Pdf;
        FlattenWriter.Verify(flat, model.Pages.Count);
        Assert.Equal(model.AllAnnotations.Count(), result.MergedApp);
        Assert.Equal(0, result.KeptForeign);

        // Same look as the editable file in a viewer (source content, transforms, transparency groups, images, text).
        double difference = Difference(editable, flat);
        Assert.True(difference < 1, $"mean channel difference {difference}");
        // Source text stays text, the user text layer stays searchable, the papers keep their sizes.
        Assert.Contains("Original PDF", Text(flat, 0));
        Assert.Contains("한글 English", Text(flat, 0));
        Assert.Contains("Original PDF", Text(flat, 2));
        var geometry = PdfPageGeometryReader.Read(flat);
        for (int i = 0; i < model.Pages.Count; i++)
        {
            Assert.Equal(model.Pages[i].Setup.WidthPt, geometry[i].Box.WidthPt, 3);
            Assert.Equal(model.Pages[i].Setup.HeightPt, geometry[i].Box.HeightPt, 3);
            Assert.Equal(model.Pages[i].Setup.Rotation, geometry[i].Rotation);
        }

        // Nothing for editing is left: no hidden image original, no retained source, no app objects or keys.
        using (var doc = PdfReader.Open(new MemoryStream(flat, false), PdfDocumentOpenMode.Import))
        {
            byte[] png = image.Payload.ToArray();
            foreach (var item in doc.Internals.GetAllObjects())
                if (item is PdfDictionary { Stream: { } stream } d)
                {
                    Assert.False(stream.UnfilteredValue.AsSpan().SequenceEqual(png), "image original left behind");
                    Assert.DoesNotContain(d.Elements.Keys, k => k.StartsWith("/PdfNotes", StringComparison.Ordinal));
                }
            Assert.All(doc.Pages.Cast<PdfPage>(), p => Assert.Null(p.Elements.GetArray("/Annots")));
        }
        Assert.True(flat.Length < editable.Length, $"{flat.Length} vs {editable.Length}");

        // Reopened in the app it is an ordinary PDF: nothing editable, nothing read-only, new annotations can be added and saved.
        string path = Path.Combine(folder, "flat.pdf"); File.WriteAllBytes(path, flat);
        using var reopened = PdfSourceDocument.Open(path);
        var plain = reopened.CreateModel();
        Assert.Empty(plain.AllAnnotations);
        Assert.Equal(0, reopened.AppData.ReadOnlyCount);
        var edit = new DocumentSession(plain);
        edit.Execute(DocumentEdits.AddAnnotation(edit.Current, plain.Pages[0].Id, TestModels.Ink(150)));
        byte[] again = EditablePdfWriter.Write(reopened.Bytes, reopened.Pages, edit.Current);
        EditablePdfWriter.Verify(again, edit.Current);
        Assert.Contains("한글 English", Text(again, 0));
    }

    [Fact]
    public void ForeignAppearancesAreMergedAndAnnotationsWithoutOneAreKept()
    {
        string path = Path.Combine(folder, "foreign.pdf");
        ForeignAnnotationSample.Create(path);
        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            // A square that only has its dictionary (viewers draw it from /C): it cannot be merged and is kept.
            var bare = new PdfDictionary(doc);
            bare.Elements.SetName("/Type", "/Annot"); bare.Elements.SetName("/Subtype", "/Square"); bare.Elements.SetInteger("/F", 4);
            bare.Elements["/Rect"] = new PdfArray(doc, new PdfReal(300), new PdfReal(100), new PdfReal(360), new PdfReal(140));
            doc.Internals.AddObject(bare); doc.Pages[0].Elements.GetArray("/Annots")!.Elements.Add(bare.Reference!);
            doc.Save(path);
        }
        using var source = PdfSourceDocument.Open(path);
        var model = source.CreateModel();
        var s = new DocumentSession(model);
        s.Execute(DocumentEdits.AddAnnotation(s.Current, model.Pages[0].Id, TestModels.Ink(300)));
        byte[] editable = EditablePdfWriter.Write(source.Bytes, source.Pages, s.Current);
        var result = FlattenWriter.Flatten(editable);
        FlattenWriter.Verify(result.Pdf, model.Pages.Count);
        Assert.Equal(1, result.MergedApp);
        Assert.Equal(1, result.KeptForeign);
        Assert.True(result.MergedForeign >= 13, $"{result.MergedForeign}");

        using var flat = PdfReader.Open(new MemoryStream(result.Pdf, false), PdfDocumentOpenMode.Import);
        var left = flat.Pages[0].Elements.GetArray("/Annots")!.Elements.Select(Resolve).ToArray();
        // Kept: the square without appearance and the hidden ink. Popups of merged parents are gone.
        Assert.Equal(["/Square", "/Ink"], left.Select(a => a!.Elements.GetName("/Subtype")).Order().Reverse().ToArray());
        Assert.Null(flat.Pages[1].Elements.GetArray("/Annots"));
        // The foreign highlight has /CA 0.5 on the annotation: merged, it must stay translucent (white under it turns light
        // yellow, B ≈ 128) instead of an opaque yellow bar (B = 0) hiding the source text. PDFium ignores annotation /CA,
        // so this is checked on the merged content itself.
        using (var engine = new PdfiumDocument(result.Pdf))
        {
            var page = engine.Render(0, 1, annotations: true);
            int brightestBlue = 0;
            for (int y = 792 - 682; y < 792 - 666; y++)
                for (int x = 71; x < 327; x++) brightestBlue = Math.Max(brightestBlue, page.Bgra[y * page.Stride + x * 4]);
            Assert.InRange(brightestBlue, 100, 160);
        }
        // Everything else looks as the editable file does in PDFium (the kept square has no appearance in either file).
        double difference = Difference(editable, result.Pdf);
        Assert.True(difference < 3, $"mean channel difference {difference}");
    }

    [Fact]
    public void ReadOnlyAppAppearancesAreFlattenedToo()
    {
        var model = TestModels.Empty();
        var ink = TestModels.Ink(40);
        model = model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = [ink] }) };
        byte[] editable = EditablePdfWriter.Write(null, null, model);
        // Another program changed the appearance: the app shows it read-only and keeps it when saving.
        using (var doc = PdfReader.Open(new MemoryStream(editable, false), PdfDocumentOpenMode.Modify))
        {
            var annot = Resolve(doc.Pages[0].Elements.GetArray("/Annots")!.Elements[0])!;
            annot.Elements.SetString(EditablePdfKeys.AppearanceHash, "changed");
            using var buffer = new MemoryStream(); doc.Save(buffer, false); editable = buffer.ToArray();
        }
        var read = EditablePdfReader.Read(editable, PdfPageGeometryReader.Read(editable));
        Assert.Equal(1, read.ReadOnlyCount);
        byte[] resaved = EditablePdfWriter.Write(editable, PdfPageGeometryReader.Read(editable), read.Model);
        Assert.Equal(1, EditablePdfReader.Read(resaved, PdfPageGeometryReader.Read(resaved)).ReadOnlyCount); // survives a save
        var result = FlattenWriter.Flatten(resaved);
        FlattenWriter.Verify(result.Pdf, 1);
        Assert.Equal(1, result.MergedApp);
        Assert.True(Difference(resaved, result.Pdf) < 1);
    }
}
