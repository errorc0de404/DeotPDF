using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.ViewModels;

/// <summary>One PDF of a merge: the opened file (checked like opening), its renderer for the range picker, and the range text.</summary>
public sealed class MergeFile(string name, PdfSourceDocument file, PdfiumDocument shown)
{
    public string Name { get; } = name;
    public PdfSourceDocument File { get; } = file;
    public PdfiumDocument Shown { get; } = shown;
    public int PageCount => Shown.PageCount;
    public string Range { get; set; } = PageRanges.All;
}

/// <summary>Where the pages of several PDFs go (spec 11.9 여러 PDF, D13j).</summary>
public enum MergePosition { AfterCurrent, Start, End }

/// <summary>What the merge window chose: the files in order with their pages, and for an insert where they go.</summary>
public sealed record MergeChoice(IReadOnlyList<(MergeFile File, int[] Pages)> Files, MergePosition Position);

/// <summary>What the export window chose: page groups (one file each) and whether the notes are merged (Flatten).</summary>
public sealed record PageExportChoice(int[][] Groups, bool Flatten);

/// <summary>
/// The shell's page windows (P1 V6, the extension of <see cref="IEditorDialogs"/>): choosing PDF files, a page range with
/// thumbnails, the merge window and the export window. Choosing or cancelling changes nothing; the caller owns the files.
/// </summary>
public interface IPageDialogs
{
    /// <summary>PDF files to read, or null when cancelled.</summary>
    Task<string[]?> ChoosePdfFilesAsync(string title);

    /// <summary>The pages of one file to insert (zero-based), or null when cancelled.</summary>
    Task<int[]?> ChooseImportPagesAsync(MergeFile file);

    /// <summary>The merge window (<paramref name="insert"/>: into this document, else a new one); <paramref name="addFiles"/> is its `파일 추가…`.</summary>
    Task<MergeChoice?> ChooseMergeAsync(IReadOnlyList<MergeFile> files, bool insert, Func<Task<IReadOnlyList<MergeFile>>> addFiles);

    /// <summary>The export window, starting at <paramref name="range"/>; <paramref name="flatten"/> preselects 필기 합친 PDF.</summary>
    Task<PageExportChoice?> ChooseExportAsync(int pageCount, string range, bool flatten);

    /// <summary>The blank page window (spec 11.5): size and background, starting at <paramref name="reference"/>'s; null when cancelled.</summary>
    Task<NewPageSpec?> ChooseBlankPageAsync(bool insert, PageSetup reference);

    /// <summary>
    /// The resize (<paramref name="resize"/>) or shrink window (spec 11.6–11.8) with a preview of <paramref name="active"/>;
    /// <paramref name="targets"/> gives the pages of each scope. Null when cancelled.
    /// </summary>
    Task<PageTransformRequest?> ChooseTransformAsync(bool resize, DocumentModel model, Page active, Func<PageApplyScope, PageSetup[]> targets);
}

/// <summary>
/// Pages in and out of PDF files (P1 V6): pages of other PDFs (spec 11.9, D13g; several files D13j), 파일 › PDF 합치기 (a new
/// document) and 내보내기 (11.10, 14.5, D14g). The windows are the shell's (<see cref="IPageDialogs"/>).
/// </summary>
public sealed class ImportExportViewModel(MainViewModel owner, IEditorDialogs dialogs, IPageDialogs pageDialogs) : ObservableObject
{
    private Texts texts => owner.Texts;

    private bool importing;

    /// <summary>An import or a merge is in progress (another one is refused).</summary>
    public bool Importing => importing;

    private OpenDocument? Document => owner.Document;

