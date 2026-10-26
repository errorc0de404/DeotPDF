using System.Collections.Immutable;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Commands;

/// <summary>
/// Immutable selection snapshot; binaries are shared, while pasted annotations and resources get fresh IDs. The page it
/// came from (<see cref="SourcePageId"/>, <see cref="SourceFrame"/>) lets a paste on another page keep where the
/// annotations show on the paper (spec 5.7, D14g).
/// </summary>
public sealed record AnnotationClipboard(ImmutableArray<Annotation> Annotations, ImmutableArray<Resource> Resources,
    Guid? SourcePageId = null, PageFrame? SourceFrame = null)
{
    /// <summary>How far a paste on the same place steps aside (spec 5.7: 10pt), again for every copy already there.</summary>
    public const double StepPt = 10;

    public static AnnotationClipboard Capture(DocumentModel model, Guid pageId, IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        var page = model.GetPage(pageId);
        // Highlights bound to PDF glyphs mean nothing at an offset or on another page; they are not copied (spec 9.4).
        var annotations = page.Annotations.Where(a => selected.Contains(a.Id) && AnnotationGeometry.CanTransform(a)).ToImmutableArray();
        var resources = annotations.OfType<ImageAnnotation>().Select(a => a.ResourceId).Distinct().Select(id => model.Resources[id]).ToImmutableArray();
        return new(annotations, resources, pageId, page.Setup.Frame);
    }

    /// <summary>
    /// Pastes onto page <paramref name="pageId"/> (spec 5.7). With <paramref name="center"/> (canonical points of that
    /// page) the copies are centered there. Without it they keep their place: on their own page they step aside by
    /// <see cref="StepPt"/> (past earlier copies at the same place), on another page they keep where they show on the
    /// displayed paper (rotation included). Copies that would land completely outside the page are centered on it.
    /// </summary>
    public (EditCommand Command, Guid[] Ids) Paste(DocumentModel model, Guid pageId, Point2? center = null, Func<TextBoxAnnotation, Rect2>? textBounds = null)
    {
        var page = model.GetPage(pageId);
        var resources = Resources.ToDictionary(r => r.Id, r => r with { Id = Guid.NewGuid() });
        var copies = Annotations.Select(a => a with { Id = Guid.NewGuid(), Locked = false }).Select(a =>
            a is ImageAnnotation i ? i with { ResourceId = resources[i.ResourceId].Id } : a).ToArray();
        bool samePage = SourcePageId is not { } source || source == pageId;
        if (!samePage && SourceFrame is { } from)
        {
            var change = from.CanonicalToDisplay.Then(page.Setup.Frame.DisplayToCanonical);
            copies = copies.Select(a => Moved(a, change)).ToArray();
        }
        var bounds = AnnotationGeometry.Bounds(copies, textBounds);
        double dx = 0, dy = 0;
        if (center is { } c)
        {
            dx = c.X - (bounds.X + bounds.Width / 2);
            dy = c.Y - (bounds.Y + bounds.Height / 2);
        }
        else if (samePage)
        {
            int step = 1;
            while (step < 100 && page.Annotations.Any(a => SameBox(AnnotationGeometry.Bounds([a], textBounds), bounds, StepPt * step))) step++;
            dx = dy = StepPt * step;
        }
        if (bounds.X + bounds.Width + dx <= 0 || bounds.X + dx >= page.Setup.WidthPt) dx = page.Setup.WidthPt / 2 - (bounds.X + bounds.Width / 2);
        if (bounds.Y + bounds.Height + dy <= 0 || bounds.Y + dy >= page.Setup.HeightPt) dy = page.Setup.HeightPt / 2 - (bounds.Y + bounds.Height / 2);
        copies = copies.Select(a => AnnotationGeometry.Similarity(a, default, move: new(dx, dy))).ToArray();
        var ops = resources.Values.Select(r => (EditOperation)new AddResourceOperation(r)).ToList();
        for (int i = 0; i < copies.Length; i++) ops.Add(new InsertAnnotationOperation(pageId, page.Annotations.Count + i, copies[i]));
        return (new("undo.paste", ops), copies.Select(a => a.Id).ToArray());
    }

    /// <summary>An annotation moved by a rigid change of page coordinates (a quarter-turn rotation and a shift), keeping its size.</summary>
    public static Annotation Moved(Annotation a, Affine change) =>
        AnnotationGeometry.Similarity(a, default, 1, Math.Atan2(change.M12, change.M11), new(change.OffsetX, change.OffsetY));

    private static bool SameBox(Rect2 a, Rect2 b, double shift) =>
        Math.Abs(a.X - b.X - shift) < 0.01 && Math.Abs(a.Y - b.Y - shift) < 0.01 && Math.Abs(a.Width - b.Width) < 0.01 && Math.Abs(a.Height - b.Height) < 0.01;
}
