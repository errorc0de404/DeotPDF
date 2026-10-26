using PdfNotes.Core.Models;

namespace PdfNotes.Core.Commands;

/// <summary>Raised when a user edit targets a locked annotation (spec 5.2).</summary>
public sealed class AnnotationLockedException() : InvalidOperationException("The annotation is locked.");

public enum ZOrderChange { Forward, Backward, ToFront, ToBack }

/// <summary>
/// Builds commands for common document edits from the current model. Builders apply user rules (locking,
/// last-page protection, resource cleanup); operations only guarantee reversible, consistent data changes.
/// A builder returns an empty command when nothing would change.
/// </summary>
public static class DocumentEdits
{
    /// <summary>Adds an annotation on top of the page. An image's new resource is added in the same Undo unit.</summary>
    public static EditCommand AddAnnotation(DocumentModel model, Guid pageId, Annotation annotation, Resource? resource = null,
        string label = "undo.addAnnotation")
    {
        var page = model.GetPage(pageId);
        var operations = new List<EditOperation>();
        if (resource is not null && !model.Resources.ContainsKey(resource.Id)) operations.Add(new AddResourceOperation(resource));
        operations.Add(new InsertAnnotationOperation(pageId, page.Annotations.Count, annotation));
        return new(label, operations);
    }

    /// <summary>Adds several annotations on top of one page, with their new resources, as one Undo unit (image drop, spec 24.11).</summary>
    public static EditCommand AddAnnotations(DocumentModel model, Guid pageId, IEnumerable<(Annotation Annotation, Resource? Resource)> items, string label = "undo.addAnnotation")
    {
        var page = model.GetPage(pageId);
        var operations = new List<EditOperation>();
        var resources = new HashSet<Guid>(model.Resources.Keys);
        int index = page.Annotations.Count;
        foreach (var (annotation, resource) in items)
        {
            if (resource is not null && resources.Add(resource.Id)) operations.Add(new AddResourceOperation(resource));
            operations.Add(new InsertAnnotationOperation(pageId, index++, annotation));
        }
        return new(label, operations);
    }

    /// <summary>Deletes annotations (possibly on several pages) as one Undo unit and drops resources nobody references.</summary>
    public static EditCommand DeleteAnnotations(DocumentModel model, IEnumerable<Guid> annotationIds, string label = "undo.delete")
    {
        var locations = Locate(model, annotationIds);
        if (locations.Any(l => l.Annotation.Locked)) throw new AnnotationLockedException();
        var operations = new List<EditOperation>();
        // Remove from the highest index down so recorded indices stay valid; Undo re-inserts in reverse.
        foreach (var l in locations.OrderBy(l => l.PageIndex).ThenByDescending(l => l.AnnotationIndex))
            operations.Add(new RemoveAnnotationOperation(l.Page.Id, l.AnnotationIndex, l.Annotation));
        AddOrphanedResourceRemovals(model, operations, locations.Select(l => l.Annotation));
        return new(label, operations);
    }

    /// <summary>
    /// Moves annotations to another page (spec 5.3, D14g: dragging between pages): they leave <paramref name="fromPageId"/>
    /// and go on top of <paramref name="toPageId"/> in their stacking order, keeping their IDs, as <paramref name="moved"/>
    /// (already in that page's coordinates). Locked annotations cannot move. One Undo unit.
    /// </summary>
    public static EditCommand MoveToPage(DocumentModel model, Guid fromPageId, Guid toPageId, IReadOnlyList<Annotation> moved)
    {
        var from = model.GetPage(fromPageId);
        var to = model.GetPage(toPageId);
        if (from.Id == to.Id) throw new ArgumentException("The annotations are on that page already.", nameof(toPageId));
        var ids = moved.Select(a => a.Id).ToHashSet();
        var operations = new List<EditOperation>();
        for (int i = from.Annotations.Count - 1; i >= 0; i--)
        {
            var a = from.Annotations[i];
            if (!ids.Contains(a.Id)) continue;
            if (a.Locked) throw new AnnotationLockedException();
            operations.Add(new RemoveAnnotationOperation(from.Id, i, a));
        }
        if (operations.Count != ids.Count) throw new KeyNotFoundException("An annotation is not on the page it moves from.");
        var order = from.Annotations.Select((a, i) => (a.Id, i)).ToDictionary(p => p.Id, p => p.i);
        int at = to.Annotations.Count;
        foreach (var a in moved.OrderBy(a => order[a.Id])) operations.Add(new InsertAnnotationOperation(to.Id, at++, a));
        return new("undo.moveToPage", operations);
    }

