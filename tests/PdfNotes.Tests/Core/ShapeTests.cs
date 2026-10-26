using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Nodes;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;

namespace PdfNotes.Tests.Core;

/// <summary>D11 shapes: validation, geometry (bounds, picking, lasso, scaling, free resize) and schema v5.</summary>
public sealed class ShapeTests
{
    public static ShapeAnnotation Box(ShapeKind kind, Rect2 box, ShapeStyle? style = null, Affine? transform = null) =>
        new(Guid.NewGuid(), false, transform ?? Affine.Identity, kind, box, [], style ?? ShapeStyle.Default);

    public static ShapeAnnotation Poly(ShapeKind kind, Point2[] points, ShapeStyle? style = null) =>
        new(Guid.NewGuid(), false, Affine.Identity, kind, default, [.. points], style ?? ShapeStyle.Default);

    private static readonly ShapeStyle Outline = ShapeStyle.Default with { FillOpacity = 0, BorderWidthPt = 4 };

    [Fact]
    public void ValidationRequiresAKnownShapeWithAFillOrABorder()
    {
        Assert.Null(ModelValidator.ValidateAnnotation(Box(ShapeKind.Rectangle, new(10, 10, 50, 30)), _ => false));
        Assert.Null(ModelValidator.ValidateAnnotation(Poly(ShapeKind.Polyline, [new(0, 0), new(30, 10)]), _ => false));
        Assert.NotNull(ModelValidator.ValidateAnnotation(Box(ShapeKind.Ellipse, new(10, 10, 0, 30)), _ => false));
        Assert.NotNull(ModelValidator.ValidateAnnotation(Poly(ShapeKind.Polygon, [new(0, 0), new(30, 10)]), _ => false));
        Assert.NotNull(ModelValidator.ValidateAnnotation(Box(ShapeKind.Rectangle, new(0, 0, 10, 10)) with { Points = [new(1, 1)] }, _ => false));
        Assert.NotNull(ModelValidator.ValidateAnnotation(Poly(ShapeKind.Polygon, [new(0, 0), new(30, 10), new(5, 20)]) with { Box = new(0, 0, 1, 1) }, _ => false));
        Assert.NotNull(ModelValidator.ValidateAnnotation(Box(ShapeKind.Rectangle, new(0, 0, 10, 10), Outline with { BorderEnabled = false }), _ => false));
        Assert.NotNull(ModelValidator.ValidateAnnotation(Poly(ShapeKind.Polyline, [new(0, 0), new(9, 9)], ShapeStyle.Default with { BorderEnabled = false }), _ => false));
        Assert.Null(ModelValidator.ValidateAnnotation(Box(ShapeKind.Triangle, new(0, 0, 10, 10), ShapeStyle.Default with { BorderEnabled = false }), _ => false));
        Assert.NotNull(ModelValidator.ValidateAnnotation(Box(ShapeKind.Rectangle, new(0, 0, 10, 10), ShapeStyle.Default with { FillOpacity = 1.5 }), _ => false));
    }

    [Fact]
    public void BoundsIncludeHalfTheBorderAndPickingFollowsFillAndBorder()
    {
        var filled = Box(ShapeKind.Rectangle, new(100, 100, 80, 40), ShapeStyle.Default with { BorderWidthPt = 6 });
        Assert.Equal(new Rect2(97, 97, 86, 46), ShapeGeometry.Bounds(filled));
        Assert.True(AnnotationGeometry.Hit(filled, new(140, 120), 0));
        var hollow = filled with { Style = Outline };
        Assert.False(AnnotationGeometry.Hit(hollow, new(140, 120), 1)); // no fill: the inside is not the shape
        Assert.True(AnnotationGeometry.Hit(hollow, new(140, 101.5), 0));
        Assert.True(AnnotationGeometry.Hit(hollow, new(140, 97), 1.5)); // border half width 2 + tolerance
        var ellipse = Box(ShapeKind.Ellipse, new(0, 0, 100, 50));
        Assert.True(AnnotationGeometry.Hit(ellipse, new(50, 25), 0));
        Assert.False(AnnotationGeometry.Hit(ellipse, new(4, 4), 0)); // the box corner is outside the ellipse
        var triangle = Box(ShapeKind.Triangle, new(0, 0, 100, 100), ShapeStyle.Default with { BorderEnabled = false });
        Assert.True(AnnotationGeometry.Hit(triangle, new(50, 60), 0));
        Assert.False(AnnotationGeometry.Hit(triangle, new(10, 20), 0));
        // A self-crossing star is filled even-odd: its center pentagon is a hole (same rule as the PDF f*).
        Point2[] star = [.. Enumerable.Range(0, 5).Select(i => new Point2(50 + 40 * Math.Cos(-Math.PI / 2 + i * 4 * Math.PI / 5), 50 + 40 * Math.Sin(-Math.PI / 2 + i * 4 * Math.PI / 5)))];
        var filledStar = Poly(ShapeKind.Polygon, star, ShapeStyle.Default with { BorderEnabled = false });
        Assert.False(AnnotationGeometry.Hit(filledStar, new(50, 52), 0));
        Assert.True(AnnotationGeometry.Hit(filledStar, new(50, 18), 0));
    }

