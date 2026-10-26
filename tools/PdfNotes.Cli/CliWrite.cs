using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Writing;

namespace PdfNotes.Cli;

/// <summary>
/// Options and safety rules every writing command shares (cli_plan 3.5, 7): <c>-o</c> must differ from the input and
/// needs <c>--overwrite</c> to replace a file; <c>--expect-hash</c> refuses an input that changed; a file a running app
/// window shows is refused (exit 3); <c>--dry-run</c> writes nothing; <c>--preview</c> renders the changed pages;
/// <c>--flatten</c> writes the merged PDF the app exports. Files are written by the app's own save path (write, verify by
/// reading back, replace).
/// </summary>
public sealed class CliWrite
{
    public static readonly string[] ValueOptions = ["--out", "--preview", "--expect-hash", "--batch-id", "--app-folder"];
    public static readonly string[] FlagOptions = ["--overwrite", "--dry-run", "--flatten"];

    private CliWrite(string input, string? output, bool dryRun, string? preview, string? expectHash, string? batchId, bool flatten, string appFolder)
    {
        Input = input;
        Output = output;
        DryRun = dryRun;
        Preview = preview;
        ExpectHash = expectHash;
        BatchId = batchId;
        Flatten = flatten;
        AppFolder = appFolder;
    }

    public string Input { get; }
    public string? Output { get; }
    public bool DryRun { get; }
    public string? Preview { get; }
    public string? ExpectHash { get; }
    public bool Flatten { get; }
    public string AppFolder { get; }

    /// <summary>The run's batch id (cli_plan 6): the given one, or a new one when not given.</summary>
    public string? BatchId { get; }

    /// <summary>
    /// Reads the shared options and checks the paths before anything is opened. <c>apply</c> passes its preview folder and
    /// the <c>expectHash</c> and <c>batchId</c> of its operations file, used when the options are not given (<c>--batch-id</c>
    /// wins over the file; two different hashes are refused).
    /// </summary>
    public static CliWrite Parse(CliArgs args, string input, string? preview = null, string? expectHash = null, string? batchId = null)
    {
        string full = Path.GetFullPath(input);
        bool dryRun = args.Flag("--dry-run");
        string? output = args.Value("--out") is { } o ? Path.GetFullPath(o) : null;
        if (output is null && !dryRun) throw CliException.Invalid("Option -o <output pdf> is required (or --dry-run).");
        if (output is not null)
        {
            if (string.Equals(output, full, StringComparison.OrdinalIgnoreCase))
                throw new CliException("SAME_AS_INPUT", "The output path is the input file; the input is never changed.", ExitCodes.InvalidInput,
                    new JsonObject { ["path"] = output });
            if (!dryRun && File.Exists(output) && !args.Flag("--overwrite"))
                throw new CliException("OUTPUT_EXISTS", $"The file {output} exists; add --overwrite to replace it.", ExitCodes.InvalidInput,
                    new JsonObject { ["path"] = output });
        }
        string? batch = args.Value("--batch-id") ?? batchId;
        string? hash = Either(args, "--expect-hash", expectHash, "expectHash");
        if (batch is not null && new AnnotationTag(AnnotationTag.AiAuthor, batch).Validate() is not null)
            throw CliException.Invalid("--batch-id is empty, too long or has control characters.");
        string appFolder = args.Value("--app-folder") ?? Path.GetDirectoryName(AppSettings.DefaultPath)!;
        var options = new CliWrite(full, output, dryRun, args.Value("--preview") ?? preview, hash,
            batch ?? $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}", args.Flag("--flatten"), appFolder);
        options.RefuseShown(full);
        if (output is not null && !dryRun) options.RefuseShown(output);
        return options;
    }

    private static string? Either(CliArgs args, string option, string? fromFile, string field)
    {
        string? given = args.Value(option);
        if (given is not null && fromFile is not null && given != fromFile)
            throw CliException.Invalid($"{option} and the operations file's \"{field}\" differ.");
        return given ?? fromFile;
    }

