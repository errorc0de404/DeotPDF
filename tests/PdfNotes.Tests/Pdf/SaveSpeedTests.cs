using System.Collections.Immutable;
using System.IO;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

/// <summary>
/// Save plan 2026-11-12: a save of the same pages writes once with the page fingerprints of the last save, unchanged
/// annotations keep their drawn appearance, and backups take the quick check — with the same bytes and safety as before.
/// </summary>
public sealed class SaveSpeedTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));

    public SaveSpeedTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    private (byte[] Pdf, IReadOnlyList<PdfPageGeometry> Pages, DocumentModel Model) Source(int pages = 3)
    {
        string path = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(path, pages);
        byte[] pdf = File.ReadAllBytes(path);
        var geometry = PdfPageGeometryReader.Read(pdf);
        return (pdf, geometry, EditablePdfReader.Read(pdf, geometry).Model!);
    }

    private static InkStroke Ink(double y) =>
        new(Guid.NewGuid(), false, Affine.Identity, [new(40, y), new(120, y + 10), new(200, y - 5)], 3, new(20, 40, 200), 1) { Pressures = [0.3, 0.8, 0.5] };

    private static DocumentModel With(DocumentModel model, int page, Annotation annotation) =>
        model with { Pages = model.Pages.SetItem(page, model.Pages[page] with { Annotations = model.Pages[page].Annotations.Add(annotation) }) };

    private static string WithoutStamps(byte[] pdf) => System.Text.RegularExpressions.Regex.Replace(System.Text.Encoding.Latin1.GetString(pdf),
        @"\(D:\d{14}[^)]*\)|\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:Z|[+-]\d\d:\d\d)|uuid:[0-9a-f-]{36}|/ID\[<[0-9A-F]+><[0-9A-F]+>\]", "#");

    [Fact]
    public void ASecondSaveOfTheSamePagesWritesOnceAndTheSameBytes()
    {
        var (pdf, pages, model) = Source();
        model = With(model, 0, Ink(100));
        var first = EditablePdfWriter.WritePdf(pdf, pages, model);
        EditablePdfWriter.Verify(first.Bytes, model);

        model = With(model, 1, Ink(200)); // a new stroke: the pages are the same
        var second = EditablePdfWriter.WritePdf(pdf, pages, model);
        Assert.True(second.ReusedBackgrounds);
        EditablePdfWriter.Verify(second.Bytes, model); // the next open keeps the stored original (no "background changed")
        var reopened = EditablePdfReader.Read(second.Bytes, PdfPageGeometryReader.Read(second.Bytes));
        Assert.Empty(reopened.Warnings);

        EditablePdfWriter.ForgetBackgrounds(); // the first save of these pages again: two passes, the same file
        var fresh = EditablePdfWriter.WritePdf(pdf, pages, model);
        Assert.False(fresh.ReusedBackgrounds);
        Assert.Equal(WithoutStamps(fresh.Bytes), WithoutStamps(second.Bytes));
    }

    [Fact]
    public void ChangedPagesOrANewTextLayerAreFingerprintedAgain()
    {
        var (pdf, pages, model) = Source();
        EditablePdfWriter.WritePdf(pdf, pages, With(model, 0, Ink(100)));

        var withoutPage = model with { Pages = model.Pages.RemoveAt(2) };
        var deleted = EditablePdfWriter.WritePdf(pdf, pages, withoutPage);
        Assert.False(deleted.ReusedBackgrounds);
        EditablePdfWriter.Verify(deleted.Bytes, withoutPage);

        var box = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Rigid(0, 60, 80), "글 text",
            [new TextRun(0, 6, "Malgun Gothic", 14, RgbColor.Black, false, false, false, false)], TextAlignment.Left, 200, TextBoxStyle.Default, []);
        var withText = With(withoutPage, 0, box); // the page's content gains a text layer
        var text = EditablePdfWriter.WritePdf(pdf, pages, withText);
        Assert.False(text.ReusedBackgrounds);
        EditablePdfWriter.Verify(text.Bytes, withText);
        var again = EditablePdfWriter.WritePdf(pdf, pages, With(withText, 1, Ink(150)));
        Assert.True(again.ReusedBackgrounds);
    }

    [Fact]
    public void TheQuickCheckCatchesWrongDataAndWrongFingerprints()
    {
        var (pdf, pages, model) = Source();
        model = With(model, 0, Ink(100));
        var written = EditablePdfWriter.WritePdf(pdf, pages, model);
        EditablePdfWriter.VerifyQuick(written.Bytes, written);

        var other = EditablePdfWriter.WritePdf(pdf, pages, With(model, 1, Ink(300)));
        Assert.Throws<InvalidDataException>(() => EditablePdfWriter.VerifyQuick(other.Bytes, written)); // an annotation too many

        // The same data with a page fingerprint another app's edit would no longer match.
        using var changed = PdfReader.Open(new MemoryStream(written.Bytes, false), PdfDocumentOpenMode.Modify);
        changed.Pages[1].Elements.SetString("/PdfNotesBackgroundHash", "00");
        using var output = new MemoryStream();
        changed.Save(output, false);
        Assert.Throws<InvalidDataException>(() => EditablePdfWriter.VerifyQuick(output.ToArray(), written));
    }

    [Fact]
    public void WhatIsReadBackMustBeWhatWasWrittenAndIsCheckedOnce()
    {
        byte[] content = [1, 2, 3];
        int checks = 0;
        var check = new WrittenCheck(content, _ => checks++);
        check.Verify(content);       // before an Android write
        check.Verify([1, 2, 3]);     // read back: the same bytes, not checked again
        Assert.Equal(1, checks);
        Assert.Throws<InvalidDataException>(() => check.Verify([1, 2, 4]));
    }
}
