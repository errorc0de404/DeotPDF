using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;

namespace PdfNotes.Tests.Core;

/// <summary>P3 T1: pen pressure in the model, geometry, eraser, schema v8, clipboard and the pen tool (spec 7.6).</summary>
public sealed class PressureTests
{
    private static readonly RgbColor Blue = new(20, 40, 200);

    /// <summary>From (0,0) to (100,0), width 4: half-width 0.8 at the thin end (pressure 0) and 3.2 at the thick end (1).</summary>
    private static InkStroke Wedge(Affine? transform = null) =>
        new(Guid.NewGuid(), false, transform ?? Affine.Identity, [new(0, 0), new(100, 0)], 4, Blue, 1) { Pressures = [0, 1] };

    private static DocumentModel With(params Annotation[] annotations)
    {
        var model = TestModels.Empty();
        return model with { Pages = model.Pages.SetItem(0, model.Pages[0] with { Annotations = [.. annotations] }) };
    }

    [Fact]
    public void PressureMapsToFourTenthsToOneAndSixTenthsOfTheWidth()
    {
        Assert.Equal(0.4, StrokeGeometry.PressureFactor(0), 9);
        Assert.Equal(1.0, StrokeGeometry.PressureFactor(0.5), 9);
        Assert.Equal(1.6, StrokeGeometry.PressureFactor(1), 9);
        Assert.Equal(1.6, StrokeGeometry.PressureFactor(3), 9);
        Assert.Equal(1.0, StrokeGeometry.PressureFactor(double.NaN), 9);
    }

    [Fact]
    public void SimplificationKeepsAPressureSwellOnAStraightLine()
    {
        var raw = Enumerable.Range(0, 21).Select(i => new Point2(i * 5, 0)).ToArray();
        var pressures = Enumerable.Range(0, 21).Select(i => i == 10 ? 0.95 : 0.3).ToArray();
        Assert.Equal(2, StrokeGeometry.Finish(raw, 0.05).Length); // the line alone needs only its ends
        var (points, kept) = StrokeGeometry.Finish(raw, pressures, 0.05);
        Assert.Equal(points.Length, kept.Length);
        Assert.True(points.Length >= 3, $"{points.Length} points");
        Assert.True(kept.Max() > 0.5, "the swell survives (softened by smoothing)");
        Assert.Equal(0.3, kept[0], 9);
        Assert.Equal(0.3, kept[^1], 9);
        // A steady pressure on the same line simplifies to the ends.
        var (plain, _) = StrokeGeometry.Finish(raw, Enumerable.Repeat(0.4, 21).ToArray(), 0.05);
        Assert.Equal(2, plain.Length);
    }

    [Fact]
    public void SmoothingSoftensAPressureSpikeLikeAJitter()
    {
        Point2[] points = [new(0, 0), new(10, 1), new(20, 0)];
        var (smoothed, pressures) = StrokeGeometry.Smooth(points, [0.2, 1.0, 0.2]);
        Assert.Equal(new Point2(10, 0.5), smoothed[1]);
        Assert.Equal((0.2 + 2 + 0.2) / 4, pressures[1], 9);
        Assert.Equal(0.2, pressures[0], 9);
    }

    [Fact]
    public void TheRegionBoundsAndPickingFollowTheWidthAlongTheStroke()
    {
        var wedge = Wedge();
        var region = StrokeRegion.PressureRegion(wedge);
        // The thick end reaches 3.2 off the line, the thin end only 0.8.
        Assert.True(Clipper2Lib.Clipper.PointInPolygon(new(99, 3.0), region[0]) != Clipper2Lib.PointInPolygonResult.IsOutside);
        Assert.Equal(Clipper2Lib.PointInPolygonResult.IsOutside, Clipper2Lib.Clipper.PointInPolygon(new(1, 1.5), region[0]));
        var bounds = AnnotationGeometry.LocalBounds(wedge);
        Assert.Equal(-0.8, bounds.X, 6);
        Assert.Equal(103.2, bounds.X + bounds.Width, 6);
        Assert.Equal(-3.2, bounds.Y, 6);
        Assert.True(AnnotationGeometry.Hit(wedge, new(95, 2.8), 0));
        Assert.False(AnnotationGeometry.Hit(wedge, new(5, 2.8), 0));
        Assert.True(AnnotationGeometry.Hit(wedge, new(5, 2.8), 2)); // tolerance still counts
        // Area: the tangent hull of the two end circles, about a trapezoid plus the caps.
        double expected = 100 * (0.8 + 3.2) + Math.PI * 3.2 * 3.2 / 2 + Math.PI * 0.8 * 0.8 / 2;
        Assert.InRange(StrokeRegion.Area(region), expected * 0.97, expected * 1.03);
        // The painter's pieces cover the same area when joined.
        var joined = Clipper2Lib.Clipper.Union(StrokeRegion.ToPaths([.. StrokeRegion.PressurePieces(wedge)]), [], Clipper2Lib.FillRule.NonZero, 3);
        Assert.Equal(StrokeRegion.Area(region), StrokeRegion.Area(joined), 3);
    }

