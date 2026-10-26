using System.IO;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfNotes.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

public sealed class TextPdfTests
{
    public static TextBoxAnnotation Sample() => TextEditing.Format(Core.TextEditingTests.Box("한글 English 😀\n밑줄 취소선", 170), 3, 7,
        r => r with { Color = new(190, 30, 60), SizePt = 22, Bold = true, Italic = true, Underline = true, Strikethrough = true })
        with { BoxStyle = new(new(240, 200, 100), .35, true, new(20, 70, 170), .7) };
    private static DocumentModel Model(TextBoxAnnotation box, int rotation = 0)
    {
        var s = new DocumentSession(DocumentModel.Create([new(400, 500, rotation, PageSource.Generated, PageBackground.Blank)]));
        s.Execute(DocumentEdits.AddAnnotation(s.Current, s.Current.Pages[0].Id, box)); return s.Current;
    }
    [Theory] [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public void KoreanMixedStylesRoundTripAndRasterMatchSharedOutlineLayout(int rotation)
    {
        var box = Sample(); var model = Model(box, rotation); byte[] pdf = EditablePdfWriter.Write(null, null, model);
        EditablePdfWriter.Verify(pdf, model); var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(1, read.RestoredCount); Assert.Equal(0, read.ReadOnlyCount);
        Assert.Equal(SchemaV1.Serialize(model).Annotations[0].Envelope, SchemaV1.Serialize(read.Model).Annotations[0].Envelope);
        using var document = new PdfiumDocument(pdf); using var background = new PdfiumDocument(read.BackgroundPdf);
        var actual = document.Render(0, 2, annotations: true); var blank = background.Render(0, 2, annotations: true);
        Assert.True(actual.Bgra.Where((v, i) => i % 4 != 3 && v < 210).Count() > 1000);
        Assert.True(blank.Bgra.Where((v, i) => i % 4 != 3 && v < 210).Count() < 10);
        // Independent rasterization of the shared vector drawing verifies real glyph positions/orientation and fill alpha.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var setup = model.Pages[0].Setup; var m = box.Transform.Then(setup.Frame.CanonicalToDisplay).Then(Affine.Scale(2));
                var visual = new DrawingVisual(); using (var c = visual.RenderOpen())
                {
                    c.DrawRectangle(Brushes.White, null, new Rect(0, 0, actual.Width, actual.Height));
                    c.PushTransform(new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY)));
                    c.DrawDrawing(WpfText.Measure(box).Drawing); c.Pop();
                }
                var bitmap = new RenderTargetBitmap(actual.Width, actual.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var expected = new byte[actual.Bgra.Length]; bitmap.CopyPixels(expected, actual.Stride, 0);
                long error = 0; int colored = 0;
                for (int i = 0; i < expected.Length; i += 4)
                    if (expected[i] < 250 || expected[i + 1] < 250 || expected[i + 2] < 250 || actual.Bgra[i] < 250 || actual.Bgra[i + 1] < 250 || actual.Bgra[i + 2] < 250)
                    { error += Math.Abs(expected[i] - actual.Bgra[i]) + Math.Abs(expected[i + 1] - actual.Bgra[i + 1]) + Math.Abs(expected[i + 2] - actual.Bgra[i + 2]); colored++; }
                Assert.True(error / (double)(colored * 3) < 12, $"Mean painted-pixel RGB error: {error / (double)(colored * 3)}");
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) throw failure;
    }
    [Fact] public void LockedBlankTextHasNeitherMetadataNorAppearance()
    {
        var model = Model(Core.TextEditingTests.Box(" \t\n") with { Locked = true });
        var pdf = EditablePdfWriter.Write(null, null, model); EditablePdfWriter.Verify(pdf, model);
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf)); Assert.Empty(read.Model.AllAnnotations);
        using var stream = new MemoryStream(pdf); using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
        Assert.True(doc.Pages[0].Elements.GetArray("/Annots") is null or { Elements.Count: 0 });
    }
    [Fact] public void MissingFontKeepsOriginalAppearanceAndWarnsWithoutChangingLayout()
    {
        var model = Model(Sample()); byte[] pdf = EditablePdfWriter.Write(null, null, model);
        using (var stream = new MemoryStream(pdf)) using (var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify))
        {
            var replacement = model with { Pages = model.Pages.Select(p => p with { Annotations = p.Annotations.Select(a => a is TextBoxAnnotation t ? t with { Runs = t.Runs.Select(r => r with { FontFamily = "MissingFont-D06-12345" }).ToImmutableArray() } : a).ToImmutableList() }).ToImmutableList() };
            var data = SchemaV6.Serialize(replacement); // the version the writer uses
            var annot = (PdfDictionary)((PdfReference)doc.Pages[0].Elements.GetArray("/Annots")!.Elements[0]).Value;
            var envelope = new PdfDictionary(doc); envelope.CreateStream(data.Annotations[0].Envelope); envelope.Stream.Zip(); doc.Internals.AddObject(envelope); annot.Elements[EditablePdfKeys.Envelope] = envelope.Reference!;
            using var output = new MemoryStream(); doc.Save(output, false); pdf = output.ToArray();
        }
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(1, read.ReadOnlyCount); Assert.Empty(read.Model.AllAnnotations); Assert.Contains(read.Warnings, w => w.StartsWith("Missing font:"));
        var again = EditablePdfWriter.Write(pdf, PdfPageGeometryReader.Read(pdf), read.Model);
        using var a = new PdfiumDocument(pdf); using var b = new PdfiumDocument(again);
        Assert.Equal(a.Render(0, annotations: true).Bgra, b.Render(0, annotations: true).Bgra);
    }
    [Fact] public void ChangedTextStrokeAlphaIsReadOnlyInsteadOfHiddenBehindMetadata()
    {
        var model = Model(Sample()); byte[] pdf = EditablePdfWriter.Write(null, null, model);
        using (var input = new MemoryStream(pdf)) using (var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            var annot = (PdfDictionary)((PdfReference)doc.Pages[0].Elements.GetArray("/Annots")!.Elements[0]).Value;
            var states = annot.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/ExtGState")!;
            states.Elements.GetDictionary(states.Elements.Keys.First())!.Elements.SetReal("/CA", .01);
            using var output = new MemoryStream(); doc.Save(output, false); pdf = output.ToArray();
        }
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf)); Assert.Equal(1, read.ReadOnlyCount); Assert.Empty(read.Model.AllAnnotations);
    }
    [Fact] public void RepeatedTextSaveDoesNotAccumulateOutlinesAndStylesStayExact()
    {
        var model = Model(Sample()); byte[] pdf = EditablePdfWriter.Write(null, null, model); var sizes = new List<int>();
        for (int i = 0; i < 5; i++)
        {
            var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf)); Assert.Equal(1, read.RestoredCount);
            pdf = EditablePdfWriter.Write(pdf, PdfPageGeometryReader.Read(pdf), read.Model); EditablePdfWriter.Verify(pdf, read.Model); sizes.Add(pdf.Length);
        }
        Assert.True(sizes.Max() - sizes.Min() < 100, string.Join(",", sizes));
    }

    [Theory] [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public void SavedTextIsUnicodePageTextWithMeasuredPositionsAndNoBackgroundGhosts(int rotation)
    {
        var box = Sample() with { Transform = Affine.Rigid(.23, 40, 60) };
        byte[] pdf = EditablePdfWriter.Write(null, null, Model(box, rotation));
        using var engine = new PdfiumDocument(pdf);
        var chars = PdfTextReader.ExtractCanonical(engine, PdfPageGeometryReader.Read(pdf)[0]);
        string text = string.Concat(chars.Select(c => c.Text));
        string normalized = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        foreach (string line in box.Text.Split('\n')) Assert.Contains(string.Concat(line.Where(c => !char.IsWhiteSpace(c))), normalized);
        Assert.Equal(box.Text.EnumerateRunes().Where(r => !System.Text.Rune.IsWhiteSpace(r)).OrderBy(r => r.Value), text.EnumerateRunes().Where(r => !System.Text.Rune.IsWhiteSpace(r)).OrderBy(r => r.Value));
        var first = TextLayout.Measure(box).Characters[0];
        var expected = AnnotationGeometry.Corners(first.Bounds, box.Transform);
        var firstExtracted = chars.Single(c => c.Text == first.Text);
        Assert.InRange(firstExtracted.Quad.UpperLeft.X, expected.Min(p => p.X) - 1, expected.Min(p => p.X) + 1);
        Assert.InRange(firstExtracted.Quad.UpperLeft.Y, expected.Min(p => p.Y) - 1, expected.Min(p => p.Y) + 1);
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        using var background = new PdfiumDocument(read.BackgroundPdf);
        Assert.Empty(background.ExtractText(0));
    }

    [Fact] public void TextEditingAndDeletionRemoveOldSearchableWordsAcrossSaveAndReopen()
    {
        var model = Model(Core.TextEditingTests.Box("OLD한글", 180));
        byte[] pdf = EditablePdfWriter.Write(null, null, model);
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        var session = new DocumentSession(read.Model);
        var old = (TextBoxAnnotation)session.Current.AllAnnotations.Single();
        session.Execute(DocumentEdits.UpdateAnnotations(session.Current, [old with { Text = "NEW😀", Runs = [TextEditing.Default with { Length = "NEW😀".Length }] }], "edit"));
        pdf = EditablePdfWriter.Write(pdf, PdfPageGeometryReader.Read(pdf), session.Current);
        using (var engine = new PdfiumDocument(pdf)) Assert.Equal("NEW😀", string.Concat(engine.ExtractText(0).Select(c => c.Text)).Trim());
        var updated = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        var empty = updated.Model with { Pages = updated.Model.Pages.Select(p => p with { Annotations = [] }).ToImmutableList() };
        pdf = EditablePdfWriter.Write(pdf, PdfPageGeometryReader.Read(pdf), empty);
        using var final = new PdfiumDocument(pdf); Assert.Empty(final.ExtractText(0));
    }

    [Fact] public void ChangedSearchTextIsKeptReadOnlyTogetherWithItsAppearance()
    {
        byte[] pdf = EditablePdfWriter.Write(null, null, Model(Sample()));
        using (var input = new MemoryStream(pdf)) using (var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            var annot = (PdfDictionary)((PdfReference)doc.Pages[0].Elements.GetArray("/Annots")!.Elements[0]).Value;
            var layer = annot.Elements.GetDictionary("/PdfNotesTextLayer")!;
            layer.Stream.Value = [.. layer.Stream.UnfilteredValue, .. System.Text.Encoding.ASCII.GetBytes("% external change\n")];
            layer.Elements.Remove("/Filter");
            using var output = new MemoryStream(); doc.Save(output, false); pdf = output.ToArray();
        }
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf));
        Assert.Equal(1, read.ReadOnlyCount); Assert.Empty(read.Model.AllAnnotations);
        using var engine = new PdfiumDocument(read.BackgroundPdf);
        Assert.Contains("한글", string.Concat(engine.ExtractText(0).Select(c => c.Text)));
    }

    [Fact] public void OlderOutlineOnlyPdfRestoresAndGainsPageTextWhenSaved()
    {
        var model = Model(Sample()); byte[] pdf = EditablePdfWriter.Write(null, null, model);
        using (var input = new MemoryStream(pdf)) using (var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            var annot = (PdfDictionary)((PdfReference)doc.Pages[0].Elements.GetArray("/Annots")!.Elements[0]).Value;
            var layer = annot.Elements.GetDictionary("/PdfNotesTextLayer")!;
            for (int i = doc.Pages[0].Contents.Elements.Count - 1; i >= 0; i--)
                if (doc.Pages[0].Contents.Elements[i] is PdfReference r && r == layer.Reference) doc.Pages[0].Contents.Elements.RemoveAt(i);
            doc.Pages[0].Resources.Elements.GetDictionary("/XObject")!.Elements.Remove(layer.Elements.GetString("/PdfNotesTextName"));
            annot.Elements.Remove("/PdfNotesTextLayer"); annot.Elements.Remove("/PdfNotesTextHash");
            var legacy = SchemaV1.Serialize(model);
            var header = doc.Internals.Catalog.Elements.GetDictionary(EditablePdfKeys.Header)!;
            header.Stream.Value = legacy.Header; header.Elements.Remove("/Filter");
            var envelope = annot.Elements.GetDictionary(EditablePdfKeys.Envelope)!;
            envelope.Stream.Value = legacy.Annotations[0].Envelope; envelope.Elements.Remove("/Filter");
            using var output = new MemoryStream(); doc.Save(output, false); pdf = output.ToArray();
        }
        using (var old = new PdfiumDocument(pdf)) Assert.Empty(old.ExtractText(0));
        var read = EditablePdfReader.Read(pdf, PdfPageGeometryReader.Read(pdf)); Assert.Equal(1, read.RestoredCount);
        var saved = EditablePdfWriter.Write(pdf, PdfPageGeometryReader.Read(pdf), read.Model);
        EditablePdfWriter.Verify(saved, read.Model); // legacy files use their physical PDF pages as source
        using var engine = new PdfiumDocument(saved); Assert.Contains("한글 English 😀", string.Concat(engine.ExtractText(0).Select(c => c.Text)));
    }

    [Fact] public void LongUnicodeLineCrossingFontAndCmapBoundariesIsNotLostOrDuplicated()
    {
        string text = string.Concat(Enumerable.Repeat("한글ABC😀", 52));
        var box = Core.TextEditingTests.Box(text, 3500) with { Transform = Affine.Translation(20, 20) };
        var model = Model(box);
        model = model with { Pages = model.Pages.Select(p => p with { Setup = p.Setup with { WidthPt = 4000 } }).ToImmutableList() };
        byte[] pdf = EditablePdfWriter.Write(null, null, model); EditablePdfWriter.Verify(pdf, model);
        using var engine = new PdfiumDocument(pdf);
        Assert.Equal(text, string.Concat(engine.ExtractText(0).Select(c => c.Text)).Trim());
    }
}
