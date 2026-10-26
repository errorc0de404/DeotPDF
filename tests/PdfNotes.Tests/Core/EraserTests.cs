using System.Collections.Immutable;
using System.IO;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;

namespace PdfNotes.Tests.Core;

/// <summary>D08: stroke/area eraser results, one Undo unit per gesture, exact remaining geometry, stacking order.</summary>
public sealed class EraserTests
{
    private static readonly RgbColor Red = new(220, 40, 40);

    private static InkStroke Ink(Point2[] points, double width = 4, Affine? transform = null, bool locked = false) =>
        new(Guid.NewGuid(), locked, transform ?? Affine.Identity, [.. points], width, Red, 1);

    private static (DocumentSession Session, Guid PageId) Session(params Annotation[] annotations)
    {
        var model = DocumentModel.Create([TestModels.A4()]);
        model = model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = [.. annotations] }) };
        return (new DocumentSession(model), model.Pages[0].Id);
    }

    private static EditCommand Erase(DocumentSession session, Guid pageId, EraserMode mode, double diameter, params Point2[] path)
    {
        var gesture = new EraseGesture(session.Current, pageId, mode, diameter);
        foreach (var p in path) gesture.AddPoint(p);
        return gesture.Commit();
    }

    private static bool SameModel(DocumentModel a, DocumentModel b) =>
        SchemaV3.Serialize(a).Annotations.Select(x => x.Envelope).SequenceEqual(SchemaV3.Serialize(b).Annotations.Select(x => x.Envelope),
            new BytesComparer());

    private sealed class BytesComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => obj.Length;
    }

    private static byte[] SetVersion(byte[] json, int version)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["schemaVersion"] = version;
        return System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    private static bool Painted(DocumentModel model, Point2 page) =>
        model.Pages[0].Annotations.Any(a => AnnotationGeometry.Hit(a, page, 0));

    [Fact]
    public void StrokeEraserRemovesOnlyTouchedUnlockedFreehandStrokesAndUndoRestoresThem()
    {
        var touched = Ink([new(100, 100), new(200, 100)]);
        var far = Ink([new(100, 300), new(200, 300)]);
        var highlight = new FreeHighlightStroke(Guid.NewGuid(), false, Affine.Identity, [new(150, 60), new(150, 140)], 14, new(255, 230, 0), .4);
        var locked = Ink([new(120, 90), new(120, 110)], locked: true);
        var line = new LineAnnotation(Guid.NewGuid(), false, Affine.Identity, LineKind.Straight, new(170, 50), new(170, 150), [], 2,
            DashStyle.Solid, false, false, Red, 1);
        var (session, pageId) = Session(touched, far, highlight, locked, line);
        var before = session.Current;

        Assert.True(session.Execute(Erase(session, pageId, EraserMode.Stroke, 10, new Point2(110, 100), new Point2(190, 100))));
        var ids = session.Current.Pages[0].Annotations.Select(a => a.Id).ToArray();
        Assert.Equal([far.Id, locked.Id, line.Id], ids); // locked ink, line: never erased
        Assert.Equal(1, session.UndoCount);
        Assert.Equal("undo.strokeEraser", session.UndoLabel);

        session.Undo();
        Assert.Equal(before.Pages[0].Annotations, session.Current.Pages[0].Annotations);
        Assert.Equal([touched.Id, far.Id, highlight.Id, locked.Id, line.Id], session.Current.Pages[0].Annotations.Select(a => a.Id));
    }

    [Theory]
    [InlineData(EraserMode.Stroke)]
    [InlineData(EraserMode.Area)]
    public void FastMovesEraseEverythingBetweenInputPoints(EraserMode mode)
    {
        // Two input points 300pt apart; the strokes lie only between them.
        var a = Ink([new(150, 50), new(150, 150)]);
        var b = Ink([new(250, 50), new(250, 150)]);
        var (session, pageId) = Session(a, b);
        session.Execute(Erase(session, pageId, mode, 20, new Point2(50, 100), new Point2(350, 100)));
        var model = session.Current;
        Assert.False(Painted(model, new(150, 100)), "the gap between input points was erased");
        Assert.False(Painted(model, new(250, 100)));
        if (mode == EraserMode.Area)
        {
            Assert.True(Painted(model, new(150, 60)) && Painted(model, new(150, 140)), "outside the swept band stays");
            Assert.Equal(4, model.Pages[0].Annotations.Count); // each stroke split in two
        }
        else Assert.Empty(model.Pages[0].Annotations);
    }

    [Fact]
    public void AreaEraserSplitsStrokeIntoEditablePiecesAtTheOriginalStackingPosition()
    {
        var below = Ink([new(50, 200), new(60, 200)]);
        var stroke = Ink([new(0, 50), new(200, 50)], width: 4);
        var above = Ink([new(50, 300), new(60, 300)]);
        var (session, pageId) = Session(below, stroke, above);
        var before = session.Current;
        session.Execute(Erase(session, pageId, EraserMode.Area, 20, new Point2(100, 50))); // one press: a circle

        var page = session.Current.Pages[0];
        Assert.Equal(4, page.Annotations.Count);
        Assert.Equal(below.Id, page.Annotations[0].Id);
        Assert.Equal(above.Id, page.Annotations[3].Id);
        var pieces = page.Annotations.Skip(1).Take(2).Cast<InkStroke>().ToArray();
        Assert.Contains(pieces, p => p.Id == stroke.Id); // the larger piece keeps the id
        Assert.All(pieces, p => Assert.True(p.IsOutlined && p.Points.IsEmpty && p.Color == stroke.Color && p.Opacity == stroke.Opacity));
        Assert.Null(ModelValidator.ValidateDocument(session.Current));

        // The cut follows the eraser circle: x = 100 - sqrt(10² - 2²) at the stroke edge, 90 on the centerline.
        var left = pieces.OrderBy(p => AnnotationGeometry.LocalBounds(p).X).First();
        var b = AnnotationGeometry.LocalBounds(left);
        Assert.InRange(b.X + b.Width, 100 - Math.Sqrt(96) - .02, 100 - Math.Sqrt(96) + .02);
        Assert.False(Painted(session.Current, new(90.1, 50)), "inside the circle is erased");
        Assert.True(Painted(session.Current, new(90.1, 51.95)), "just outside the circle, inside the stroke, is kept");
        Assert.True(Painted(session.Current, new(1, 50)) && Painted(session.Current, new(-1.9, 50)), "untouched round cap is kept");
        Assert.False(Painted(session.Current, new(100, 50)));

        session.Undo();
        Assert.Equal(before.Pages[0].Annotations, session.Current.Pages[0].Annotations);
    }

    [Fact]
    public void ErasingOnlyTheEdgeOfAThickStrokeKeepsTheRestExactly()
    {
        var stroke = Ink([new(0, 50), new(200, 50)], width: 20); // y 40..60
        var (session, pageId) = Session(stroke);
        double original = StrokeRegion.Area(StrokeRegion.Region(stroke));
        session.Execute(Erase(session, pageId, EraserMode.Area, 10, new Point2(50, 37), new Point2(150, 37))); // reaches y = 42
        var piece = Assert.IsType<InkStroke>(Assert.Single(session.Current.Pages[0].Annotations));
        Assert.Equal(stroke.Id, piece.Id);
        Assert.True(piece.IsOutlined);
        Assert.False(Painted(session.Current, new(100, 41)));
        Assert.True(Painted(session.Current, new(100, 42.1)));
        Assert.True(Painted(session.Current, new(100, 59.9)) && Painted(session.Current, new(20, 40.5)), "outside the swept band is unchanged");
        // Removed: the 100pt × 2pt band plus half a circular segment (r 5, chord 3 from the center) at each end.
        double expected = 100 * 2 + (25 * Math.Acos(.6) - 3 * 4);
        double rest = StrokeRegion.Area(StrokeRegion.Region(piece));
        Assert.InRange(original - rest, expected - .3, expected + .3);
    }

    [Fact]
    public void RotatedAndScaledStrokesAreErasedWhereTheCircleIsOnThePage()
    {
        var rotated = Ink([new(0, 0), new(100, 0)], width: 6, transform: Affine.Rigid(Math.PI / 2, 300, 100)); // page x=300, y 100..200
        var (session, pageId) = Session(rotated);
        var scaled = AnnotationGeometry.Similarity(rotated, new(300, 100), 2); // y 100..300, width 12
        session.Execute(DocumentEdits.UpdateAnnotations(session.Current, [scaled]));
        session.Execute(Erase(session, pageId, EraserMode.Area, 20, new Point2(300, 250)));
        var model = session.Current;
        Assert.Equal(2, model.Pages[0].Annotations.Count);
        Assert.False(Painted(model, new(300, 250)));
        Assert.False(Painted(model, new(305, 250)));
        Assert.True(Painted(model, new(300, 238)) && Painted(model, new(300, 262)));
        Assert.True(Painted(model, new(305, 230)), "scaled width (12pt) is kept outside the circle");
        Assert.All(model.Pages[0].Annotations, a => Assert.True(a.Transform.IsRigid));
    }

    [Fact]
    public void PiecesCanBeErasedAgainScaledAndFullyRemoved()
    {
        var stroke = Ink([new(0, 50), new(200, 50)]);
        var (session, pageId) = Session(stroke);
        session.Execute(Erase(session, pageId, EraserMode.Area, 20, new Point2(100, 0), new Point2(100, 100)));
        session.Execute(Erase(session, pageId, EraserMode.Area, 20, new Point2(50, 0), new Point2(50, 100)));
        Assert.Equal(3, session.Current.Pages[0].Annotations.Count);

        session.Execute(Erase(session, pageId, EraserMode.Stroke, 10, new Point2(150, 50))); // the right piece
        Assert.Equal(2, session.Current.Pages[0].Annotations.Count);
        Assert.False(Painted(session.Current, new(150, 50)));

        var piece = (StrokeAnnotation)session.Current.Pages[0].Annotations.Single(a => AnnotationGeometry.Hit(a, new(25, 50), 0));
        var bigger = AnnotationGeometry.Similarity(piece, new(25, 50), 2);
        var (b0, b1) = (AnnotationGeometry.LocalBounds(piece), AnnotationGeometry.LocalBounds(bigger));
        Assert.Equal(b0.Width * 2, b1.Width, 6);
        Assert.Equal(b0.Height * 2, b1.Height, 6);
        session.Execute(DocumentEdits.UpdateAnnotations(session.Current, [bigger]));
        Assert.True(Painted(session.Current, new(25, 53.5)), "the scaled piece is twice as thick");

        session.Execute(Erase(session, pageId, EraserMode.Area, 100, new Point2(0, 50), new Point2(400, 50), new Point2(400, 120), new Point2(0, 120)));
        Assert.Empty(session.Current.Pages[0].Annotations);
        Assert.Equal(5, session.UndoCount);
        while (session.CanUndo) session.Undo();
        Assert.Equal([stroke.Id], session.Current.Pages[0].Annotations.Select(a => a.Id));
    }

    [Fact]
    public void ErasedHighlighterPiecesKeepKindStyleAndRoundTripInSchemaV3Only()
    {
        var highlight = new FreeHighlightStroke(Guid.NewGuid(), false, Affine.Rigid(.3, 50, 60), [new(0, 0), new(80, 10), new(160, 0)], 14,
            new(255, 230, 0), .4);
        var (session, pageId) = Session(highlight, TestModels.Ink());
        var dot = session.Current.Pages[0].Annotations[1];
        session.Execute(Erase(session, pageId, EraserMode.Area, 30, highlight.Transform.Transform(new Point2(80, 10))));
        var pieces = session.Current.Pages[0].Annotations.OfType<FreeHighlightStroke>().ToArray();
        Assert.Equal(2, pieces.Length);
        Assert.All(pieces, p => Assert.True(p.IsOutlined && p.WidthPt == 14 && p.Opacity == .4 && p.Transform == highlight.Transform));

        var model = session.Current;
        var data = SchemaV3.Serialize(model);
        var read = SchemaV3.Read(data.Header, data.Annotations.Select(a => a.Envelope).ToArray(), _ => null);
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        Assert.All(read.Annotations, a => Assert.Equal(AnnotationReadStatus.Restored, a.Status));
        Assert.True(SameModel(model, read.Model!));
        Assert.Equal(dot.Id, read.Model!.Pages[0].Annotations[^1].Id);

        Assert.Throws<InvalidDataException>(() => SchemaV2.Serialize(model));
        Assert.Throws<InvalidDataException>(() => SchemaV1.Serialize(model));
        Assert.Equal(HeaderStatus.Unsupported, SchemaV2.Read(data.Header, data.Annotations.Select(a => a.Envelope).ToArray(), _ => null).HeaderStatus);
        // An outline smuggled into a v2 file is not accepted as editable data.
        var forged = SchemaV2.Read(SetVersion(data.Header, 2), data.Annotations.Select(a => SetVersion(a.Envelope, 2)).ToArray(), _ => null);
        Assert.Equal(HeaderStatus.Valid, forged.HeaderStatus);
        Assert.Equal([AnnotationReadStatus.Rejected, AnnotationReadStatus.Rejected, AnnotationReadStatus.Restored], forged.Annotations.Select(a => a.Status));
        // v1/v2 files still read through v3.
        var old = SchemaV2.Serialize(session.Current with { Pages = [session.Current.Pages[0] with { Annotations = [dot] }] });
        Assert.Equal(HeaderStatus.Valid, SchemaV3.Read(old.Header, old.Annotations.Select(a => a.Envelope).ToArray(), _ => null).HeaderStatus);
    }

    [Fact]
    public void InvalidOutlinesAreRejectedByTheValidator()
    {
        var stroke = TestModels.Ink();
        ImmutableArray<ImmutableArray<Point2>> triangle = [[new(0, 0), new(10, 0), new(0, 10)]];
        Assert.Null(ModelValidator.ValidateAnnotation(stroke with { Points = [], Outline = triangle }, _ => true));
        Assert.NotNull(ModelValidator.ValidateAnnotation(stroke with { Outline = triangle }, _ => true)); // both shapes
        Assert.NotNull(ModelValidator.ValidateAnnotation(stroke with { Points = [], Outline = [[new(0, 0), new(1, 1)]] }, _ => true));
        Assert.NotNull(ModelValidator.ValidateAnnotation(stroke with { Points = [], Outline = [[new(0, 0), new(double.NaN, 1), new(2, 2)]] }, _ => true));
        Assert.NotNull(ModelValidator.ValidateAnnotation(stroke with { Outline = [] }, _ => true));
    }

    [Fact]
    public void GestureWithoutContactChangesNothing()
    {
        var stroke = Ink([new(0, 50), new(200, 50)], width: 4);
        var (session, pageId) = Session(stroke);
        var command = Erase(session, pageId, EraserMode.Area, 10, new Point2(100, 58), new Point2(150, 58)); // circle edge at y=53, stroke edge at 52
        Assert.True(command.IsEmpty);
        Assert.True(Erase(session, pageId, EraserMode.Stroke, 10, new Point2(100, 58)).IsEmpty);
    }
}