    [Fact]
    public void PiecesTurnTheSameWaySoANonZeroFillIsTheirUnion()
    {
        var wedge = Wedge() with { Points = [new(0, 0), new(50, 30), new(100, 0), new(20, -40)], Pressures = [0.1, 0.9, 0.3, 1] };
        foreach (var piece in StrokeRegion.PressurePieces(wedge))
        {
            double twiceArea = 0;
            for (int i = 0, j = piece.Length - 1; i < piece.Length; j = i++) twiceArea += (piece[j].X * piece[i].Y) - (piece[i].X * piece[j].Y);
            Assert.True(twiceArea > 0, "every piece turns with increasing angle");
        }
    }

    [Fact]
    public void ValidationChecksPressures()
    {
        Assert.Null(ModelValidator.ValidateAnnotation(Wedge(), _ => true));
        Assert.NotNull(ModelValidator.ValidateAnnotation(Wedge() with { Pressures = [0.5] }, _ => true));
        Assert.NotNull(ModelValidator.ValidateAnnotation(Wedge() with { Pressures = [0.5, 1.5] }, _ => true));
        Assert.NotNull(ModelValidator.ValidateAnnotation(Wedge() with { Pressures = [0.5, double.NaN] }, _ => true));
        Assert.NotNull(ModelValidator.ValidateAnnotation(Wedge() with { Points = [], Outline = [[new(0, 0), new(1, 0), new(0, 1)]] }, _ => true));
        Assert.Null(ModelValidator.ValidateAnnotation(Wedge() with { Pressures = [] }, _ => true)); // empty = even width
    }

    [Fact]
    public void AreaErasingAPressureStrokeCutsItsRealShapeAndLeavesPiecesWithoutPressures()
    {
        var wedge = Wedge();
        var session = new DocumentSession(With(wedge));
        var pageId = session.Current.Pages[0].Id;
        // 2.0 off the line near the thin end: outside its 0.8 half-width, so nothing is erased there.
        var miss = new EraseGesture(session.Current, pageId, EraserMode.Area, 1);
        miss.AddPoint(new(5, 2.2)); miss.AddPoint(new(10, 2.2));
        Assert.Empty(miss.Commit().Operations);
        // A cut through the middle splits it into two pieces of the same paint.
        var cut = new EraseGesture(session.Current, pageId, EraserMode.Area, 6);
        cut.AddPoint(new(50, -10)); cut.AddPoint(new(50, 10));
        Assert.True(session.Execute(cut.Commit()));
        var pieces = session.Current.Pages[0].Annotations.Cast<InkStroke>().ToArray();
        Assert.Equal(2, pieces.Length);
        Assert.All(pieces, p => { Assert.True(p.IsOutlined); Assert.False(p.HasPressure); Assert.Null(ModelValidator.ValidateAnnotation(p, _ => true)); });
        // The piece at the thick end is taller than the one at the thin end.
        var heights = pieces.Select(p => StrokeRegion.Bounds(p.Outline).Height).Order().ToArray();
        Assert.True(heights[1] > heights[0] * 1.5);
        // Stroke mode: touching the thick end's edge removes it; the same distance at the thin end does not.
        var whole = new DocumentSession(With(Wedge()));
        var far = new EraseGesture(whole.Current, whole.Current.Pages[0].Id, EraserMode.Stroke, 1);
        far.AddPoint(new(2, 2.5));
        Assert.Empty(far.Commit().Operations);
        var near = new EraseGesture(whole.Current, whole.Current.Pages[0].Id, EraserMode.Stroke, 1);
        near.AddPoint(new(98, 3.5));
        Assert.True(whole.Execute(near.Commit()));
        Assert.Empty(whole.Current.Pages[0].Annotations);
    }

