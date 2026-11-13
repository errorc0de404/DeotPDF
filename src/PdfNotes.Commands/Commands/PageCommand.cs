using System.Text.Json.Nodes;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.Cli.Commands;

/// <summary>
/// <c>page duplicate | insert-blank | delete | move | rotate | shrink | resize | import | export</c> (cli_plan 4.5): the
/// app's own page commands (spec 11) on the input, written to <c>-o</c> (<see cref="CliWrite"/>). Results carry the new
/// page IDs and, for rotate, shrink and resize, one <c>pageTransform</c> per changed page (3.2).
/// </summary>
public static class PageCommand
{
    private static readonly string[] Anchors = ["top-left", "top", "top-right", "left", "center", "right", "bottom-left", "bottom", "bottom-right"];

    public static readonly CliOp Duplicate = Op("page duplicate", "Copies of a page right after it", ["--page", "--count"], ["--with-annotations"], (args, doc, result) =>
    {
        var page = doc.ResolvePage(args.Required("--page"));
        int count = args.Integer("--count", 1, 1, 100);
        var clipboard = PageClipboard.Capture(doc.Model, [page.Id], args.Flag("--with-annotations") ? PageCopyMode.WithAnnotations : PageCopyMode.PageOnly);
        var created = new List<Guid>();
        for (int i = 0; i < count; i++)
        {
            var (command, ids) = clipboard.Paste(doc.Model, doc.Model.IndexOfPage(page.Id) + 1 + i);
            doc.Document.Session.Execute(command);
            created.AddRange(ids);
        }
        result["source"] = Ref(doc, page);
        result["created"] = Refs(doc, created);
        return created;
    });

    public static readonly CliOp InsertBlank = Op("page insert-blank", "Blank pages after a page", ["--after", "--size", "--background", "--count"], [], (args, doc, result) =>
    {
        int index = After(doc, args.Required("--after"));
        var reference = doc.Model.Pages[Math.Max(0, index - 1)].Setup;
        var (w, h) = args.Value("--size") is { } s ? Size(s, "--size") : PageEdits.DisplayedSize(reference);
        var background = args.Value("--background") switch
        {
            null or "blank" => PageBackground.Blank,
            "grid" => PageBackground.Grid,
            "lines" => PageBackground.Lined,
            _ => throw CliException.Invalid("--background takes blank, grid or lines."),
        };
        int count = args.Integer("--count", 1, 1, 100);
        var created = new List<Guid>();
        for (int i = 0; i < count; i++)
        {
            var command = DocumentEdits.InsertPage(doc.Model, index + i, new PageSetup(w, h, 0, PageSource.Generated, background));
            doc.Document.Session.Execute(command);
            created.Add(doc.Model.Pages[index + i].Id);
        }
        result["created"] = Refs(doc, created);
        return created;
    });

    public static readonly CliOp Delete = Op("page delete", "Deletes pages", ["--page", "--pages"], [], (args, doc, result) =>
    {
        var pages = Targets(doc, args);
        if (pages.Count >= doc.Model.Pages.Count)
            throw new CliException("LAST_PAGE", "A document keeps at least one page.", ExitCodes.Unsatisfiable, new JsonObject { ["pageCount"] = doc.Model.Pages.Count });
        result["deleted"] = Refs(doc, pages.Select(p => p.Id));
        doc.Document.Session.Execute(DocumentEdits.DeletePages(doc.Model, pages.Select(p => p.Id)));
        return [];
    });

    public static readonly CliOp Move = Op("page move", "Moves pages to a position", ["--page", "--pages", "--to"], [], (args, doc, result) =>
    {
        var pages = Targets(doc, args);
        var moving = pages.Select(p => p.Id).ToHashSet();
        var kept = doc.Model.Pages.Where(p => !moving.Contains(p.Id)).ToList();
        int to = args.Integer("--to", 0, 1, kept.Count + 1);
        if (!args.Has("--to")) throw CliException.Invalid("Option --to <position> is required: where the first moved page ends up (1-based).");
        int insertBefore = to - 1 < kept.Count ? doc.Model.IndexOfPage(kept[to - 1].Id) : doc.Model.Pages.Count;
        doc.Document.Session.Execute(PageEdits.MovePages(doc.Model, moving, insertBefore));
        result["moved"] = Refs(doc, doc.Model.Pages.Where(p => moving.Contains(p.Id)).Select(p => p.Id));
        return [.. moving];
    });

