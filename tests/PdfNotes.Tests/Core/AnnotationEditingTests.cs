using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Tests.Core;

public sealed class AnnotationEditingTests
{
    private static InkStroke Stroke => new(Guid.NewGuid(), false, Affine.Rigid(.4, 30, 40), [new(10, 20), new(90, 80)], 4, RgbColor.Black, .7);
    [Fact]
    public void SimilarityPreservesWorldGeometryAndBakesWidthExactlyOnce()
    {
        var stroke = Stroke;
        var center = new Point2(130, 150);
        var changed = (InkStroke)AnnotationGeometry.Similarity(stroke, center, 2.5, .8, new(12, -3));
        var worldChange = Affine.Translation(-130, -150).Then(Affine.Scale(2.5)).Then(Affine.Rotation(.8)).Then(Affine.Translation(142, 147));
        Assert.True(changed.Transform.IsRigid);
        Assert.Equal(10, changed.WidthPt);
        for (int i = 0; i < stroke.Points.Length; i++)
            Assert.True(changed.Transform.Transform(changed.Points[i]).DistanceTo(worldChange.Transform(stroke.Transform.Transform(stroke.Points[i]))) < 1e-9);
        var twice = (InkStroke)AnnotationGeometry.Similarity(changed, center, 2);
        Assert.Equal(20, twice.WidthPt);
    }
    [Fact]
    public void MixedTransformIsOneUndoAndReturnsToSavePoint()
    {
        var model = DocumentModel.Create([new(600, 800, 0, PageSource.Generated, PageBackground.Blank)]);
        var session = new DocumentSession(model);
        var stroke = Stroke;
        session.Execute(DocumentEdits.AddAnnotation(model, model.Pages[0].Id, stroke));
        var dot = Stroke;
        session.Execute(DocumentEdits.AddAnnotation(session.Current, model.Pages[0].Id, dot));
        session.MarkSaved(session.CreateSnapshot());
        var before = session.Current;
        session.Execute(DocumentEdits.UpdateAnnotations(before, before.AllAnnotations.Select(a => AnnotationGeometry.Similarity(a, new(50, 50), 3, .6)), "크기 조절"));
        Assert.True(session.IsDirty);
        session.Undo();
        Assert.Equal(before.AllAnnotations, session.Current.AllAnnotations); Assert.False(session.IsDirty);
        session.Redo(); Assert.True(session.IsDirty);
    }
    [Fact]
    public void ImageFreeResizeKeepsOppositeWorldCornerAndHitFollowsRotation()
    {
        var image = new ImageAnnotation(Guid.NewGuid(), false, Affine.Rigid(Math.PI / 2, 300, 200), Guid.NewGuid(), new(0, 0, 100, 50));
        var resized = AnnotationGeometry.ResizeImage(image, new(100, 50), 2, 3);
        Assert.Equal(new Rect2(-100, -100, 200, 150), resized.LocalBounds);
        Assert.True(AnnotationGeometry.Hit(resized, resized.Transform.Transform(new(-50, -50)), 0));
        Assert.False(AnnotationGeometry.Hit(resized, resized.Transform.Transform(new(110, 60)), 0));
        Assert.Equal(image.Transform.Transform(new(100, 50)), resized.Transform.Transform(new(resized.LocalBounds.X + resized.LocalBounds.Width, resized.LocalBounds.Y + resized.LocalBounds.Height)));
    }
    [Fact]
    public void PasteGetsIndependentIdsAndKeepsOrderAndBinarySharing()
    {
        var resource = Resource.Create(Guid.NewGuid(), "image/png", [137, 80, 78, 71, 13, 10, 26, 10]);
        var model = DocumentModel.Create([new(600, 800, 0, PageSource.Generated, PageBackground.Blank)]);
        var session = new DocumentSession(model);
        var image = new ImageAnnotation(Guid.NewGuid(), false, Affine.Translation(40, 50), resource.Id, new(0, 0, 100, 50));
        session.Execute(DocumentEdits.AddAnnotation(model, model.Pages[0].Id, image, resource));
        var stroke = Stroke;
        session.Execute(DocumentEdits.AddAnnotation(session.Current, model.Pages[0].Id, stroke));
        var data = AnnotationClipboard.Capture(session.Current, model.Pages[0].Id, [stroke.Id, image.Id]);
        var result = data.Paste(session.Current, model.Pages[0].Id);
        session.Execute(result.Command);
        var copy = Assert.IsType<ImageAnnotation>(session.Current.Pages[0].Annotations[2]);
        Assert.NotEqual(image.Id, copy.Id); Assert.NotEqual(image.ResourceId, copy.ResourceId);
        Assert.Equal(resource.Payload, session.Current.Resources[copy.ResourceId].Payload);
        Assert.Equal(50, copy.Transform.OffsetX); Assert.Equal(60, copy.Transform.OffsetY);
        Assert.IsType<InkStroke>(session.Current.Pages[0].Annotations[3]);
        session.Undo(); Assert.Equal(2, session.Current.AllAnnotations.Count()); Assert.Single(session.Current.Resources);
        session.Redo(); Assert.Equal(copy.Id, session.Current.Pages[0].Annotations[2].Id);
    }
    [Fact]
    public void EntirelyOutsidePasteIsBroughtIntoPageAndLockPreventsTransforms()
    {
        var stroke = Stroke with { Transform = Affine.Translation(2000, 3000) };
        var model = DocumentModel.Create([new(600, 800, 0, PageSource.Generated, PageBackground.Blank)]);
        var data = new AnnotationClipboard([stroke], []);
        var session = new DocumentSession(model);
        session.Execute(data.Paste(model, model.Pages[0].Id).Command);
        var b = AnnotationGeometry.Bounds(session.Current.AllAnnotations);
        Assert.InRange(b.X + b.Width / 2, 0, 600); Assert.InRange(b.Y + b.Height / 2, 0, 800);
        Assert.Throws<AnnotationLockedException>(() => AnnotationGeometry.Similarity(stroke with { Locked = true }, default, 2));
    }
}