    /// <summary>
    /// Chooses PDFs (or takes <paramref name="paths"/>) and inserts pages of them at <paramref name="at"/> (default: after the
    /// active page) as one Undo unit. One file asks its range with thumbnails; several files open the merge window (order,
    /// range per file, position). A file that cannot be opened (spec 4.2) or a failure while taking the pages leaves the
    /// document unchanged. Returns true when pages were inserted.
    /// </summary>
    public async Task<bool> ImportPagesAsync(IReadOnlyList<string>? paths = null, int? at = null)
    {
        if (Document is not { } target || importing) return false;
        paths ??= await pageDialogs.ChoosePdfFilesAsync(texts.Get("import.chooseTitle"));
        if (paths is not { Count: > 0 }) return false;
        importing = true;
        var opened = new List<MergeFile>();
        PageImport[]? prepared = null;
        try
        {
            if (!await OpenForImportAsync(paths, opened) || opened.Count == 0 || !ReferenceEquals(Document, target)) return false;
            IReadOnlyList<(MergeFile File, int[] Pages)>? chosen;
            MergePosition position = MergePosition.AfterCurrent;
            if (opened.Count == 1)
            {
                var pages = await pageDialogs.ChooseImportPagesAsync(opened[0]);
                chosen = pages is { Length: > 0 } ? [(opened[0], pages)] : null;
            }
            else
            {
                var merge = await pageDialogs.ChooseMergeAsync(opened, insert: true, () => AddMergeFilesAsync(opened));
                chosen = merge?.Files;
                if (merge is not null) position = merge.Position;
            }
            if (chosen is not { Count: > 0 } || !ReferenceEquals(Document, target)) return false;
            owner.Status = texts.Get("import.loading");
            var files = chosen.Select(c => (c.File.File, c.Pages)).ToArray();
            try { prepared = await Task.Run(() => target.PrepareImports(files)); }
            catch (Exception e) when (IsImportFailure(e))
            {
                owner.Status = "";
                dialogs.Tell(texts.Get("import.failed"), NoticeKind.Warning);
                return false;
            }
            if (!ReferenceEquals(Document, target)) return false;
            owner.Editing.FinishTextEditing();
            owner.Editing.CancelTool();
            int count = target.Session.Current.Pages.Count;
            int index = at ?? position switch { MergePosition.Start => 0, MergePosition.End => count, _ => owner.Pages.ActivePageIndex + 1 };
            var imports = prepared;
            prepared = null; // owned by the document from here
            Guid[] ids;
            try { ids = target.InsertImported(imports, Math.Clamp(index, 0, count)); }
            catch (InvalidOperationException)
            {
                owner.Status = "";
                dialogs.Tell(texts.Get("import.failed"), NoticeKind.Warning);
                return false;
            }
            owner.Pages.SelectPages(ids);
            owner.Status = ids.Length == 1 ? texts.Get("import.doneOne") : texts.Format("import.done", ("count", ids.Length));
            return true;
        }
        finally
        {
            importing = false;
            if (prepared is not null) foreach (var part in prepared) part.Pdf?.Dispose();
            foreach (var file in opened) { file.Shown.Dispose(); file.File.Dispose(); }
        }
    }

    /// <summary>
    /// 파일 › PDF 합치기 (D13j): several PDFs in a chosen order and ranges become a new unsaved document (the current one
    /// is closed like 새 노트, asking to save first). Returns true when the new document is shown.
    /// </summary>
    public async Task<bool> MergeAsync(IReadOnlyList<string>? paths = null)
    {
        if (owner.IsBusy || importing) return false;
        paths ??= await pageDialogs.ChoosePdfFilesAsync(texts.Get("merge.title"));
        if (paths is not { Count: > 0 }) return false;
        importing = true;
        var opened = new List<MergeFile>();
        PageImport[]? prepared = null;
        try
        {
            if (!await OpenForImportAsync(paths, opened) || opened.Count == 0) return false;
            var merge = await pageDialogs.ChooseMergeAsync(opened, insert: false, () => AddMergeFilesAsync(opened));
            if (merge?.Files is not { Count: > 0 } chosen) return false;
            owner.Status = texts.Get("merge.merging");
            var files = chosen.Select(c => (c.File.File, c.Pages)).ToArray();
            try { prepared = await Task.Run(() => OpenDocument.PrepareForNew(files)); }
            catch (Exception e) when (IsImportFailure(e))
            {
                owner.Status = "";
                dialogs.Tell(texts.Get("merge.failed"), NoticeKind.Warning);
                return false;
            }
            owner.Status = "";
            importing = false; // closing the current document may ask to save it
            if (!await owner.Workflow.ConfirmDiscardChangesAsync()) return false;
            var parts = prepared;
            prepared = null;
            OpenDocument merged;
            try { merged = OpenDocument.CreateMerged(parts); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException)
            {
                foreach (var part in parts) part.Pdf?.Dispose();
                dialogs.Tell(texts.Get("merge.failed"), NoticeKind.Warning);
                return false;
            }
            owner.Workflow.Replace(merged);
            owner.Status = texts.Format("merge.done", ("files", chosen.Count), ("pages", merged.Session.Current.Pages.Count));
            return true;
        }
        finally
        {
            importing = false;
            if (prepared is not null) foreach (var part in prepared) part.Pdf?.Dispose();
            foreach (var file in opened) { file.Shown.Dispose(); file.File.Dispose(); }
        }
    }

