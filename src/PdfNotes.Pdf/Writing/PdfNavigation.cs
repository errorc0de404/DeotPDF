using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfNotes.Pdf.Writing;

/// <summary>
/// Keeps the outline, links and named destinations of a saved PDF consistent with its pages (spec 24.3–24.4, D13h).
/// After pages were deleted, everything that pointed only at them is removed: outline items without a remaining page
/// (an item whose children remain stays, without its own target), link annotations to a deleted page and named
/// destinations of deleted pages. Otherwise those references would also keep the deleted pages' objects in the file.
/// Targets that are not page references (URIs, remote files) are left as they are.
/// </summary>
internal static class PdfNavigation
{
    public static void Prune(PdfDocument document)
    {
        var pages = new HashSet<PdfDictionary>(document.Pages.Cast<PdfPage>(), ReferenceEqualityComparer.Instance);
        var named = NamedDestinations(document);
        bool Gone(PdfItem? target) => PageOf(target, named) is { } page && !pages.Contains(page);

        if (document.Internals.Catalog.Elements.GetDictionary("/Outlines") is { } outlines)
        {
            int count = PruneChildren(outlines, Gone, 0);
            if (outlines.Elements.ContainsKey("/Count")) outlines.Elements.SetInteger("/Count", count);
        }
        foreach (PdfPage page in document.Pages)
        {
            if (page.Elements.GetArray("/Annots") is not { } annots) continue;
            for (int i = annots.Elements.Count - 1; i >= 0; i--)
                if (Resolve(annots.Elements[i]) is { } annot && annot.Elements.GetName("/Subtype") == "/Link" && Gone(Target(annot)))
                    annots.Elements.RemoveAt(i);
        }
        PruneNames(document, pages);
    }

    /// <summary>
    /// Removes children whose target page is gone and that keep no child, relinks the rest and corrects /Count. Returns how
    /// many items of <paramref name="parent"/>'s subtree are visible when it is open.
    /// </summary>
    private static int PruneChildren(PdfDictionary parent, Func<PdfItem?, bool> gone, int depth)
    {
        var kept = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        int visible = 0;
        for (var item = Resolve(parent.Elements["/First"]); item is not null && depth < 64 && seen.Count < 100_000 && seen.Add(item); item = Resolve(item.Elements["/Next"]))
        {
            int below = PruneChildren(item, gone, depth + 1);
            bool hasChildren = item.Elements.ContainsKey("/First");
            if (gone(Target(item)))
            {
                if (!hasChildren) continue;
                item.Elements.Remove("/Dest"); item.Elements.Remove("/A"); // keep the heading of its remaining children
            }
            if (!hasChildren) item.Elements.Remove("/Count");
            else if (item.Elements.ContainsKey("/Count")) item.Elements.SetInteger("/Count", item.Elements.GetInteger("/Count") < 0 ? -below : below);
            kept.Add(item);
            visible += 1 + (item.Elements.GetInteger("/Count") > 0 ? below : 0);
        }
        if (kept.Count == 0) { parent.Elements.Remove("/First"); parent.Elements.Remove("/Last"); return 0; }
        for (int i = 0; i < kept.Count; i++)
        {
            var item = kept[i];
            if (i == 0) item.Elements.Remove("/Prev"); else item.Elements["/Prev"] = Ref(kept[i - 1]);
            if (i == kept.Count - 1) item.Elements.Remove("/Next"); else item.Elements["/Next"] = Ref(kept[i + 1]);
        }
        parent.Elements["/First"] = Ref(kept[0]);
        parent.Elements["/Last"] = Ref(kept[^1]);
        return visible;
    }

    private static PdfItem Ref(PdfDictionary item) => item.Reference is { } reference ? reference : item;