    /// <summary>Replaces annotations with edited versions (same ids). Locked annotations cannot be changed.</summary>
    public static EditCommand UpdateAnnotations(DocumentModel model, IEnumerable<Annotation> updated, string label = "undo.changeProperties",
        string? coalesceKey = null)
    {
        var operations = new List<EditOperation>();
        foreach (var after in updated)
        {
            var location = model.FindAnnotation(after.Id) ?? throw new KeyNotFoundException("Annotation does not exist.");
            if (location.Annotation.Locked || after.Locked) throw new AnnotationLockedException();
            if (!location.Annotation.Equals(after))
                operations.Add(new ReplaceAnnotationOperation(location.Page.Id, location.Annotation, after));
        }
        return new(label, operations, coalesceKey);
    }

    /// <summary>Storage hygiene also removes locked app-owned blank text; external annotations are not in this model.</summary>
    public static EditCommand RemoveBlankTextBoxes(DocumentModel model) => new("undo.removeEmptyText",
        model.Pages.SelectMany(page => page.Annotations.Select((a, i) => (a, i)).Where(x => x.a is TextBoxAnnotation { IsBlank: true })
            .OrderByDescending(x => x.i).Select(x => (EditOperation)new RemoveAnnotationOperation(page.Id, x.i, x.a))));

    public static EditCommand SetLocked(DocumentModel model, IEnumerable<Guid> annotationIds, bool locked)
    {
        var operations = Locate(model, annotationIds)
            .Where(l => l.Annotation.Locked != locked)
            .Select(l => (EditOperation)new ReplaceAnnotationOperation(l.Page.Id, l.Annotation, l.Annotation with { Locked = locked }));
        return new(locked ? "undo.lock" : "undo.unlock", operations);
    }

