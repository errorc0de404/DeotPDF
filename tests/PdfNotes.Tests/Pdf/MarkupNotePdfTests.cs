using System.Collections.Immutable;
using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

/// <summary>D13i / A41–A42: underline and strikeout on PDF glyphs, sticky notes — schema v6, standard PDF types, Flatten.</summary>
public sealed class MarkupNotePdfTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
    private readonly string path;
    private static readonly RgbColor Red = new(0xE5, 0x39, 0x35), Yellow = new(0xFF, 0xD5, 0x4F);

    public MarkupNotePdfTests()
    {
        Directory.CreateDirectory(folder);
        path = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(path, 3);
    }

    public void Dispose() => Directory.Delete(folder, true);

    private static PdfDictionary? Resolve(PdfItem item) => item is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value as PdfDictionary : item as PdfDictionary;

    private static int Find(IReadOnlyList<TextGlyph> glyphs, string text) =>
        string.Concat(glyphs.Select(g => g.Text.Length == 1 ? g.Text : "?")).IndexOf(text, StringComparison.Ordinal);

    private (DocumentModel Model, PdfTextMarkup Under, PdfTextMarkup Strike, StickyNote Note) Build(PdfSourceDocument source)
    {
        var model = source.CreateModel();
        var glyphs = PdfTextReader.Glyphs(source.Engine, source.Pages[0]);
        int start = Find(glyphs, "Original");
        var quads = TextSelection.LineQuads(glyphs, start, start + 8);
        var under = new PdfTextMarkup(Guid.NewGuid(), false, quads, "Original", TextMarkupKind.Underline, 1.5, Red, 1);
        int english = Find(glyphs, "English");
        var strike = new PdfTextMarkup(Guid.NewGuid(), false, TextSelection.LineQuads(glyphs, english, english + 7), "English", TextMarkupKind.StrikeOut, 2, Red, 0.8);
        var note = new StickyNote(Guid.NewGuid(), false, Affine.Translation(400, 300), "메모 내용\n둘째 줄", Yellow);
        var rotatedNote = new StickyNote(Guid.NewGuid(), false, Affine.Translation(100, 100), "회전 페이지 메모", Yellow);
        var page = model.Pages[0];
        model = model with { Pages = model.Pages.SetItem(0, page with { Annotations = page.Annotations.AddRange([under, strike, note]) })
            .SetItem(1, model.Pages[1] with { Annotations = [rotatedNote] }) };
        return (model, under, strike, note);
    }

    [Fact]
    public void MarkupBandsSitAtTheLowerEdgeOrTheMiddle()
    {
        var quad = new Quad(new(10, 100), new(60, 100), new(10, 120), new(60, 120)); // y down: upper y 100, lower y 120
        var under = TextSelection.MarkupBand(quad, TextMarkupKind.Underline, 2);
        Assert.Equal(120, under.LowerLeft.Y, 6); Assert.Equal(118, under.UpperLeft.Y, 6);
        var strike = TextSelection.MarkupBand(quad, TextMarkupKind.StrikeOut, 2);
        Assert.Equal(111, strike.LowerLeft.Y, 6); Assert.Equal(109, strike.UpperLeft.Y, 6);
    }

    [Fact]
    public void SchemaV6RoundTripsAndOlderVersionsRefuse()
    {
        using var source = PdfSourceDocument.Open(path);
        var (model, _, _, _) = Build(source);
        Assert.Throws<InvalidDataException>(() => SchemaV5.Serialize(model));
        var data = SchemaV6.Serialize(model);
        var read = SchemaV6.Read(data.Header, data.Annotations.Select(a => a.Envelope).ToArray(), _ => null);
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        Assert.Equal(SchemaV6.Serialize(model).Header, SchemaV6.Serialize(read.Model!).Header);
        Assert.Equal(4, read.Model!.AllAnnotations.Count());
        Assert.Equal(HeaderStatus.Unsupported, SchemaV5.Read(data.Header, data.Annotations.Select(a => a.Envelope).ToArray(), _ => null).HeaderStatus);
        // A blank note is dropped on reading, like a blank text box.
        var blank = model with { Pages = model.Pages.SetItem(2, model.Pages[2] with { Annotations = [new StickyNote(Guid.NewGuid(), false, Affine.Translation(5, 5), "  ", Yellow)] }) };
        var written = EditablePdfWriter.Write(source.Bytes, source.Pages, blank);
        Assert.Equal(4, EditablePdfReader.Read(written, PdfPageGeometryReader.Read(written)).Model.AllAnnotations.Count());
    }

    [Fact]
    public void SavedAsStandardUnderlineStrikeOutAndTextWithOnePopupAcrossSaves()
    {
        using var source = PdfSourceDocument.Open(path);
        var (model, under, strike, note) = Build(source);
        var saved = EditablePdfWriter.Write(source.Bytes, source.Pages, model);
        EditablePdfWriter.Verify(saved, model);
        string file = Path.Combine(folder, "saved.pdf");
        File.WriteAllBytes(file, saved);
        using (var pdf = PdfReader.Open(new MemoryStream(saved), PdfDocumentOpenMode.Import))
        {
            var annots = pdf.Pages[0].Elements.GetArray("/Annots")!.Elements.Select(Resolve).OfType<PdfDictionary>().ToArray();
            var subtypes = annots.Select(a => a.Elements.GetName("/Subtype")).ToArray();
            Assert.Contains("/Underline", subtypes); Assert.Contains("/StrikeOut", subtypes); Assert.Contains("/Text", subtypes);
            Assert.Single(subtypes, s => s == "/Popup");
            var u = annots.Single(a => a.Elements.GetName("/Subtype") == "/Underline");
            Assert.Equal(8, u.Elements.GetArray("/QuadPoints")!.Elements.Count);
            var t = annots.Single(a => a.Elements.GetName("/Subtype") == "/Text");
            Assert.Equal(note.Text, t.Elements.GetString("/Contents"));
        }
        // Reopen: the same model, editable; saving again keeps one popup per note.
        using var reopened = PdfSourceDocument.Open(file);
        Assert.Equal(0, reopened.AppData.ReadOnlyCount);
        Assert.Equal(SchemaV6.Serialize(model).Annotations.Select(a => Convert.ToBase64String(a.Envelope)),
            SchemaV6.Serialize(reopened.CreateModel()).Annotations.Select(a => Convert.ToBase64String(a.Envelope)));
        var again = EditablePdfWriter.Write(reopened.Bytes, reopened.Pages, reopened.CreateModel());
        using (var pdf = PdfReader.Open(new MemoryStream(again), PdfDocumentOpenMode.Import))
            Assert.Single(pdf.Pages[0].Elements.GetArray("/Annots")!.Elements.Select(Resolve).OfType<PdfDictionary>(), a => a.Elements.GetName("/Subtype") == "/Popup");

        // Drawn: the underline pixels are red under "Original" (PDFium draws our appearance).
        using var engine = new PdfiumDocument(saved);
        var page = engine.Render(0, 2);
        var band = TextSelection.MarkupBand(under.Quads[0], TextMarkupKind.Underline, under.WidthPt);
        var mid = new Point2((band.LowerLeft.X + band.LowerRight.X) / 2, (band.UpperLeft.Y + band.LowerLeft.Y) / 2);
        int i = (int)(mid.Y * 2) * page.Stride + (int)(mid.X * 2) * 4;
        Assert.True(page.Bgra[i + 2] > 180 && page.Bgra[i + 1] < 120, $"pixel {page.Bgra[i + 2]},{page.Bgra[i + 1]},{page.Bgra[i]}");
        _ = strike;
    }

    [Fact]
    public void FlattenMergesMarkupsAndKeepsNotesAsStandardNotes()
    {
        using var source = PdfSourceDocument.Open(path);
        var (model, _, _, note) = Build(source);
        var result = FlattenWriter.Write(source.Bytes, source.Pages, model);
        FlattenWriter.Verify(result.Pdf, model.Pages.Count);
        using var pdf = PdfReader.Open(new MemoryStream(result.Pdf), PdfDocumentOpenMode.Import);
        var annots = pdf.Pages[0].Elements.GetArray("/Annots")!.Elements.Select(Resolve).OfType<PdfDictionary>().ToArray();
        Assert.DoesNotContain(annots, a => a.Elements.GetName("/Subtype") is "/Underline" or "/StrikeOut");
        var text = Assert.Single(annots, a => a.Elements.GetName("/Subtype") == "/Text");
        Assert.Equal(note.Text, text.Elements.GetString("/Contents"));
        Assert.DoesNotContain(text.Elements.Keys, k => k.StartsWith("/PdfNotes", StringComparison.Ordinal));
        Assert.Contains(annots, a => a.Elements.GetName("/Subtype") == "/Popup");
        // Opened again it is a plain PDF whose note is another app's note (read-only unless converted).
        var read = EditablePdfReader.Read(result.Pdf, PdfPageGeometryReader.Read(result.Pdf));
        Assert.Equal(0, read.RestoredCount);
    }

    [Fact]
    public void OtherAppsUnderlinesAndNotesConvertWhenAllowed()
    {
        using var source = PdfSourceDocument.Open(path);
        var (model, _, _, note) = Build(source);
        // A Flatten keeps the note as a standard note; reading it with "다른 프로그램에서 만든 주석도 편집" brings it back editable (14.6).
        var flat = FlattenWriter.Write(source.Bytes, source.Pages, model).Pdf;
        // Add a foreign underline next to it, as another viewer writes one.
        using (var doc = PdfReader.Open(new MemoryStream(flat), PdfDocumentOpenMode.Modify))
        {
            var u = new PdfDictionary(doc);
            u.Elements.SetName("/Type", "/Annot"); u.Elements.SetName("/Subtype", "/Underline");
            u.Elements["/Rect"] = new PdfArray(doc, new PdfReal(70), new PdfReal(660), new PdfReal(200), new PdfReal(680));
            u.Elements["/QuadPoints"] = new PdfArray(doc, new PdfReal(70), new PdfReal(680), new PdfReal(200), new PdfReal(680), new PdfReal(70), new PdfReal(662), new PdfReal(200), new PdfReal(662));
            u.Elements["/C"] = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(1));
            doc.Internals.AddObject(u);
            doc.Pages[0].Elements.GetArray("/Annots")!.Elements.Add(u.Reference!);
            using var output = new MemoryStream(); doc.Save(output, false); flat = output.ToArray();
        }
        string file = Path.Combine(folder, "foreign.pdf");
        File.WriteAllBytes(file, flat);
        using var opened = PdfSourceDocument.Open(file);
        var sources = new DocumentSources(opened, ownsMain: false);
        Assert.True(sources.ForeignCount() >= 2);
        var session = new DocumentSession(opened.CreateModel());
        Assert.True(session.Execute(sources.ForeignConversion(session.Current)!));
        var annotations = session.Current.Pages[0].Annotations;
        var converted = Assert.Single(annotations.OfType<StickyNote>());
        Assert.Equal(note.Text, converted.Text);
        Assert.True(Math.Abs(converted.Transform.OffsetX - 400) < 1 && Math.Abs(converted.Transform.OffsetY - 300) < 1, converted.Transform.ToString());
        var line = Assert.Single(annotations.OfType<PdfTextMarkup>());
        Assert.Equal(TextMarkupKind.Underline, line.MarkupKind);
        Assert.Equal(new RgbColor(0, 0, 0xFF), line.Color);
    }

    [Fact]
    public void PageTransformsMoveMarkupsAndNotes()
    {
        using var source = PdfSourceDocument.Open(path);
        var (model, under, _, note) = Build(source);
        var change = Affine.Scale(0.5).Then(Affine.Translation(20, 30));
        var m = (PdfTextMarkup)PageTransforms.Transform(under, change, 0.5);
        Assert.Equal(change.Transform(under.Quads[0].LowerLeft), m.Quads[0].LowerLeft);
        Assert.Equal(0.75, m.WidthPt, 6);
        var n = (StickyNote)PageTransforms.Transform(note, change, 0.5);
        Assert.True(n.Transform.IsTranslation);
        Assert.Equal(change.Transform(new Point2(400, 300)), new Point2(n.Transform.OffsetX, n.Transform.OffsetY));
        Assert.Null(ModelValidator.ValidateDocument(model));
    }
}