    /// <summary>A /Dest or a GoTo action's /D of an outline item or a link.</summary>
    private static PdfItem? Target(PdfDictionary item)
    {
        if (item.Elements["/Dest"] is { } dest) return dest;
        if (Resolve(item.Elements["/A"]) is { } action && action.Elements.GetName("/S") == "/GoTo") return action.Elements["/D"];
        return null;
    }

    /// <summary>The page dictionary a destination points to (array, or a named destination resolved through the catalog).</summary>
    private static PdfDictionary? PageOf(PdfItem? target, IReadOnlyDictionary<string, PdfItem> named)
    {
        for (int hops = 0; hops < 4 && target is not null; hops++)
        {
            target = target is PdfReference r ? r.Value : target;
            switch (target)
            {
                case PdfArray { Elements.Count: > 0 } array:
                    return Resolve(array.Elements[0]) is { } page && page.Elements.GetName("/Type") == "/Page" ? page : null;
                case PdfDictionary dictionary when dictionary.Elements.ContainsKey("/D"):
                    target = dictionary.Elements["/D"]; break;
                case PdfString s:
                    target = named.GetValueOrDefault(s.Value); break;
                case PdfName n:
                    target = named.GetValueOrDefault(n.Value.TrimStart('/')); break;
                default:
                    return null;
            }
        }
        return null;
    }

    private static Dictionary<string, PdfItem> NamedDestinations(PdfDocument document)
    {
        var result = new Dictionary<string, PdfItem>(StringComparer.Ordinal);
        if (Resolve(document.Internals.Catalog.Elements["/Dests"]) is { } dests)
            foreach (var key in dests.Elements.Keys) if (dests.Elements[key] is { } value) result[key.TrimStart('/')] = value;
        if (Resolve(document.Internals.Catalog.Elements["/Names"]) is { } names && Resolve(names.Elements["/Dests"]) is { } tree)
            Walk(tree, 0, (name, value) => result.TryAdd(name, value));
        return result;
    }

    private static void PruneNames(PdfDocument document, HashSet<PdfDictionary> pages)
    {
        var empty = new Dictionary<string, PdfItem>();
        bool Gone(PdfItem value) => PageOf(value, empty) is { } page && !pages.Contains(page);
        if (Resolve(document.Internals.Catalog.Elements["/Dests"]) is { } dests)
            foreach (var key in dests.Elements.Keys.ToArray()) if (dests.Elements[key] is { } value && Gone(value)) dests.Elements.Remove(key);
        if (Resolve(document.Internals.Catalog.Elements["/Names"]) is { } names && Resolve(names.Elements["/Dests"]) is { } tree)
            PruneTree(tree, Gone, 0);
    }

    private static void Walk(PdfDictionary node, int depth, Action<string, PdfItem> visit)
    {
        if (depth > 32) return;
        if (node.Elements.GetArray("/Names") is { } pairs)
            for (int i = 0; i + 1 < pairs.Elements.Count; i += 2)
                if (pairs.Elements[i] is PdfString name) visit(name.Value, pairs.Elements[i + 1]);
        if (node.Elements.GetArray("/Kids") is { } kids)
            foreach (var kid in kids.Elements) if (Resolve(kid) is { } child) Walk(child, depth + 1, visit);
    }

    private static void PruneTree(PdfDictionary node, Func<PdfItem, bool> gone, int depth)
    {
        if (depth > 32) return;
        if (node.Elements.GetArray("/Names") is { } pairs)
            for (int i = pairs.Elements.Count - 2; i >= 0; i -= 2)
                if (gone(pairs.Elements[i + 1])) { pairs.Elements.RemoveAt(i + 1); pairs.Elements.RemoveAt(i); }
        if (node.Elements.GetArray("/Kids") is { } kids)
            foreach (var kid in kids.Elements) if (Resolve(kid) is { } child) PruneTree(child, gone, depth + 1);
    }

    private static PdfDictionary? Resolve(PdfItem? item) => item switch
    {
        PdfReference r => r.Value as PdfDictionary,
        PdfDictionary d => d,
        _ => null
    };
}