    [Fact]
    public void LassoTakesTheFillOrTheBorderButNotAnEmptyInside()
    {
        var hollow = Box(ShapeKind.Rectangle, new(100, 100, 200, 200), Outline);
        var filled = Box(ShapeKind.Ellipse, new(400, 100, 100, 100));
        var page = Page.Create(new PageSetup(600, 400, 0, PageSource.Generated, PageBackground.Blank)) with { Annotations = [hollow, filled] };
        Point2[] Square(double x, double y, double s) => [new(x, y), new(x + s, y), new(x + s, y + s), new(x, y + s)];
        Assert.Empty(Lasso.Select(page, Square(150, 150, 50), _ => default)); // inside the empty rectangle
        Assert.Equal([hollow.Id], Lasso.Select(page, Square(90, 150, 20), _ => default)); // over its border
        Assert.Equal([filled.Id], Lasso.Select(page, Square(440, 140, 10), _ => default)); // inside the fill
    }

    [Fact]
    public void ScalingBakesIntoTheBoxAndBorderAndFreeResizeStretchesOnlyTheGeometry()
    {
        var rect = Box(ShapeKind.Rectangle, new(0, 0, 40, 20), ShapeStyle.Default with { BorderWidthPt = 2 }, Affine.Rigid(Math.PI / 6, 100, 50));
        var scaled = (ShapeAnnotation)AnnotationGeometry.Similarity(rect, new(100, 50), 2, angle: .3);
        Assert.True(scaled.Transform.IsRigid);
        Assert.Equal(new Rect2(0, 0, 80, 40), scaled.Box);
        Assert.Equal(4, scaled.Style.BorderWidthPt);
        var poly = Poly(ShapeKind.Polygon, [new(0, 0), new(10, 0), new(10, 10)]);
        var stretched = AnnotationGeometry.ResizeShape(poly, new(0, 0), 3, 1);
        Assert.Equal([new Point2(0, 0), new Point2(30, 0), new Point2(30, 10)], stretched.Points.ToArray());
        Assert.Equal(new Rect2(0, 0, 120, 20), AnnotationGeometry.ResizeShape(rect, new(0, 0), 3, 1).Box);
        // The frame corner is half a border outside the outline (-1, -1): the outline edge itself stays at 0.
        Assert.Equal(new Rect2(0, 0, 120, 40), AnnotationGeometry.ResizeShape(rect, new(-1, -1), 3, 2).Box);
        Assert.Equal(new Rect2(2, 3, 9, 4), ShapeGeometry.BoxBetween(new(11, 7), new(2, 3), false));
        Assert.Equal(new Rect2(2, -2, 9, 9), ShapeGeometry.BoxBetween(new(11, 7), new(2, 3), true));
        Assert.Throws<AnnotationLockedException>(() => AnnotationGeometry.ResizeShape(poly with { Locked = true }, default, 2, 2));
    }

    private static DocumentModel Note(params Annotation[] annotations)
    {
        var model = DocumentModel.Create([new PageSetup(500, 400, 0, PageSource.Generated, PageBackground.Blank)]);
        return model with { Pages = [model.Pages[0] with { Annotations = [.. annotations] }] };
    }