    public static readonly CliOp Rotate = Op("page rotate", "Turns pages by quarter turns", ["--page", "--pages", "--deg"], [], (args, doc, result) =>
    {
        var pages = Targets(doc, args);
        int turns = args.Required("--deg") switch
        {
            "90" => 1,
            "180" => 2,
            "270" or "-90" => 3,
            _ => throw CliException.Invalid("--deg takes 90, 180 or 270 (clockwise)."),
        };
        var before = pages.ToDictionary(p => p.Id, p => p.Setup);
        for (int i = 0; i < turns; i++) doc.Document.Session.Execute(PageEdits.RotatePages(doc.Model, before.Keys, clockwise: true));
        result["pageTransforms"] = CliJson.Array(pages.Select(p => (JsonNode?)CliJson.PageTransform(p.Id, before[p.Id], doc.Model.GetPage(p.Id).Setup, Affine.Identity)));
        return before.Keys;
    });

    public static readonly CliOp Shrink = Op("page shrink", "Shrinks page content with its annotations", ["--page", "--pages", "--scale", "--anchor"], [], (args, doc, result) =>
    {
        var pages = Targets(doc, args);
        double scale = args.Number("--scale", double.NaN, 0.01, 1);
        if (!args.Has("--scale")) throw CliException.Invalid("Option --scale is required (0.01–1, e.g. 0.8 for 80%).");
        var before = pages.ToDictionary(p => p.Id, p => p.Setup);
        var (command, changes) = PageTransforms.ShrinkWithChanges(doc.Model, before.Keys, scale * 100, Anchor(args));
        doc.Document.Session.Execute(command);
        result["pageTransforms"] = Transforms(doc, pages, before, changes);
        return changes.Keys;
    });

    public static readonly CliOp Resize = Op("page resize", "Changes the page size, fitting or keeping the content", ["--page", "--pages", "--size", "--mode", "--anchor"], [], (args, doc, result) =>
    {
        var pages = Targets(doc, args);
        var (w, h) = Size(args.Required("--size"), "--size");
        var mode = args.Value("--mode") switch
        {
            null or "fit" => PageResizeMode.FitContent,
            "clip" => PageResizeMode.ClipContent,
            _ => throw CliException.Invalid("--mode takes fit (shrink the content to fit) or clip (keep its size)."),
        };
        var before = pages.ToDictionary(p => p.Id, p => p.Setup);
        var (command, changes) = PageTransforms.ResizeWithChanges(doc.Model, before.Keys, Units.PointsToMillimeters(w), Units.PointsToMillimeters(h), mode, Anchor(args));
        doc.Document.Session.Execute(command);
        result["pageTransforms"] = Transforms(doc, pages, before, changes);
        return changes.Keys;
    });

    public static readonly CliOp Import = Op("page import", "Inserts pages of another PDF", ["--from", "--range", "--after"], [], (args, doc, result) =>
    {
        string from = CliPaths.Full(args.Required("--from"));
        if (!System.IO.File.Exists(from)) throw new CliException("FILE_NOT_FOUND", $"The file {from} does not exist.", ExitCodes.InvalidInput, new JsonObject { ["path"] = from });
        int index = After(doc, args.Required("--after"));
        PdfSourceDocument file;
        try { file = PdfSourceDocument.Open(from, allowPermissionRestricted: true, allowSigned: true); }
        catch (PdfOpenException e) { throw new CliException("PDF_OPEN_FAILED", e.Message, ExitCodes.InvalidInput, new JsonObject { ["path"] = from, ["reason"] = e.Failure.ToString() }); }
        using (file)
        {
            int count = file.CreateModel().Pages.Count;
            string range = args.Value("--range") ?? "all";
            int[] indices;
            if (range == "all") indices = [.. Enumerable.Range(0, count)];
            else if (PageRanges.TryParse(range, count, out indices) is { } error)
                throw CliException.Invalid($"--range '{range}' is not usable ({error}); use all or numbers and ranges like 1-3,5 (file pages 1-{count}).");
            var prepared = doc.Document.PrepareImport(file, indices);
            Guid[] ids;
            try { ids = doc.Document.InsertImported(prepared, index); }
            catch { prepared.Pdf?.Dispose(); throw; }
            result["from"] = from;
            result["created"] = Refs(doc, ids);
            return ids;
        }
    });