    /// <summary>Moves the selected annotations of one page in the stacking order, keeping their relative order.</summary>
    public static EditCommand ChangeZOrder(DocumentModel model, Guid pageId, IEnumerable<Guid> annotationIds, ZOrderChange change)
    {
        var page = model.GetPage(pageId);
        var selected = annotationIds.ToHashSet();
        if (selected.Any(id => page.IndexOf(id) < 0)) throw new KeyNotFoundException("Annotation is not on the page.");
        var order = page.Annotations.ToList();
        switch (change)
        {
            case ZOrderChange.ToFront:
                order = [.. order.Where(a => !selected.Contains(a.Id)), .. order.Where(a => selected.Contains(a.Id))];
                break;
            case ZOrderChange.ToBack:
                order = [.. order.Where(a => selected.Contains(a.Id)), .. order.Where(a => !selected.Contains(a.Id))];
                break;
            case ZOrderChange.Forward:
                for (int i = order.Count - 2; i >= 0; i--)
                    if (selected.Contains(order[i].Id) && !selected.Contains(order[i + 1].Id))
                        (order[i], order[i + 1]) = (order[i + 1], order[i]);
                break;
            case ZOrderChange.Backward:
                for (int i = 1; i < order.Count; i++)
                    if (selected.Contains(order[i].Id) && !selected.Contains(order[i - 1].Id))
                        (order[i], order[i - 1]) = (order[i - 1], order[i]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }
        if (order.SequenceEqual(page.Annotations, ReferenceEqualityComparer.Instance)) return new("undo.reorder", []);
        var operations = new List<EditOperation>();
        for (int i = page.Annotations.Count - 1; i >= 0; i--)
            if (selected.Contains(page.Annotations[i].Id))
                operations.Add(new RemoveAnnotationOperation(pageId, i, page.Annotations[i]));
        for (int i = 0; i < order.Count; i++)
            if (selected.Contains(order[i].Id))
                operations.Add(new InsertAnnotationOperation(pageId, i, order[i]));
        return new("undo.reorder", operations);
    }

    public static EditCommand InsertPage(DocumentModel model, int index, PageSetup setup, string label = "undo.insertPage") =>
        new(label, [new InsertPageOperation(index, Page.Create(setup))]);

    /// <summary>Deletes pages with their annotations. Refuses the whole command when no page would remain (spec 11.2).</summary>
    public static EditCommand DeletePages(DocumentModel model, IEnumerable<Guid> pageIds, string label = "undo.deletePages")
    {
        var ids = pageIds.ToHashSet();
        var targets = model.Pages.Select((page, index) => (page, index)).Where(p => ids.Contains(p.page.Id)).ToList();
        if (targets.Count != ids.Count) throw new KeyNotFoundException("Page does not exist.");
        if (targets.Count >= model.Pages.Count) throw new InvalidOperationException("The last page cannot be deleted.");
        var operations = new List<EditOperation>();
        foreach (var (page, index) in targets.OrderByDescending(t => t.index)) operations.Add(new RemovePageOperation(index, page));
        AddOrphanedResourceRemovals(model, operations, targets.SelectMany(t => t.page.Annotations));
        return new(label, operations);
    }

    public static EditCommand SetPageRotation(DocumentModel model, Guid pageId, int rotation)
    {
        var page = model.GetPage(pageId);
        if (page.Setup.Rotation == rotation) return new("undo.rotatePages", []);
        return new("undo.rotatePages", [new ReplacePageSetupOperation(pageId, page.Setup, page.Setup with { Rotation = rotation })]);
    }

    public const string ClearPagesLabel = "undo.clearNotes";

    /// <summary>
    /// "이 페이지의 필기 삭제" (spec 11.11, D13j): every app annotation on the pages, locked ones included (the user confirmed),
    /// as one Undo unit; unused image resources go too. Content of the PDF itself is not touched. Empty when there is none.
    /// </summary>
    public static EditCommand ClearPages(DocumentModel model, IEnumerable<Guid> pageIds)
    {
        var operations = new List<EditOperation>();
        var removed = new List<Annotation>();
        foreach (var pageId in pageIds.Distinct())
        {
            var page = model.GetPage(pageId);
            for (int i = page.Annotations.Count - 1; i >= 0; i--)
            {
                operations.Add(new RemoveAnnotationOperation(page.Id, i, page.Annotations[i]));
                removed.Add(page.Annotations[i]);
            }
        }
        AddOrphanedResourceRemovals(model, operations, removed);
        return new(ClearPagesLabel, operations);
    }

    private static List<AnnotationLocation> Locate(DocumentModel model, IEnumerable<Guid> ids) =>
        ids.Distinct().Select(id => model.FindAnnotation(id) ?? throw new KeyNotFoundException("Annotation does not exist.")).ToList();

    private static void AddOrphanedResourceRemovals(DocumentModel model, List<EditOperation> operations, IEnumerable<Annotation> removed)
    {
        var removedIds = removed.Select(a => a.Id).ToHashSet();
        var candidates = removed.OfType<ImageAnnotation>().Select(i => i.ResourceId).Distinct();
        var stillUsed = model.AllAnnotations.Where(a => !removedIds.Contains(a.Id)).OfType<ImageAnnotation>()
            .Select(i => i.ResourceId).ToHashSet();
        foreach (var id in candidates)
            if (!stillUsed.Contains(id) && model.Resources.TryGetValue(id, out var resource))
                operations.Add(new RemoveResourceOperation(resource));
    }
}
