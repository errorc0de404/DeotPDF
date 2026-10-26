using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PdfNotes.Core.Models;

namespace PdfNotes.Core.Commands;

/// <summary>Why a page range text cannot be used (spec 11.9). The UI maps each to a short message.</summary>
public enum PageRangeError { Empty, Syntax, OutOfRange }

/// <summary>
/// Page range text of the import dialog (spec 11.9): <c>전체</c>, or page numbers and ranges separated by commas such as
/// <c>1-3, 5</c>. Pages are always taken in the file's order, each once.
/// </summary>
public static class PageRanges
{
    /// <summary>The word for every page; the editor sets it from the language pack (D14e).</summary>
    public static string All { get; set; } = "전체"; // Korean default; the language pack sets it (D14e)

    /// <summary>Zero-based page indices in ascending order, or the reason the text is not usable.</summary>
    public static PageRangeError? TryParse(string? text, int pageCount, out int[] indices)
    {
        indices = [];
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return PageRangeError.Empty;
        if (trimmed == All) { indices = [.. Enumerable.Range(0, pageCount)]; return pageCount > 0 ? null : PageRangeError.Empty; }
        var chosen = new SortedSet<int>();
        foreach (var raw in trimmed.Split(',', '，'))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            var ends = part.Split('-', '~', '–');
            if (ends.Length > 2 || !Number(ends[0], out int first)) return PageRangeError.Syntax;
            int last = first;
            if (ends.Length == 2 && !Number(ends[1], out last)) return PageRangeError.Syntax;
            if (last < first) return PageRangeError.Syntax;
            if (first < 1 || last > pageCount) return PageRangeError.OutOfRange;
            for (int page = first; page <= last; page++) chosen.Add(page - 1);
        }
        if (chosen.Count == 0) return PageRangeError.Empty;
        indices = [.. chosen];
        return null;
    }

    /// <summary>
    /// Several files at once (page export, spec 11.10): groups separated by <c>;</c>, each a range text as above, e.g.
    /// <c>1,3-10; 2,11-20; 21-30</c> is three files. Empty groups (a trailing <c>;</c>) are ignored. On an error
    /// <paramref name="badGroup"/> is the zero-based group it is in.
    /// </summary>
    public static PageRangeError? TryParseGroups(string? text, int pageCount, out int[][] groups, out int badGroup)
    {
        groups = [];
        badGroup = -1;
        var parsed = new List<int[]>();
        var parts = (text ?? "").Split(';', '；');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Trim().Length == 0) continue;
            if (TryParse(parts[i], pageCount, out var indices) is { } error) { badGroup = parsed.Count; return error; }
            parsed.Add(indices);
        }
        if (parsed.Count == 0) return PageRangeError.Empty;
        groups = [.. parsed];
        return null;
    }

    /// <summary>The shortest text for <paramref name="indices"/>: <c>전체</c> when every page is chosen, otherwise <c>1-3, 5</c>.</summary>
    public static string Format(IEnumerable<int> indices, int pageCount)
    {
        var sorted = indices.Distinct().Order().ToArray();
        if (sorted.Length == 0) return "";
        if (pageCount > 0 && sorted.Length == pageCount && sorted[0] == 0 && sorted[^1] == pageCount - 1) return All;
        var text = new StringBuilder();
        for (int i = 0; i < sorted.Length;)
        {
            int j = i;
            while (j + 1 < sorted.Length && sorted[j + 1] == sorted[j] + 1) j++;
            if (text.Length > 0) text.Append(", ");
            text.Append(sorted[i] + 1);
            if (j > i) text.Append('-').Append(sorted[j] + 1);
            i = j + 1;
        }
        return text.ToString();
    }

    private static bool Number(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

public static class PageImports
{
    public const string Label = "undo.importPdf";

    /// <summary>
    /// Inserts pages taken from another PDF at <paramref name="index"/>, in their order, as one Undo unit (spec 11.9).
    /// The pages must already have new IDs and source indices in this document. An image resource whose ID is already used
    /// by different bytes gets a new ID. Returns the command and the inserted page ids.
    /// </summary>
    public static (EditCommand Command, Guid[] PageIds) Insert(DocumentModel model, int index, IReadOnlyList<Page> pages, IEnumerable<Resource> resources) =>
        Insert(model, index, [(pages, resources)]);

    /// <summary>
    /// Pages of several files at once (spec 11.9 여러 PDF, D13j): the parts one after another from <paramref name="index"/>,
    /// as one Undo unit. Image resources are matched across the parts too: the same ID with other bytes gets a new ID.
    /// </summary>
    public static (EditCommand Command, Guid[] PageIds) Insert(DocumentModel model, int index, IReadOnlyList<(IReadOnlyList<Page> Pages, IEnumerable<Resource> Resources)> parts)
    {
        if (index < 0 || index > model.Pages.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (parts.Sum(p => p.Pages.Count) == 0) throw new ArgumentException("No page to insert.", nameof(parts));
        var operations = new List<EditOperation>();
        var known = model.Resources.ToDictionary(r => r.Key, r => r.Value.Sha256);
        var ids = new List<Guid>();
        int at = index;
        foreach (var (pages, resources) in parts)
        {
            var renamed = new Dictionary<Guid, Guid>();
            foreach (var resource in resources)
            {
                if (known.TryGetValue(resource.Id, out var sha))
                {
                    if (sha == resource.Sha256) continue;
                    var copy = resource with { Id = Guid.NewGuid() };
                    renamed[resource.Id] = copy.Id;
                    known[copy.Id] = copy.Sha256;
                    operations.Add(new AddResourceOperation(copy));
                }
                else
                {
                    known[resource.Id] = resource.Sha256;
                    operations.Add(new AddResourceOperation(resource));
                }
            }
            foreach (var original in pages)
            {
                var page = renamed.Count == 0 ? original : original with
                {
                    Annotations = original.Annotations.Select(a => a is ImageAnnotation image && renamed.TryGetValue(image.ResourceId, out var id)
                        ? image with { ResourceId = id } : a).ToImmutableList()
                };
                operations.Add(new InsertPageOperation(at++, page));
                ids.Add(page.Id);
            }
        }
        return (new(Label, operations), [.. ids]);
    }
}