    private static bool IsImportFailure(Exception e) =>
        e is InvalidDataException or NotSupportedException or InvalidOperationException or ArgumentException or PdfiumOpenException or IOException;

    /// <summary>`파일 추가…` of the merge window: chooses and opens more files; they are disposed with the others.</summary>
    private async Task<IReadOnlyList<MergeFile>> AddMergeFilesAsync(List<MergeFile> owned)
    {
        if (await pageDialogs.ChoosePdfFilesAsync(texts.Get("merge.addFilesTitle")) is not { Length: > 0 } more) return [];
        var added = new List<MergeFile>();
        await OpenForImportAsync(more, added);
        owned.AddRange(added);
        return added;
    }

    /// <summary>
    /// Opens each file off the UI thread with the same checks as opening a document (and a renderer for its thumbnails).
    /// Permission-restricted and signed PDFs may give pages (spec 11.9, D14g); a signature does not carry over, which is
    /// told once. Other apps' annotations come in read-only (14.6: the document command converts them). A file that cannot
    /// be used is reported and left out; the others stay. Returns false when none could be opened.
    /// </summary>
    private async Task<bool> OpenForImportAsync(IEnumerable<string> paths, List<MergeFile> into)
    {
        var failed = new List<string>();
        bool signed = false;
        foreach (var path in paths)
        {
            string name = Path.GetFileName(path);
            owner.Status = texts.Format("status.openingFile", ("name", name));
            try
            {
                var (file, shown) = await Task.Run(() =>
                {
                    var opened = PdfSourceDocument.Open(path, allowPermissionRestricted: true, allowSigned: true);
                    try
                    {
                        // The windows show the file as saved, with its annotations; the pages keep the order of the model.
                        var engine = new PdfiumDocument(File.ReadAllBytes(opened.Path));
                        if (engine.PageCount != opened.CreateModel().Pages.Count) { engine.Dispose(); throw new PdfOpenException(PdfOpenFailure.Unsupported, "Page count differs."); }
                        return (opened, engine);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfiumOpenException)
                    {
                        opened.Dispose();
                        throw new PdfOpenException(PdfOpenFailure.AccessDenied, "File cannot be read.", e);
                    }
                    catch { opened.Dispose(); throw; }
                });
                into.Add(new MergeFile(name, file, shown));
                signed |= file.Signed;
            }
            catch (PdfOpenException e) { failed.Add($"{name}\n\n{OpenFailureMessages.For(e.Failure, texts)}"); }
        }
        owner.Status = "";
        if (failed.Count > 0) dialogs.Tell(string.Join("\n\n", failed), NoticeKind.Warning);
        if (signed) dialogs.Tell(texts.Get("import.signedNotice"), NoticeKind.Information);
        return into.Count > 0;
    }

    /// <summary>
    /// 내보내기, one window (spec 11.10, 14.5, D14g): pages of the current state (several files split by <c>;</c>) become
    /// PDFs of their own, editable or with the notes merged (Flatten). From the 페이지 tab and the page list the range
    /// starts as the selected pages, from the 파일 tab as every page; <paramref name="flatten"/> preselects 필기 합친 PDF
    /// with every page (the former Flatten command). The document, its path and its unsaved mark do not change.
    /// Returns the number of files written.
    /// </summary>
    public async Task<int> ExportPagesAsync(bool allPages = false, bool flatten = false)
    {
        if (owner.IsBusy || Document is not { } doc) return 0;
        owner.Editing.FinishTextEditing();
        var model = doc.Session.Current;
        var selected = owner.Pages.SelectedPageIds.Select(model.IndexOfPage).Where(i => i >= 0);
        var choice = await pageDialogs.ChooseExportAsync(model.Pages.Count, allPages || flatten ? PageRanges.All : PageRanges.Format(selected, model.Pages.Count), flatten);
        if (choice?.Groups is not { Length: > 0 } groups) return 0;
        return await owner.Workflow.ExportPagesAsync(groups, choice.Flatten);
    }
}
