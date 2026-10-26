using System.Collections.Immutable;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Commands;

/// <summary>What a page copy takes along (spec 11.3): the page with its app annotations, or the page content only.</summary>
public enum PageCopyMode { WithAnnotations, PageOnly }

/// <summary>
/// Page structure edits of the page panel (spec 11.1–11.5): reorder, rotate by quarter turns, add/insert blank pages.
/// Each builder returns one Undo unit and keeps page ids, so Undo restores the very same pages.
/// </summary>
public static class PageEdits
{
    /// <summary>
    /// Moves <paramref name="pageIds"/> together, in their current relative order, so that they end up before the page
    /// that is at <paramref name="insertBefore"/> now (Count = to the end). Empty when the order would not change.
    /// </summary>
    public static EditCommand MovePages(DocumentModel model, IEnumerable<Guid> pageIds, int insertBefore)
    {
        var moving = pageIds.ToHashSet();
        if (moving.Any(id => model.IndexOfPage(id) < 0)) throw new KeyNotFoundException("Page does not exist.");
        if (insertBefore < 0 || insertBefore > model.Pages.Count) throw new ArgumentOutOfRangeException(nameof(insertBefore));
        var kept = model.Pages.Where(p => !moving.Contains(p.Id)).ToList();
        var block = model.Pages.Where(p => moving.Contains(p.Id)).ToList();
        int at = model.Pages.Take(insertBefore).Count(p => !moving.Contains(p.Id));
        var order = kept.Take(at).Concat(block).Concat(kept.Skip(at)).ToList();
        if (order.SequenceEqual(model.Pages, ReferenceEqualityComparer.Instance)) return new("undo.movePages", []);
        var operations = new List<EditOperation>();
        for (int i = model.Pages.Count - 1; i >= 0; i--)
            if (moving.Contains(model.Pages[i].Id)) operations.Add(new RemovePageOperation(i, model.Pages[i]));
        for (int i = 0; i < order.Count; i++)
            if (moving.Contains(order[i].Id)) operations.Add(new InsertPageOperation(i, order[i]));
        return new("undo.movePages", operations);
    }

    /// <summary>Turns pages by ±90° (spec 11.2). Annotations are stored unrotated and turn with their page.</summary>
    public static EditCommand RotatePages(DocumentModel model, IEnumerable<Guid> pageIds, bool clockwise)
    {
        var operations = new List<EditOperation>();
        foreach (var id in pageIds.Distinct())
        {
            var page = model.GetPage(id);
            int rotation = (page.Setup.Rotation + (clockwise ? 90 : 270)) % 360;
            operations.Add(new ReplacePageSetupOperation(id, page.Setup, page.Setup with { Rotation = rotation }));
        }
        return new("undo.rotatePages", operations);
    }

    /// <summary>
    /// Size of a new blank page that matches <paramref name="reference"/> as displayed (spec 11.5): the turned page's
    /// visible width and height, as an unrotated page.
    /// </summary>
    public static (double WidthPt, double HeightPt) DisplayedSize(PageSetup reference) =>
        (reference.Frame.DisplayWidthPt, reference.Frame.DisplayHeightPt);
}

/// <summary>
/// Pages copied in the page panel (spec 11.3–11.4): an immutable snapshot taken at copy time, so a later edit or
/// deletion of the originals does not change what is pasted. Pasting gives every page and annotation a new id; image
/// resources keep their (immutable) bytes and are shared or re-added. Valid only for the document it was taken from
/// (spec 11.4: same session and document).
/// </summary>
public sealed record PageClipboard(Guid DocumentId, PageCopyMode Mode, ImmutableArray<Page> Pages, ImmutableArray<Resource> Resources)
{
    public static PageClipboard Capture(DocumentModel model, IEnumerable<Guid> pageIds, PageCopyMode mode)
    {
        var ids = pageIds.ToHashSet();
        var pages = model.Pages.Where(p => ids.Contains(p.Id))
            .Select(p => mode == PageCopyMode.PageOnly ? p with { Annotations = [] } : p).ToImmutableArray();
        if (pages.Length != ids.Count) throw new KeyNotFoundException("Page does not exist.");
        var resources = pages.SelectMany(p => p.Annotations).OfType<ImageAnnotation>().Select(i => i.ResourceId).Distinct()
            .Select(id => model.Resources[id]).ToImmutableArray();
        return new(model.Id, mode, pages, resources);
    }

    public bool CanPasteInto(DocumentModel model) => model.Id == DocumentId && !Pages.IsEmpty;

    /// <summary>Inserts copies at <paramref name="index"/> in their copied order. Returns the command and the new page ids.</summary>
    public (EditCommand Command, Guid[] PageIds) Paste(DocumentModel model, int index)
    {
        if (!CanPasteInto(model)) throw new InvalidOperationException("Pages can only be pasted into the document they were copied from.");
        if (index < 0 || index > model.Pages.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var operations = new List<EditOperation>();
        foreach (var resource in Resources)
            if (!model.Resources.ContainsKey(resource.Id)) operations.Add(new AddResourceOperation(resource));
        var ids = new List<Guid>();
        for (int i = 0; i < Pages.Length; i++)
        {
            var copy = Pages[i] with
            {
                Id = Guid.NewGuid(),
                Annotations = Pages[i].Annotations.Select(a => a with { Id = Guid.NewGuid() }).ToImmutableList()
            };
            operations.Add(new InsertPageOperation(index + i, copy));
            ids.Add(copy.Id);
        }
        string label = Mode == PageCopyMode.PageOnly ? "undo.pastePagesOnly" : "undo.pastePages";
        return (new(label, operations), [.. ids]);
    }
}
