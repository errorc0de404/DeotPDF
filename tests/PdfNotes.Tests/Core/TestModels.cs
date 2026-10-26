using System.Collections.Immutable;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Tests.Core;

internal static class TestModels
{
    public static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public static PageSetup A4(int rotation = 0) =>
        new(Units.MillimetersToPoints(210), Units.MillimetersToPoints(297), rotation, PageSource.Generated, PageBackground.Blank);

    public static DocumentModel Empty(int pages = 1) => DocumentModel.Create(Enumerable.Range(0, pages).Select(_ => A4()));

    public static InkStroke Ink(double x = 10, double width = 2, bool locked = false) =>
        new(Guid.NewGuid(), locked, Affine.Identity, [new(x, 10), new(x + 5.25, 12.125), new(x + 10, 30)], width, new(220, 40, 40), 1);

    public static Resource PngResource(int padding = 0)
    {
        var bytes = TinyPng.Concat(new byte[padding]).ToArray();
        return Resource.Create(Guid.NewGuid(), "image/png", bytes);
    }

    public static ImageAnnotation Image(Resource resource) =>
        new(Guid.NewGuid(), false, Affine.Rigid(0.25, 100, 200), resource.Id, new(0, 0, 64, 48));

    /// <summary>A two-page document containing every annotation kind with awkward numeric values.</summary>
    public static DocumentModel AllKinds(out Resource resource)
    {
        resource = PngResource();
        var model = DocumentModel.Create([A4(), A4(90) with { Background = PageBackground.Grid }]);
        string text = "한글 English 😀 é!";
        int emoji = text.IndexOf("😀", StringComparison.Ordinal);
        var runs = ImmutableArray.Create(
            new TextRun(0, 3, "Malgun Gothic", 12, RgbColor.Black, true, false, false, false),
            new TextRun(3, emoji - 3, "Malgun Gothic", 1.0 / 3, new(10, 20, 30), false, true, true, false),
            new TextRun(emoji, text.Length - emoji, "Segoe UI", 400, new(1, 2, 3), false, false, false, true));
        var box = new TextBoxAnnotation(Guid.NewGuid(), true, Affine.Rigid(Math.PI / 7, 0.1 + 0.2, 1e-7), text, runs,
            TextAlignment.Center, 180.000001, new(new(255, 255, 0), 0.4, true, new(0, 0, 255), 0.75),
            [new TextHighlightRange(0, 2, HighlightHeightMode.Auto, null, new(255, 215, 0), 0.4),
             new TextHighlightRange(emoji, 2, HighlightHeightMode.Manual, 7.5, new(0, 255, 0), 0.6)]);
        var free = new FreeHighlightStroke(Guid.NewGuid(), false, Affine.Identity, [new(1, 1), new(2.5, 1.0 / 3)], 14, new(255, 230, 0), 0.4);
        var line = new LineAnnotation(Guid.NewGuid(), false, Affine.Rigid(-1, 3, 4), LineKind.Cubic, new(0, 0), new(100, 50),
            [new(20, -30), new(80.5, 90.25)], 2, DashStyle.DashDot, true, false, new(0, 128, 255), 0.9);
        var pdfHighlight = new PdfTextHighlight(Guid.NewGuid(), false,
            [new Quad(new(70, 100), new(130, 100), new(70, 118), new(130, 118)), new Quad(new(70, 130), new(90, 130), new(70, 148), new(90, 148))],
            "원본 본문", HighlightHeightMode.Manual, 12, new(255, 215, 20), 0.4);
        var image = Image(resource);
        var pages = model.Pages;
        model = model with
        {
            Pages = pages.SetItem(0, pages[0] with { Annotations = [Ink(), box, free, image] })
                         .SetItem(1, pages[1] with { Annotations = [line, pdfHighlight] }),
            Resources = model.Resources.Add(resource.Id, resource)
        };
        return model;
    }
}