    /// <summary>
    /// <c>page export &lt;pdf&gt; --pages &lt;list&gt; (or --range) -o &lt;pdf&gt; [--flatten]</c>: only those pages as a PDF of their own
    /// (spec 11.10), editable with their annotations or flattened. The input document is not changed.
    /// </summary>
    public static JsonObject Export(IReadOnlyList<string> tokens)
    {
        var args = CliArgs.Parse(tokens, [.. CliWrite.ValueOptions, "--pages", "--page", "--range"], CliWrite.FlagOptions);
        string input = args.InputPath();
        var write = CliWrite.Parse(args, input);
        if (write.Preview is not null) throw CliException.Invalid("--preview is not available for page export.");
        using var doc = CliDocument.Open(input);
        write.CheckHash(doc);
        if (args.Has("--range") && (args.Has("--page") || args.Has("--pages"))) throw CliException.Invalid("Give --pages (or --range), not both.");
        var pages = args.Value("--range") is { } range ? doc.ResolvePages(range) : Targets(doc, args);
        var result = CliJson.Result("page export", doc.SourceHash);
        result["exported"] = Refs(doc, pages.Select(p => p.Id));
        result["dryRun"] = write.DryRun;
        result["flatten"] = write.Flatten;
        result["output"] = null;
        result["outputHash"] = null;
        if (!write.DryRun)
        {
            doc.Document.ExportPagesAsync(write.Output!, [.. pages.Select(p => p.Id)], write.Flatten, _ => true).GetAwaiter().GetResult();
            result["output"] = write.Output;
            result["outputHash"] = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(write.Output!)));
        }
        result["pageCount"] = pages.Count;
        return result;
    }

    public static readonly CliOp[] Ops = [Duplicate, InsertBlank, Delete, Move, Rotate, Shrink, Resize, Import];

    private static CliOp Op(string name, string summary, string[] values, string[] flags, Func<CliArgs, CliDocument, JsonObject, IEnumerable<Guid>> edit) =>
        new(name, summary, values, flags, (args, context, result) => edit(args, context.Doc, result));

    private static IReadOnlyList<Page> Targets(CliDocument doc, CliArgs args)
    {
        if (!args.Has("--page") && !args.Has("--pages")) throw CliException.Invalid("Give the pages with -p <page> or --pages <list>.");
        var pages = doc.SelectPages(args);
        if (pages.Select(p => p.Id).Distinct().Count() != pages.Count) throw CliException.Invalid("A page is given twice.");
        return pages;
    }

    /// <summary><c>--after</c>: a page (the new pages follow it) or 0 for the start; the insert index.</summary>
    private static int After(CliDocument doc, string value) => value.Trim() == "0" ? 0 : doc.NumberOf(doc.ResolvePage(value));

    private static int Anchor(CliArgs args)
    {
        string anchor = args.Value("--anchor") ?? "center";
        int index = Array.IndexOf(Anchors, anchor);
        return index >= 0 ? index : throw CliException.Invalid($"--anchor takes {string.Join(", ", Anchors)}.");
    }

    /// <summary><c>w,h</c> in points within the app's page size limits (10–2000 mm).</summary>
    private static (double W, double H) Size(string text, string option)
    {
        var n = CliArgs.ParseNumbers(text, 2, option);
        double min = Units.MillimetersToPoints(InputLimits.MinNewPageSideMm), max = Units.MillimetersToPoints(InputLimits.MaxNewPageSideMm);
        if (n.Any(v => v < min - 1e-9 || v > max + 1e-9))
            throw CliException.Invalid($"{option} sides must be {min:0.##}–{max:0.##} pt (10–2000 mm).");
        return (n[0], n[1]);
    }

    private static JsonArray Transforms(CliDocument doc, IReadOnlyList<Page> pages, Dictionary<Guid, PageSetup> before, IReadOnlyDictionary<Guid, Affine> changes) =>
        CliJson.Array(pages.Select(p => (JsonNode?)CliJson.PageTransform(p.Id, before[p.Id], doc.Model.GetPage(p.Id).Setup,
            changes.TryGetValue(p.Id, out var m) ? m : Affine.Identity)));

    private static JsonObject Ref(CliDocument doc, Page page) => new() { ["page"] = doc.NumberOf(page), ["pageId"] = page.Id.ToString() };

    private static JsonArray Refs(CliDocument doc, IEnumerable<Guid> ids) =>
        CliJson.Array(ids.Select(id => (JsonNode?)(doc.Model.IndexOfPage(id) is var i and >= 0
            ? new JsonObject { ["page"] = i + 1, ["pageId"] = id.ToString() }
            : new JsonObject { ["page"] = null, ["pageId"] = id.ToString() })));
}
