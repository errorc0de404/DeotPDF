using PdfNotes.Core.Models;

namespace PdfNotes.Core.Commands;

/// <summary>Raised when an operation's precondition does not hold. The document is left unchanged.</summary>
public sealed class EditConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// Smallest reversible document change. Operations keep only the data they change (before/after values),
/// never a copy of the whole document. Each operation verifies its precondition so a stale or mismatched
/// command fails instead of corrupting the model.
/// </summary>
public abstract class EditOperation
{
    internal abstract DocumentModel Apply(DocumentModel model);

    public abstract EditOperation Inverse();

    /// <summary>Approximate bytes retained by this operation in history.</summary>
    public abstract long EstimatedBytes { get; }

    private protected static (int Index, Page Page) RequirePage(DocumentModel model, Guid pageId)
    {
        int index = model.IndexOfPage(pageId);
        return index >= 0 ? (index, model.Pages[index]) : throw new EditConflictException("Page does not exist.");
    }

    private protected static DocumentModel WithPage(DocumentModel model, int index, Page page) =>
        model with { Pages = model.Pages.SetItem(index, page) };

    private protected static void RequireNewIds(DocumentModel model, IEnumerable<Guid> ids)
    {
        var incoming = new HashSet<Guid>();
        foreach (var id in ids)
            if (id == Guid.Empty || !incoming.Add(id)) throw new EditConflictException("Inserted ids are empty or duplicated.");
        if (incoming.Contains(model.Id) || model.Resources.Keys.Any(incoming.Contains) ||
            model.Pages.Any(p => incoming.Contains(p.Id) || p.Annotations.Any(a => incoming.Contains(a.Id))))
            throw new EditConflictException("Inserted id already exists in the document.");
    }

    private protected static void RequireValidAnnotation(DocumentModel model, Annotation annotation)
    {
        if (ModelValidator.ValidateAnnotation(annotation, model.Resources.ContainsKey) is { } error)
            throw new EditConflictException(error);
    }
}

public sealed class InsertAnnotationOperation(Guid pageId, int index, Annotation annotation) : EditOperation
{
    public Guid PageId { get; } = pageId;
    public int Index { get; } = index;
    public Annotation Annotation { get; } = annotation;

    internal override DocumentModel Apply(DocumentModel model)
    {
        var (pageIndex, page) = RequirePage(model, PageId);
        if (Index < 0 || Index > page.Annotations.Count) throw new EditConflictException("Annotation index is out of range.");
        RequireNewIds(model, [Annotation.Id]);
        RequireValidAnnotation(model, Annotation);
        return WithPage(model, pageIndex, page with { Annotations = page.Annotations.Insert(Index, Annotation) });
    }

    public override EditOperation Inverse() => new RemoveAnnotationOperation(PageId, Index, Annotation);
    public override long EstimatedBytes => SizeEstimator.Estimate(Annotation);
}

public sealed class RemoveAnnotationOperation(Guid pageId, int index, Annotation annotation) : EditOperation
{
    public Guid PageId { get; } = pageId;
    public int Index { get; } = index;
    public Annotation Annotation { get; } = annotation;

    internal override DocumentModel Apply(DocumentModel model)
    {
        var (pageIndex, page) = RequirePage(model, PageId);
        if (Index < 0 || Index >= page.Annotations.Count || !ReferenceEquals(page.Annotations[Index], Annotation))
            throw new EditConflictException("Annotation to remove is not at the expected position.");
        return WithPage(model, pageIndex, page with { Annotations = page.Annotations.RemoveAt(Index) });
    }

    public override EditOperation Inverse() => new InsertAnnotationOperation(PageId, Index, Annotation);
    public override long EstimatedBytes => SizeEstimator.Estimate(Annotation);
}

public sealed class ReplaceAnnotationOperation : EditOperation
{
    public ReplaceAnnotationOperation(Guid pageId, Annotation before, Annotation after)
    {
        if (before.Id != after.Id) throw new ArgumentException("Replacement must keep the annotation id.");
        PageId = pageId;
        Before = before;
        After = after;
    }

    public Guid PageId { get; }
    public Annotation Before { get; }
    public Annotation After { get; }

    internal override DocumentModel Apply(DocumentModel model)
    {
        var (pageIndex, page) = RequirePage(model, PageId);
        int index = page.IndexOf(Before.Id);
        if (index < 0 || !ReferenceEquals(page.Annotations[index], Before))
            throw new EditConflictException("Annotation was changed by another edit.");
        RequireValidAnnotation(model, After);
        return WithPage(model, pageIndex, page with { Annotations = page.Annotations.SetItem(index, After) });
    }

    public override EditOperation Inverse() => new ReplaceAnnotationOperation(PageId, After, Before);
    public override long EstimatedBytes => SizeEstimator.Estimate(Before) + SizeEstimator.Estimate(After);
}

public sealed class InsertPageOperation(int index, Page page) : EditOperation
{
    public int Index { get; } = index;
    public Page Page { get; } = page;

    internal override DocumentModel Apply(DocumentModel model)
    {
        if (Index < 0 || Index > model.Pages.Count) throw new EditConflictException("Page index is out of range.");
        if (ModelValidator.ValidatePageSetup(Page.Setup) is { } error) throw new EditConflictException(error);
        RequireNewIds(model, Page.Annotations.Select(a => a.Id).Prepend(Page.Id));
        foreach (var annotation in Page.Annotations) RequireValidAnnotation(model, annotation);
        return model with { Pages = model.Pages.Insert(Index, Page) };
    }

    public override EditOperation Inverse() => new RemovePageOperation(Index, Page);
    public override long EstimatedBytes => SizeEstimator.Estimate(Page);
}