    public static Annotation[] AllShapes() =>
    [
        Box(ShapeKind.Rectangle, new(20, 20, 120, 60)),
        Box(ShapeKind.Ellipse, new(0, 0, 90, 60), ShapeStyle.Default with { FillColor = new(0xE5, 0x39, 0x35), FillOpacity = .5, BorderDash = DashStyle.Dash }, Affine.Rigid(.4, 200, 80)),
        Box(ShapeKind.Triangle, new(300, 20, 100, 90), ShapeStyle.Default with { BorderEnabled = false, FillOpacity = 1 }),
        Poly(ShapeKind.Polygon, [new(40, 200), new(140, 180), new(160, 260), new(60, 280)], Outline with { BorderDash = DashStyle.DashDot, BorderOpacity = .6 }),
        Poly(ShapeKind.Polyline, [new(250, 200), new(300, 260), new(360, 200), new(420, 260)], Outline with { BorderDash = DashStyle.Dot }) with { Locked = true },
    ];

    [Fact]
    public void SchemaV5StoresShapesAndOlderSchemasRefuseThem()
    {
        var model = Note(AllShapes());
        var data = SchemaV5.Serialize(model);
        Assert.Equal(5, (int?)JsonNode.Parse(data.Header)!["schemaVersion"]);
        Assert.All(data.Annotations, a => Assert.Equal("shape", (string?)JsonNode.Parse(a.Envelope)!["annotation"]!["type"]));
        var read = SchemaV5.Read(data.Header, data.Annotations.Select(a => a.Envelope).ToArray(), _ => null);
        Assert.Equal(HeaderStatus.Valid, read.HeaderStatus);
        Assert.True(SameMetadata(model, read.Model!));

        Assert.Throws<InvalidDataException>(() => SchemaV4.Serialize(model));
        Assert.Throws<InvalidDataException>(() => SchemaV3.Serialize(model));
        Assert.Equal(HeaderStatus.Unsupported, SchemaV4.Read(data.Header, [], _ => null).HeaderStatus);
        // A v5 shape envelope under a v4 header is not restored (the v4 reader does not know shapes).
        var v4 = SchemaV4.Serialize(Note());
        var foreign = SchemaV5.Read(v4.Header, [data.Annotations[0].Envelope], _ => null);
        Assert.Equal(HeaderStatus.Valid, foreign.HeaderStatus);
        Assert.All(foreign.Annotations, a => Assert.Equal(AnnotationReadStatus.Rejected, a.Status));
        // Older files keep reading through v5.
        Assert.Equal(HeaderStatus.Valid, SchemaV5.Read(v4.Header, [], _ => null).HeaderStatus);
    }

    /// <summary>Annotation records hold immutable arrays (reference equality), so models are compared by their metadata.</summary>
    public static bool SameMetadata(DocumentModel a, DocumentModel b)
    {
        var x = SchemaV5.Serialize(a); var y = SchemaV5.Serialize(b);
        return x.Header.AsSpan().SequenceEqual(y.Header) && x.Annotations.Length == y.Annotations.Length &&
            x.Annotations.Zip(y.Annotations).All(p => p.First.Envelope.AsSpan().SequenceEqual(p.Second.Envelope));
    }

    [Fact]
    public void PageShrinkAndClipboardKeepShapes()
    {
        var model = Note(AllShapes());
        var session = new DocumentSession(model);
        session.Execute(PageTransforms.Shrink(session.Current, [model.Pages[0].Id], 50, alignment: 0));
        var rect = (ShapeAnnotation)session.Current.Pages[0].Annotations[0];
        Assert.Equal(new Rect2(10, 10, 60, 30), rect.Box);
        Assert.Equal(1, rect.Style.BorderWidthPt);
        Assert.True(((ShapeAnnotation)session.Current.Pages[0].Annotations[4]).Locked);
        session.Undo();
        var clip = AnnotationClipboard.Capture(session.Current, model.Pages[0].Id, model.Pages[0].Annotations.Select(a => a.Id));
        var (command, ids) = clip.Paste(session.Current, model.Pages[0].Id);
        session.Execute(command);
        var copy = (ShapeAnnotation)session.Current.FindAnnotation(ids[0])!.Annotation;
        Assert.NotEqual(model.Pages[0].Annotations[0].Id, copy.Id);
        Assert.Equal(30, copy.Transform.OffsetX + copy.Box.X, 6);
        Assert.False(((ShapeAnnotation)session.Current.FindAnnotation(ids[4])!.Annotation).Locked);
    }
}