    private void RefuseShown(string path)
    {
        if (BackupStore.WindowShowing(AppFolder, path) is { } window)
            throw new CliException("FILE_OPEN_IN_APP", $"A DeotPDF window has {path} open; close it there first.", ExitCodes.Locked,
                new JsonObject { ["path"] = path, ["window"] = window });
    }

    /// <summary>Refuses an input whose bytes are not the ones the caller read before (<c>--expect-hash</c>).</summary>
    public void CheckHash(CliDocument doc)
    {
        if (ExpectHash is null) return;
        string expected = ExpectHash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? ExpectHash : "sha256:" + ExpectHash;
        if (!string.Equals(expected, doc.SourceHash, StringComparison.OrdinalIgnoreCase))
            throw new CliException("HASH_MISMATCH", "The input file changed since its hash was taken.", ExitCodes.Locked,
                new JsonObject { ["expected"] = expected, ["actual"] = doc.SourceHash });
    }

    /// <summary>
    /// Writes the document (unless <see cref="DryRun"/>), renders previews of <paramref name="changedPages"/> and adds
    /// <c>dryRun</c>, <c>output</c>, <c>outputHash</c>, <c>flatten</c>, <c>pageCount</c>, <c>pageIds</c> and <c>previews</c>
    /// to <paramref name="result"/>.
    /// </summary>
    public void Finish(CliDocument doc, JsonObject result, IEnumerable<Guid> changedPages)
    {
        result["dryRun"] = DryRun;
        result["flatten"] = Flatten;
        result["output"] = null;
        result["outputHash"] = null;
        if (!DryRun)
        {
            string output = Output!;
            try
            {
                if (Flatten) doc.Document.ExportFlattenedAsync(output, _ => true).GetAwaiter().GetResult();
                else doc.Document.WriteCopyAsync(output).GetAwaiter().GetResult();
            }
            catch (SaveFailedException e)
            {
                throw new CliException("SAVE_FAILED", $"The output could not be written ({e.Failure}).", e.Failure == SaveFailure.Locked ? ExitCodes.Locked : ExitCodes.Internal,
                    new JsonObject { ["path"] = output, ["reason"] = e.Failure.ToString() });
            }
            result["output"] = output;
            result["outputHash"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(output)));
        }
        var model = doc.Model;
        result["pageCount"] = model.Pages.Count;
        result["pageIds"] = CliJson.Array(model.Pages.Select(p => (JsonNode?)p.Id.ToString()));
        result["previews"] = Preview is null ? null : Previews(doc, changedPages.Distinct().Where(id => model.IndexOfPage(id) >= 0).ToList());
    }

    /// <summary>
    /// <c>--preview</c>: a <c>.png</c> path for one changed page (more pages get <c>-&lt;number&gt;</c> added), otherwise a
    /// folder that gets <c>page-&lt;number&gt;.png</c> per changed page.
    /// </summary>
    private JsonArray Previews(CliDocument doc, IReadOnlyList<Guid> pages)
    {
        var list = new JsonArray();
        bool single = Preview!.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        double ppp = Commands.RenderCommand.DefaultDpi / 72;
        foreach (var id in pages)
        {
            var page = doc.Model.GetPage(id);
            int number = doc.NumberOf(page);
            string path = single
                ? pages.Count == 1 ? Preview : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Preview))!, $"{Path.GetFileNameWithoutExtension(Preview)}-{number}.png")
                : Path.Combine(Preview, $"page-{number}.png");
            path = Path.GetFullPath(path);
            if (string.Equals(path, Input, StringComparison.OrdinalIgnoreCase)) throw new CliException("SAME_AS_INPUT", "A preview path is the input file.");
            var rendered = PageRaster.Render(doc, page, ppp, null, _ => true, out _);
            PageRaster.SavePng(PageRaster.ToBitmap(rendered), path);
            var toPixel = PageRaster.PageToFullPixel(page.Setup, ppp);
            list.Add(new JsonObject
            {
                ["page"] = number, ["pageId"] = id.ToString(), ["path"] = path,
                ["pageToPixel"] = CliJson.Matrix(toPixel), ["pixelToPage"] = CliJson.Matrix(toPixel.Invert()),
            });
        }
        return list;
    }
}
