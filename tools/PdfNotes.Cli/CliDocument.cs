using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.Cli;

/// <summary>
/// One input PDF opened through the app's own path (<see cref="OpenDocument"/>), with its file hash, page references
/// (cli_plan 3.3) and the PDF text layer of each page (3.4).
/// </summary>
public sealed class CliDocument : IDisposable
{
    private readonly Dictionary<int, TextGlyph[]> glyphs = [];

    private CliDocument(OpenDocument document, string path, string sourceHash, bool derivedPageIds)
    {
        Document = document;
        Path = path;
        SourceHash = sourceHash;
        DerivedPageIds = derivedPageIds;
    }

    public OpenDocument Document { get; }
    public string Path { get; }

    /// <summary><c>sha256:</c> + hex of the input file bytes.</summary>
    public string SourceHash { get; }

    /// <summary>
    /// True when the PDF has no app data: page IDs are then derived from the file hash and page position so that the
    /// same file gives the same IDs on every run (a fresh model would otherwise get new IDs on every open).
    /// </summary>
    public bool DerivedPageIds { get; }

    public DocumentModel Model => Document.Session.Current;

    public static CliDocument Open(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full)) throw new CliException("FILE_NOT_FOUND", $"The file {full} does not exist.", ExitCodes.InvalidInput, new JsonObject { ["path"] = full });
        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch (IOException e) { throw new CliException("FILE_LOCKED", $"The file cannot be read: {e.Message}", ExitCodes.Locked, new JsonObject { ["path"] = full }); }
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        bool derived = false;
        try
        {
            var document = OpenDocument.OpenPdfAsync(full, prepare: (source, model) =>
            {
                if (!source.AppData.FreshModel) return model;
                derived = true;
                return model with { Pages = model.Pages.Select((p, i) => p with { Id = DerivedPageId(hash, i) }).ToImmutableList() };
            }).GetAwaiter().GetResult();
            return new CliDocument(document, full, "sha256:" + hash, derived);
        }
        catch (PdfOpenException e)
        {
            throw new CliException("PDF_OPEN_FAILED", e.Message, ExitCodes.InvalidInput,
                new JsonObject { ["path"] = full, ["reason"] = e.Failure.ToString() });
        }
    }

    /// <summary>Name-based (version 5 layout) UUID of page <paramref name="index"/> of the file with <paramref name="hexHash"/>.</summary>
    public static Guid DerivedPageId(string hexHash, int index)
    {
        // The seed keeps the old exe name: page IDs must not change with the rename to deot.
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"pdfnotes-cli/page/{hexHash}/{index.ToString(CultureInfo.InvariantCulture)}"));
        var bytes = digest.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)(bytes[6] & 0x0F | 0x50);
        bytes[8] = (byte)(bytes[8] & 0x3F | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>1-based position of <paramref name="page"/> in the current document.</summary>
    public int NumberOf(Page page) => Model.IndexOfPage(page.Id) + 1;

    /// <summary>
    /// In <c>apply</c> (cli_plan 3.3): the page IDs of the document before its first operation, so that page numbers keep
    /// meaning those pages while operations add, delete and move pages. Null outside <c>apply</c> (numbers are current).
    /// </summary>
    public IReadOnlyList<Guid>? NumberedPages { get; set; }

    /// <summary>In <c>apply</c>: pages that earlier operations made and named with <c>as</c>, referenced as <c>$name</c>.</summary>
    public Dictionary<string, Guid> PageNames { get; } = new(StringComparer.Ordinal);

    /// <summary>A page by 1-based number, page ID or (in <c>apply</c>) <c>$name</c>.</summary>
    public Page ResolvePage(string reference)
    {
        var model = Model;
        string text = reference.Trim();
        if (text.StartsWith('$'))
        {
            if (PageNames.TryGetValue(text[1..], out var named) && model.IndexOfPage(named) >= 0) return model.GetPage(named);
            throw new CliException("PAGE_NOT_FOUND", PageNames.ContainsKey(text[1..]) ? $"Page {text} was deleted by an earlier operation." : $"No earlier operation named a page {text}.",
                ExitCodes.InvalidInput, new JsonObject { ["page"] = reference, ["names"] = CliJson.Array(PageNames.Keys.Select(k => (JsonNode?)("$" + k))) });
        }
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            if (NumberedPages is { } numbered)
            {
                if (number >= 1 && number <= numbered.Count)
                    return model.IndexOfPage(numbered[number - 1]) is var i and >= 0 ? model.Pages[i]
                        : throw new CliException("PAGE_NOT_FOUND", $"Page {number} of the input was deleted by an earlier operation.", ExitCodes.InvalidInput,
                            new JsonObject { ["page"] = reference, ["pageId"] = numbered[number - 1].ToString() });
                throw new CliException("PAGE_NOT_FOUND", $"Page '{reference}' does not exist (the input has pages 1-{numbered.Count}; name pages made by operations with \"as\").",
                    ExitCodes.InvalidInput, new JsonObject { ["page"] = reference, ["pageCount"] = numbered.Count });
            }
            if (number >= 1 && number <= model.Pages.Count) return model.Pages[number - 1];
        }
        else if (Guid.TryParse(text, out var id) && model.Pages.FirstOrDefault(p => p.Id == id) is { } byId) return byId;
        throw new CliException("PAGE_NOT_FOUND", $"Page '{reference}' does not exist (pages 1-{model.Pages.Count}).", ExitCodes.InvalidInput,
            new JsonObject { ["page"] = reference, ["pageCount"] = model.Pages.Count });
    }

    /// <summary>Pages for a list such as <c>3</c>, <c>3-12</c>, <c>1,4,6-8</c> or page IDs separated by commas, in the order given.</summary>
    public IReadOnlyList<Page> ResolvePages(string spec)
    {
        var pages = new List<Page>();
        foreach (string raw in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = raw.IndexOf('-');
            if (dash > 0 && !Guid.TryParse(raw, out _))
            {
                if (int.TryParse(raw[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out int from) &&
                    int.TryParse(raw[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int to))
                {
                    // Numbers stay numbers (in apply they name pages of the input, whatever their order is now).
                    if (from > to) throw CliException.Invalid($"Page range '{raw}' runs backwards.");
                    for (int n = from; n <= to; n++) pages.Add(ResolvePage(n.ToString(CultureInfo.InvariantCulture)));
                    continue;
                }
                var first = ResolvePage(raw[..dash]);
                var last = ResolvePage(raw[(dash + 1)..]);
                int a = NumberOf(first), b = NumberOf(last);
                if (a > b) throw CliException.Invalid($"Page range '{raw}' runs backwards.");
                for (int n = a; n <= b; n++) pages.Add(Model.Pages[n - 1]);
            }
            else pages.Add(ResolvePage(raw));
        }
        if (pages.Count == 0) throw CliException.Invalid("No pages were given.");
        return pages;
    }

    /// <summary>The pages of <c>-p</c> (one page) or <c>--pages</c> (a list); every page when neither is given.</summary>
    public IReadOnlyList<Page> SelectPages(CliArgs args)
    {
        if (args.Value("--page") is { } one)
        {
            if (args.Has("--pages")) throw CliException.Invalid("Give -p or --pages, not both.");
            return [ResolvePage(one)];
        }
        return args.Value("--pages") is { } list ? ResolvePages(list) : Model.Pages;
    }

    /// <summary>
    /// The PDF text layer of <paramref name="page"/> in canonical points of its current paper (D10 placement applied).
    /// Unlike the app's caret list, glyphs off the paper are kept so that character numbers stay the PDFium indices of
    /// the source page (cli_plan 3.4). Generated pages have none.
    /// </summary>
    public TextGlyph[] Glyphs(Page page)
    {
        var setup = page.Setup;
        if (setup.Source.Kind != PageSourceKind.Pdf || setup.Source.PdfPageIndex is not { } index || index >= Document.Sources.Pages.Count) return [];
        if (!glyphs.TryGetValue(index, out var source))
        {
            try { source = Document.Sources.Glyphs(index); }
            catch (InvalidDataException) { source = []; }
            glyphs[index] = source;
        }
        var placement = setup.Content?.Transform ?? Affine.Identity;
        return placement.IsIdentity ? source : source.Select(g => g with { Box = g.Box.Transform(placement), Direction = Normalize(placement.TransformVector(g.Direction)) }).ToArray();
    }

    private static Point2 Normalize(Point2 v)
    {
        double length = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        return length > 1e-12 ? new(v.X / length, v.Y / length) : new(1, 0);
    }

    public void Dispose() => Document.Dispose();
}