    [Fact]
    public void ScalingKeepsPressuresAndScalesTheWidth()
    {
        var scaled = (InkStroke)AnnotationGeometry.Similarity(Wedge(), new(0, 0), scale: 2);
        Assert.Equal(8, scaled.WidthPt, 9);
        Assert.Equal([0, 1], scaled.Pressures.ToArray());
        Assert.Equal(6.4, StrokeGeometry.MaxRadius(scaled), 9);
    }

    [Fact]
    public void SchemaV8IsWrittenOnlyForPressureStrokesAndRoundTrips()
    {
        var plain = With(TestModels.Ink());
        Assert.Equal(6, Version(SchemaV8.Serialize(plain).Header));

        var wedge = Wedge(Affine.Rigid(.3, 40, 60)) with { Pressures = [0.1234, 0.9876] };
        var model = With(wedge, TestModels.Ink());
        var data = SchemaV8.Serialize(model);
        Assert.Equal(8, Version(data.Header));
        Assert.All(data.Annotations, a => Assert.Equal(8, Version(a.Envelope)));
        var read = SchemaV8.Read(data.Header, data.Annotations.Select(a => a.Envelope).ToArray(), _ => null);
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        Assert.All(read.Annotations, a => Assert.Equal(AnnotationReadStatus.Restored, a.Status));
        var back = read.Model!.Pages[0].Annotations.OfType<InkStroke>().ToArray();
        Assert.Equal([0.1234, 0.9876], back[0].Pressures.ToArray());
        Assert.False(back[1].HasPressure);
        Assert.Equal(data.Header, SchemaV8.Serialize(read.Model).Header);
        Assert.Equal(data.Annotations.Select(a => a.Envelope), SchemaV8.Serialize(read.Model).Annotations.Select(a => a.Envelope));

        // Tags and pressures together.
        var tagged = model with { Tags = model.Tags.Add(wedge.Id, new AnnotationTag("ai", "b1", "k1", null)) };
        var both = SchemaV8.Serialize(tagged);
        var bothRead = SchemaV8.Read(both.Header, both.Annotations.Select(a => a.Envelope).ToArray(), _ => null);
        Assert.Equal("k1", bothRead.Model!.TagOf(wedge.Id)?.SourceKey);
        Assert.True(bothRead.Model.Pages[0].Annotations.OfType<InkStroke>().First().HasPressure);

        // v7 readers refuse it as an unknown version (appearances stay read-only in older apps).
        Assert.NotEqual(HeaderStatus.Valid, SchemaV7.Read(data.Header, data.Annotations.Select(a => a.Envelope).ToArray(), _ => null).HeaderStatus);
    }

