using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

/// <summary>
/// Spec 14.6 (D11; a document command since D14g): other apps' standard annotations become editable app annotations by
/// the 다른 프로그램 주석 편집 command, one Undo unit, saved without duplicates.
/// </summary>
public sealed class ForeignAnnotationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));

    public ForeignAnnotationTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    private string Sample()
    {
        string path = Path.Combine(folder, "foreign.pdf");
        ForeignAnnotationSample.Create(path);
        return path;
    }

    /// <summary>Opens a PDF and runs the conversion command on it.</summary>
    private static (PdfSourceDocument Source, DocumentSources Sources, DocumentSession Session) Converted(string path)
    {
        var source = PdfSourceDocument.Open(path);
        var sources = new DocumentSources(source, ownsMain: false);
        var session = new DocumentSession(source.CreateModel());
        Assert.True(sources.ForeignCount() > 0);
        Assert.True(session.Execute(sources.ForeignConversion(session.Current)!));
        return (source, sources, session);
    }

    /// <summary>Subtypes of the annotations left in a PDF (own app annotations are /Stamp with a PdfNotes name).</summary>
    private static List<string> Foreign(byte[] pdf, int page)
    {
        using var stream = new MemoryStream(pdf, writable: false);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        var annots = doc.Pages[page].Elements.GetArray("/Annots");
        if (annots is null) return [];
        return Enumerable.Range(0, annots.Elements.Count).Select(i => annots.Elements.GetDictionary(i)!)
            .Where(a => !(a.Elements.GetString("/NM") ?? "").StartsWith(EditablePdfKeys.NamePrefix, StringComparison.Ordinal))
            .Select(a => a.Elements.GetName("/Subtype") + ((a.Elements.GetInteger("/F") & 2) != 0 ? "(hidden)" : "")).ToList();
    }

    [Fact]
    public void OpeningKeepsEveryForeignAnnotationReadOnlyAndCountsTheConvertible()
    {
        using var source = PdfSourceDocument.Open(Sample());
        Assert.Empty(source.CreateModel().AllAnnotations);
        Assert.Contains("/Ink", Foreign(source.Bytes, 0));
        Assert.Equal(ForeignAnnotationSample.ConvertiblePdfAnnotations + 1, new DocumentSources(source, ownsMain: false).ForeignCount()); // the ink has two paths
    }

    [Fact]
    public void ConvertibleKindsBecomeAppAnnotationsWithTheirValues()
    {
        string path = Sample();
        byte[] before = File.ReadAllBytes(path);
        var (source, _, session) = Converted(path);
        using var _source = source;
        Assert.Equal(before, File.ReadAllBytes(path)); // nothing is written until a save
        var model = session.Current;
        var page = model.Pages[0];
        var box = source.Pages[0].Box;
        Point2 C(double x, double y) => box.PdfUserToCanonical.Transform(new(x, y));

        var ink = page.Annotations.OfType<InkStroke>().ToArray();
        Assert.Equal(2, ink.Length);
        Assert.Equal(new RgbColor(255, 0, 0), ink[0].Color);
        Assert.Equal(3, ink[0].WidthPt); Assert.Equal(.8, ink[0].Opacity, 6);
        Assert.Equal(C(150, 630), ink[0].Points[1]);
        var highlight = Assert.Single(page.Annotations.OfType<PdfTextHighlight>());
        Assert.Equal(C(68, 684), highlight.Quads[0].UpperLeft); Assert.Equal(C(330, 664), highlight.Quads[0].LowerRight);
        Assert.Equal(.5, highlight.Opacity, 6);
        var line = Assert.Single(page.Annotations.OfType<LineAnnotation>());
        Assert.True(line is { StartArrow: false, EndArrow: true, Dash: DashStyle.Dash, WidthPt: 2 });
        Assert.Equal(C(510, 550), line.End);
        var text = Assert.Single(page.Annotations.OfType<TextBoxAnnotation>());
        Assert.Equal("Foreign note", text.Text);
        Assert.Equal(14, text.Runs[0].SizePt); Assert.Equal(new RgbColor(0, 0, 255), text.Runs[0].Color);
        Assert.Equal(C(320, 640), new(text.Transform.OffsetX, text.Transform.OffsetY));
        var shapes = page.Annotations.OfType<ShapeAnnotation>().ToArray();
        Assert.Equal([ShapeKind.Rectangle, ShapeKind.Ellipse, ShapeKind.Polygon, ShapeKind.Polyline, ShapeKind.Rectangle], shapes.Select(s => s.ShapeKind));
        var square = shapes[0];
        Assert.Equal(new Rect2(61, C(0, 460).Y + 1, 138, 78), square.Box); // inside /Rect by half the 2pt border
        Assert.True(square.Style is { BorderEnabled: true, BorderWidthPt: 2, FillOpacity: 1 } && square.Style.FillColor == new RgbColor(204, 255, 204));
        Assert.Equal(0, shapes[1].Style.FillOpacity); // circle without /IC: no fill
        Assert.Equal(3, shapes[2].Points.Length);
        Assert.False(shapes[3].IsClosed);
        Assert.True(shapes[4].Locked); // /F Locked → app lock
        Assert.All(page.Annotations, a => Assert.Null(ModelValidator.ValidateAnnotation(a, _ => false)));

        var note = Assert.Single(page.Annotations.OfType<StickyNote>()); // D13i: a note with text converts (spec 24.9)
        Assert.Equal("note", note.Text);
        Assert.Equal(C(540, 720), new(note.Transform.OffsetX, note.Transform.OffsetY));
        // Only the read-only kinds stay in the PDF the converted pages render and save from (notes take their popups along).
        Assert.Equal(["/Stamp", "/Stamp", "/Ink(hidden)"], Foreign(source.Foreign!.Bytes, 0));
        Assert.Equal(["/Stamp"], Foreign(source.Foreign.Bytes, 1));
        Assert.All(model.Pages, p => Assert.True(p.Setup.Source.ForeignConverted));
    }

    [Fact]
    public void TheConversionIsOneUndoUnitAndUndoShowsTheOriginalsAgain()
    {
        var (source, sources, session) = Converted(Sample());
        using var _source = source;
        Assert.True(session.IsDirty);
        Assert.Equal("undo.convertForeign", session.UndoLabel);
        Assert.Null(sources.ForeignConversion(session.Current)); // nothing left to convert
        session.Undo();
        Assert.Empty(session.Current.AllAnnotations);
        Assert.All(session.Current.Pages, p => Assert.False(p.Setup.Source.ForeignConverted)); // the originals render again
        Assert.False(session.IsDirty);
        var (main, _, _) = sources.ForSave(session.Current);
        Assert.Same(source.Bytes, main); // unconverted pages are saved from the original
        session.Redo();
        Assert.Same(source.Foreign!.Bytes, sources.ForSave(session.Current).Main);
    }

    [Fact]
    public void OnARotatedPageTextAndSquaresStayUprightOnScreen()
    {
        var (source, _, session) = Converted(Sample());
        using var _source = source;
        var page = session.Current.Pages[1];
        var frame = page.Setup.Frame;
        Assert.Equal(90, frame.Rotation);
        var text = Assert.Single(page.Annotations.OfType<TextBoxAnnotation>());
        var square = Assert.Single(page.Annotations.OfType<ShapeAnnotation>());
        foreach (var m in new[] { text.Transform.Then(frame.CanonicalToDisplay), square.Transform.Then(frame.CanonicalToDisplay) })
        {
            Assert.Equal(1, m.M11, 9); Assert.Equal(0, m.M12, 9); Assert.Equal(0, m.M21, 9); Assert.Equal(1, m.M22, 9);
        }
    }

    [Fact]
    public void SavingReplacesTheOriginalsAndReopeningDoesNotDuplicate()
    {
        string path = Sample();
        byte[] saved;
        DocumentModel expected;
        var (source, sources, session) = Converted(path);
        using (source)
        {
            var ink = session.Current.AllAnnotations.OfType<InkStroke>().First();
            session.Execute(DocumentEdits.UpdateAnnotations(session.Current, [ink with { Color = new(0, 0, 0) }]));
            var (main, model, parts) = sources.ForSave(session.Current);
            saved = EditablePdfWriter.Write(main, source.Pages, model, parts);
            EditablePdfWriter.Verify(saved, model);
            expected = session.Current;
        }
        string again = Path.Combine(folder, "saved.pdf");
        File.WriteAllBytes(again, saved);
        // The converted note is saved as our /Text note; its popup is a plain popup (no app mark), listed here.
        Assert.Equal(["/Stamp", "/Stamp", "/Ink(hidden)", "/Popup"], Foreign(saved, 0));
        using var reopened = PdfSourceDocument.Open(again);
        Assert.Equal(0, new DocumentSources(reopened, ownsMain: false).ForeignCount()); // our own annotations are restored, nothing left to convert
        Assert.Equal(expected.AllAnnotations.Count(), reopened.CreateModel().AllAnnotations.Count());
        Assert.Equal(0, reopened.AppData.ReadOnlyCount);
        Assert.Contains(reopened.CreateModel().AllAnnotations.OfType<InkStroke>(), s => s.Color == new RgbColor(0, 0, 0));
    }

    [Fact]
    public void ABrokenAnnotationStaysReadOnlyAndNothingIsLost()
    {
        string path = Sample();
        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var annots = doc.Pages[0].Elements.GetArray("/Annots")!;
            var line = Enumerable.Range(0, annots.Elements.Count).Select(i => annots.Elements.GetDictionary(i)!).First(a => a.Elements.GetName("/Subtype") == "/Line");
            line.Elements["/L"] = new PdfArray(doc, new PdfReal(1)); // malformed
            doc.Save(path);
        }
        var (source, _, session) = Converted(path);
        using var _source = source;
        Assert.Empty(session.Current.AllAnnotations.OfType<LineAnnotation>());
        Assert.Contains("/Line", Foreign(source.Foreign!.Bytes, 0));
    }
}
