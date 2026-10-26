using System.Collections.Immutable;
using System.IO;
using System.Windows.Threading;
using PdfNotes.App.Services;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfNotes.Tests.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

/// <summary>
/// D12 (spec 14, 18): every kind together through repeated saves, read-only fallbacks that survive saves and Flatten,
/// and the document-level guarantees of saving, backup copies and Flatten export (path, unsaved mark, failures, order).
/// </summary>
public sealed class SaveCompatibilityTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    public SaveCompatibilityTests() => Directory.CreateDirectory(folder);
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private static void Same(DocumentModel a, DocumentModel b)
    {
        var x = SchemaV5.Serialize(a); var y = SchemaV5.Serialize(b); Assert.Equal(x.Header, y.Header);
        Assert.Equal(x.Annotations.Select(e => Convert.ToBase64String(e.Envelope)), y.Annotations.Select(e => Convert.ToBase64String(e.Envelope)));
    }

    private static int AppAnnotations(byte[] pdf)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf, false), PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Sum(p => p.Elements.GetArray("/Annots")?.Elements.Count(i =>
            (i is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : i) is PdfDictionary d && d.Elements.ContainsKey(EditablePdfKeys.Envelope)) ?? 0);
    }

    [Fact]
    public void EveryKindWithShapesFragmentsAndPagePlacementSurvivesTenSaves()
    {
        string path = Path.Combine(folder, "source.pdf"); ProbeSamplePdf.Create(path, 2);
        using var source = PdfSourceDocument.Open(path);
        var model = source.CreateModel();
        var all = TestModels.AllKinds(out var resource).AllAnnotations.ToImmutableList()
            .Add(TestModels.Ink() with { Points = [], Outline = [[new(350, 350), new(400, 350), new(400, 400), new(350, 400)]] })
            .AddRange(ShapeTests.AllShapes().Select(s => s with { Id = Guid.NewGuid() }));
        model = model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = all }), Resources = model.Resources.Add(resource.Id, resource) };
        var s = new DocumentSession(model);
        s.Execute(PageTransforms.Shrink(s.Current, [model.Pages[0].Id], 70, alignment: 0));
        s.Execute(PageTransforms.Resize(s.Current, [model.Pages[1].Id], 120, 150, PageResizeMode.ClipContent)); // cut, grown again after reopening below
        var expected = s.Current;
        byte[] pdf = EditablePdfWriter.Write(source.Bytes, source.Pages, expected);
        var sizes = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            EditablePdfWriter.Verify(pdf, expected);
            var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
            Same(expected, read.Model);
            Assert.Equal(0, read.ReadOnlyCount);
            Assert.Equal(expected.AllAnnotations.Count(), AppAnnotations(pdf)); // no duplicates
            sizes.Add(pdf.Length);
            pdf = EditablePdfWriter.Write(pdf, PdfPageGeometryReader.Read(pdf), read.Model);
        }
        Assert.True(sizes.Max() - sizes.Min() < 256, string.Join(",", sizes));
        // Reopened and grown back: the cut source content returns once (placement is not applied twice).
        string saved = Path.Combine(folder, "saved.pdf"); File.WriteAllBytes(saved, pdf);
        using var reopened = PdfSourceDocument.Open(saved);
        var edit = new DocumentSession(reopened.CreateModel());
        edit.Execute(PageTransforms.Resize(edit.Current, [model.Pages[1].Id], 215.9, 279.4, PageResizeMode.ClipContent));
        byte[] grown = EditablePdfWriter.Write(reopened.Bytes, reopened.Pages, edit.Current);
        EditablePdfWriter.Verify(grown, edit.Current);
        using var engine = new PdfiumDocument(grown);
        Assert.Contains("Original PDF", string.Concat(engine.ExtractText(1).Select(c => c.Text)));
        Assert.Contains("Original PDF", string.Concat(engine.ExtractText(0).Select(c => c.Text)));
    }

    [Fact]
    public void UnsupportedMetadataStaysVisibleReadOnlyThroughSavesAndFlatten()
    {
        var model = TestModels.Empty();
        model = model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = [TestModels.Ink(40, 6), TestModels.Ink(120, 6)] }) };
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        // A newer app version wrote this file: the header has a schema this app does not know.
        using (var doc = PdfReader.Open(new MemoryStream(pdf, false), PdfDocumentOpenMode.Modify))
        {
            var header = doc.Internals.Catalog.Elements.GetDictionary(EditablePdfKeys.Header)!;
            string json = System.Text.Encoding.UTF8.GetString(header.Stream.UnfilteredValue).Replace("\"schemaVersion\":6", "\"schemaVersion\":99");
            header.Stream.Value = System.Text.Encoding.UTF8.GetBytes(json); header.Elements.Remove("/Filter");
            using var buffer = new MemoryStream(); doc.Save(buffer, false); pdf = buffer.ToArray();
        }
        using var original = new PdfiumDocument(pdf);
        var look = original.Render(0, 1, annotations: true).Bgra;
        for (int i = 0; i < 3; i++)
        {
            var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
            if (i == 0) { Assert.Equal(HeaderStatus.Unsupported, read.HeaderStatus); Assert.Empty(read.Model.AllAnnotations); }
            Assert.Equal(2, read.ReadOnlyCount);
            Assert.Equal(i, read.Model.AllAnnotations.Count());
            // A new stroke is added and saved next to the read-only ones; nothing disappears or doubles.
            var session = new DocumentSession(read.Model);
            session.Execute(DocumentEdits.AddAnnotation(session.Current, read.Model.Pages[0].Id, TestModels.Ink(200 + i * 30)));
            pdf = EditablePdfWriter.Write(pdf, PdfPageGeometryReader.Read(pdf), session.Current);
            var again = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
            Assert.Equal(2, again.ReadOnlyCount);
            Assert.Equal(i + 1, again.Model.AllAnnotations.Count());
        }
        using var resaved = new PdfiumDocument(pdf);
        var after = resaved.Render(0, 1, annotations: true).Bgra;
        // The two original strokes are still drawn where they were (their pixels did not turn white).
        int lost = look.Select((b, k) => (b, k)).Count(p => p.b < 128 && after[p.k] > 200);
        Assert.Equal(0, lost);
        var flat = FlattenWriter.Flatten(pdf);
        FlattenWriter.Verify(flat.Pdf, 1);
        Assert.Equal(5, flat.MergedApp); // the 2 read-only strokes and the 3 editable ones
    }

    private static OpenDocument Note()
    {
        var doc = OpenDocument.CreateNote(NewPageSpec.Default);
        doc.Session.Execute(DocumentEdits.AddAnnotation(doc.Session.Current, doc.Session.Current.Pages[0].Id, TestModels.Ink()));
        return doc;
    }

    [Fact]
    public async Task BackupCopiesAndFlattenExportKeepTheDocumentPathAndUnsavedMark()
    {
        using var doc = Note();
        string file = Path.Combine(folder, "note.pdf");
        await doc.SaveAsync(file);
        doc.Session.Execute(DocumentEdits.AddAnnotation(doc.Session.Current, doc.Session.Current.Pages[0].Id, TestModels.Ink(80)));
        Assert.True(doc.Session.IsDirty);
        byte[] onDisk = File.ReadAllBytes(file);

        string copy = Path.Combine(folder, "note_backup.pdf");
        long revision = await doc.WriteCopyAsync(copy);
        Assert.Equal(doc.Session.Revision, revision);
        Assert.Equal((file, true), (doc.FilePath, doc.Session.IsDirty));
        Assert.Equal(onDisk, File.ReadAllBytes(file));
        Assert.Equal(2, EditablePdfReader.Read(File.ReadAllBytes(copy), PdfPageGeometryReader.Read(File.ReadAllBytes(copy))).Model.AllAnnotations.Count());

        string flat = Path.Combine(folder, "note_flattened.pdf");
        Assert.True(await doc.ExportFlattenedAsync(flat, _ => throw new InvalidOperationException("no foreign annotations to ask about")));
        Assert.Equal((file, true), (doc.FilePath, doc.Session.IsDirty));
        FlattenWriter.Verify(File.ReadAllBytes(flat), 1);
        Assert.False(doc.FileChangedExternally());
    }

    [Fact]
    public async Task FlattenCancelledForKeptForeignAnnotationsWritesNothing()
    {
        string path = Path.Combine(folder, "foreign.pdf");
        ForeignAnnotationSample.Create(path);
        using (var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var bare = new PdfDictionary(pdf);
            bare.Elements.SetName("/Type", "/Annot"); bare.Elements.SetName("/Subtype", "/Square");
            bare.Elements["/Rect"] = new PdfArray(pdf, new PdfReal(300), new PdfReal(100), new PdfReal(360), new PdfReal(140));
            pdf.Internals.AddObject(bare); pdf.Pages[0].Elements.GetArray("/Annots")!.Elements.Add(bare.Reference!);
            pdf.Save(path);
        }
        using var doc = await OpenDocument.OpenPdfAsync(path);
        string target = Path.Combine(folder, "foreign_flattened.pdf");
        int asked = 0;
        Assert.False(await doc.ExportFlattenedAsync(target, kept => { asked = kept; return false; }));
        Assert.Equal(1, asked);
        Assert.False(File.Exists(target));
        Assert.True(await doc.ExportFlattenedAsync(target, _ => true));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task ALockedTargetFailsWithoutChangingTheFileOrTheDocument()
    {
        using var doc = Note();
        string file = Path.Combine(folder, "locked.pdf");
        await doc.SaveAsync(file);
        doc.Session.Execute(DocumentEdits.AddAnnotation(doc.Session.Current, doc.Session.Current.Pages[0].Id, TestModels.Ink(90)));
        byte[] before = File.ReadAllBytes(file);
        string other = Path.Combine(folder, "other.pdf");
        File.WriteAllBytes(other, before);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(other, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<SaveFailedException>(() => doc.SaveAsync(file));
            Assert.Equal(SaveFailure.Locked, error.Failure);
            await Assert.ThrowsAsync<SaveFailedException>(() => doc.ExportFlattenedAsync(other, _ => true));
        }
        // A merged export never replaces the document's own file (spec 14.5, D14g).
        await Assert.ThrowsAsync<InvalidOperationException>(() => doc.ExportFlattenedAsync(file, _ => true));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(before, File.ReadAllBytes(other));
        Assert.True(doc.Session.IsDirty);
        Assert.Equal(file, doc.FilePath);
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Fact]
    public async Task OverlappingSavesOfOneDocumentEndWithTheNewestState()
    {
        using var doc = Note();
        string file = Path.Combine(folder, "order.pdf");
        var first = doc.SaveAsync(file);       // older snapshot
        var copy = doc.WriteCopyAsync(file);    // an automatic backup of the same state meanwhile
        doc.Session.Execute(DocumentEdits.AddAnnotation(doc.Session.Current, doc.Session.Current.Pages[0].Id, TestModels.Ink(60)));
        long newest = doc.Session.Revision;
        var second = doc.SaveAsync(file);      // newer snapshot
        await Task.WhenAll(first, copy, second);
        Assert.Equal(newest, doc.Session.SavedRevision);
        Assert.False(doc.Session.IsDirty);
        var read = EditablePdfReader.Read(File.ReadAllBytes(file), PdfPageGeometryReader.Read(File.ReadAllBytes(file)));
        Same(doc.Session.Current, read.Model);
    }
}