    [Fact]
    public void BadPressuresInAFileAreDroppedNotTheStroke()
    {
        var model = With(Wedge());
        var data = SchemaV8.Serialize(model);
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(data.Annotations[0].Envelope)!;
        envelope["annotation"]!["pressures"] = new System.Text.Json.Nodes.JsonArray(0.5, 0.5, 0.5); // three values for two points
        byte[] broken = System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString());
        var read = SchemaV8.Read(data.Header, [broken], _ => null);
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        var stroke = Assert.IsType<InkStroke>(Assert.Single(read.Model!.Pages[0].Annotations));
        Assert.False(stroke.HasPressure);
        Assert.Contains(read.Warnings, w => w.Contains("Pressures"));
        // Pressures on a highlighter stroke are not part of v8: that envelope is rejected.
        var highlight = With(new FreeHighlightStroke(Guid.NewGuid(), false, Affine.Identity, [new(0, 0), new(10, 0)], 10, Blue, .4));
        var h = SchemaV8.Serialize(highlight with { Pages = highlight.Pages.SetItem(0, highlight.Pages[0] with { Annotations = highlight.Pages[0].Annotations.Add(Wedge()) }) });
        var node = System.Text.Json.Nodes.JsonNode.Parse(h.Annotations[0].Envelope)!;
        node["annotation"]!["pressures"] = new System.Text.Json.Nodes.JsonArray(0.5, 0.5);
        var hr = SchemaV8.Read(h.Header, [System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()), h.Annotations[1].Envelope], _ => null);
        Assert.Equal(AnnotationReadStatus.Rejected, hr.Annotations[0].Status);
    }

    /// <summary>R3h: JSON nulls where v8 expects objects or numbers reject that part instead of throwing.</summary>
    [Fact]
    public void NullsInV8DataAreRejectedNotThrown()
    {
        var data = SchemaV8.Serialize(With(Wedge()));
        byte[][] envelopes = [.. data.Annotations.Select(a => a.Envelope)];
        // A null pressure value: that envelope is rejected, the read goes on.
        var node = System.Text.Json.Nodes.JsonNode.Parse(envelopes[0])!;
        node["annotation"]!["pressures"] = new System.Text.Json.Nodes.JsonArray(0.5, null);
        var read = SchemaV8.Read(data.Header, [System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())], _ => null);
        Assert.Equal(AnnotationReadStatus.Rejected, read.Annotations[0].Status);
        // An envelope that is JSON null: rejected.
        var empty = SchemaV8.Read(data.Header, [System.Text.Encoding.UTF8.GetBytes("null")], _ => null);
        Assert.Equal(AnnotationReadStatus.Rejected, empty.Annotations[0].Status);
    }

    [Fact]
    public void TheClipboardCarriesPressures()
    {
        var wedge = Wedge();
        var model = With(wedge);
        var text = AnnotationTransfer.Encode(model, model.Pages[0].Id, [wedge.Id]);
        var copy = AnnotationTransfer.Decode(text, _ => { });
        var pasted = Assert.IsType<InkStroke>(Assert.Single(copy.Annotations));
        Assert.Equal([0, 1], pasted.Pressures.ToArray());
    }

    [Theory]
    [InlineData(true, PointerDevice.Pen, true)]
    [InlineData(false, PointerDevice.Pen, false)]  // setting off
    [InlineData(true, PointerDevice.Mouse, false)] // a mouse never has pressure
    [InlineData(true, PointerDevice.Touch, false)]
    public void ThePenToolRecordsPressureOnlyFromAPenWithTheSettingOn(bool setting, PointerDevice device, bool expected)
    {
        var session = new DocumentSession(TestModels.Empty());
        var view = new FlatView(session.Current.Pages[0]);
        var tool = new StrokeTool(view, () => session, () => StrokeStyle.DefaultPen, AnnotationKind.Ink, () => setting);
        double?[] pressures = [0.2, 0.4, 0.9, 0.9, 0.3];
        for (int i = 0; i < pressures.Length; i++)
        {
            var pointer = new CanvasPointer(new(100 + i * 30, 100 + (i % 2) * 15), InputModifiers.None, Device: device, Pressure: device == PointerDevice.Mouse ? null : pressures[i]);
            if (i == 0) Assert.True(tool.PointerDown(pointer));
            else if (i < pressures.Length - 1) tool.PointerMove(pointer);
            else tool.PointerUp(pointer);
        }
        var stroke = Assert.IsType<InkStroke>(Assert.Single(session.Current.Pages[0].Annotations));
        Assert.Equal(expected, stroke.HasPressure);
        if (expected)
        {
            Assert.Equal(stroke.Points.Length, stroke.Pressures.Length);
            Assert.All(stroke.Pressures, p => Assert.Equal(Math.Round(p, 4), p));
            Assert.Equal(0.2, stroke.Pressures[0], 9);
        }
        // The highlighter never takes pressure.
        var marker = new StrokeTool(view, () => session, () => StrokeStyle.DefaultPen, AnnotationKind.FreeHighlight, () => true);
        marker.PointerDown(new(new(100, 300), InputModifiers.None, Device: PointerDevice.Pen, Pressure: 0.9));
        marker.PointerUp(new(new(200, 300), InputModifiers.None, Device: PointerDevice.Pen, Pressure: 0.1));
        Assert.IsType<FreeHighlightStroke>(session.Current.Pages[0].Annotations[^1]);
    }

    private static int Version(byte[] json) => System.Text.Json.Nodes.JsonNode.Parse(json)!["schemaVersion"]!.GetValue<int>();

    /// <summary>One page at 100% with its top-left corner at the viewport origin.</summary>
    private sealed class FlatView(Page page) : ICanvasView
    {
        private readonly ViewTransform transform = new(page.Setup.Frame, 1, 1, default);
        public PageAnchor? HitTestPage(Point2 viewportPoint) => new(0, transform.DipToPage.Transform(viewportPoint));
        public ViewTransform PageTransform(int index) => transform;
        public double DpiScale => 1;
        public Point2 Offset => default;
        public void SetHorizontalOffset(double value) { }
        public void SetVerticalOffset(double value) { }
        public void SetPreview(int pageIndex, ICanvasPreview? preview, bool mark = false) { }
        public void RefreshPreview() { }
        public void RefreshOverlays() { }
        public void RefreshOverlay(int pageIndex) { }
    }
}