public sealed class RemovePageOperation(int index, Page page) : EditOperation
{
    public int Index { get; } = index;
    public Page Page { get; } = page;

    internal override DocumentModel Apply(DocumentModel model)
    {
        if (Index < 0 || Index >= model.Pages.Count || !ReferenceEquals(model.Pages[Index], Page))
            throw new EditConflictException("Page to remove is not at the expected position.");
        return model with { Pages = model.Pages.RemoveAt(Index) };
    }

    public override EditOperation Inverse() => new InsertPageOperation(Index, Page);
    public override long EstimatedBytes => SizeEstimator.Estimate(Page);
}

public sealed class ReplacePageSetupOperation(Guid pageId, PageSetup before, PageSetup after) : EditOperation
{
    public Guid PageId { get; } = pageId;
    public PageSetup Before { get; } = before;
    public PageSetup After { get; } = after;

    internal override DocumentModel Apply(DocumentModel model)
    {
        var (pageIndex, page) = RequirePage(model, PageId);
        if (!ReferenceEquals(page.Setup, Before)) throw new EditConflictException("Page setup was changed by another edit.");
        if (ModelValidator.ValidatePageSetup(After) is { } error) throw new EditConflictException(error);
        return WithPage(model, pageIndex, page with { Setup = After });
    }

    public override EditOperation Inverse() => new ReplacePageSetupOperation(PageId, After, Before);
    public override long EstimatedBytes => 2 * SizeEstimator.PageSetupBytes;
}

public sealed class AddResourceOperation(Resource resource) : EditOperation
{
    public Resource Resource { get; } = resource;

    internal override DocumentModel Apply(DocumentModel model)
    {
        RequireNewIds(model, [Resource.Id]);
        if (ModelValidator.ValidateResource(Resource, verifyHash: false) is { } error) throw new EditConflictException(error);
        return model with { Resources = model.Resources.Add(Resource.Id, Resource) };
    }

    public override EditOperation Inverse() => new RemoveResourceOperation(Resource);
    public override long EstimatedBytes => SizeEstimator.Estimate(Resource);
}

public sealed class RemoveResourceOperation(Resource resource) : EditOperation
{
    public Resource Resource { get; } = resource;

    internal override DocumentModel Apply(DocumentModel model)
    {
        if (!model.Resources.TryGetValue(Resource.Id, out var existing) || !ReferenceEquals(existing, Resource))
            throw new EditConflictException("Resource to remove does not exist.");
        if (model.IsResourceReferenced(Resource.Id)) throw new EditConflictException("Resource is still referenced.");
        return model with { Resources = model.Resources.Remove(Resource.Id) };
    }

    public override EditOperation Inverse() => new AddResourceOperation(Resource);
    public override long EstimatedBytes => SizeEstimator.Estimate(Resource);
}

/// <summary>
/// Sets, replaces or (with a null <see cref="After"/>) removes the tag of an annotation (schema v7). Only the CLI tags;
/// the app's own edits never touch the table.
/// </summary>
public sealed class SetTagOperation(Guid annotationId, AnnotationTag? before, AnnotationTag? after) : EditOperation
{
    public Guid AnnotationId { get; } = annotationId;
    public AnnotationTag? Before { get; } = before;
    public AnnotationTag? After { get; } = after;

    internal override DocumentModel Apply(DocumentModel model)
    {
        if (!Equals(model.Tags.GetValueOrDefault(AnnotationId), Before)) throw new EditConflictException("Tag was changed by another edit.");
        if (After is null) return model with { Tags = model.Tags.Remove(AnnotationId) };
        if (model.FindAnnotation(AnnotationId) is null) throw new EditConflictException("Tagged annotation does not exist.");
        if (After.Validate() is { } error) throw new EditConflictException(error);
        return model with { Tags = model.Tags.SetItem(AnnotationId, After) };
    }

    public override EditOperation Inverse() => new SetTagOperation(AnnotationId, After, Before);
    public override long EstimatedBytes => SizeEstimator.ObjectOverhead + 2 * ((After?.SourceKey?.Length ?? 0) + (After?.GroupKey?.Length ?? 0) + 64);
}

/// <summary>What a history entry touches (spec 13.3, D14g): its pages and annotations, so the UI can show where an Undo or Redo happened.</summary>
public static class EditTargets
{
    public static (IReadOnlyList<Guid> Pages, IReadOnlyList<Guid> Annotations) Of(IEnumerable<EditOperation> operations)
    {
        var pages = new List<Guid>();
        var annotations = new List<Guid>();
        void Add(List<Guid> list, Guid id) { if (!list.Contains(id)) list.Add(id); }
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case InsertAnnotationOperation i: Add(pages, i.PageId); Add(annotations, i.Annotation.Id); break;
                case RemoveAnnotationOperation r: Add(pages, r.PageId); Add(annotations, r.Annotation.Id); break;
                case ReplaceAnnotationOperation r: Add(pages, r.PageId); Add(annotations, r.After.Id); break;
                case InsertPageOperation p: Add(pages, p.Page.Id); break;
                case RemovePageOperation p: Add(pages, p.Page.Id); break;
                case ReplacePageSetupOperation p: Add(pages, p.PageId); break;
            }
        }
        return (pages, annotations);
    }

    /// <summary>True when the step inserts, removes or re-lays out pages (a page command), not only annotations.</summary>
    public static bool ChangesPages(IEnumerable<EditOperation> operations) =>
        operations.Any(o => o is InsertPageOperation or RemovePageOperation or ReplacePageSetupOperation);
}
