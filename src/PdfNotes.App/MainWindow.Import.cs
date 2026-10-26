using System.IO;
using System.Windows;
using Microsoft.Win32;
using PdfNotes.App.Services;
using PdfNotes.App.Views;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Reading;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

/// <summary>
/// Pages from other PDFs (spec 11.9, D13g; several files and 파일 › PDF 합치기 D13j): 삽입 ▾ → PDF에서 불러오기…, PDF files
/// dropped on the page list, and a new document merged from several PDFs.
/// </summary>
public partial class MainWindow
{
    private bool importing;

    /// <summary>Replaces the file dialog of the import (validation harness); null cancels.</summary>
    public Func<string?>? ImportFileDialogOverride { get; set; }

    /// <summary>Replaces the file dialog when several files may be chosen (validation harness); null cancels.</summary>
    public Func<string[]?>? ImportFilesDialogOverride { get; set; }

    /// <summary>Replaces showing the range dialog (validation harness): gets the dialog, returns the chosen pages or null.</summary>
    public Func<PdfImportDialog, Task<int[]?>>? ImportRangeOverride { get; set; }

    /// <summary>Replaces showing the merge dialog (validation harness): gets the dialog, returns true for 삽입/만들기.</summary>
    public Func<PdfMergeDialog, Task<bool>>? MergeDialogOverride { get; set; }

    private string[]? ChoosePdfFiles(string title)
    {
        if (ImportFilesDialogOverride is { } many) return many();
        if (ImportFileDialogOverride is { } one) return one() is { } path ? [path] : null;
        var dialog = new OpenFileDialog { Filter = Strings.Get("file.pdfFilter"), Title = title, Multiselect = true };
        return dialog.ShowDialog(this) == true ? dialog.FileNames : null;
    }

    /// <summary>
    /// Chooses PDFs (or takes <paramref name="paths"/>) and inserts pages of them at <paramref name="at"/> (default: after the
    /// active page) as one Undo unit. One file asks its range with thumbnails; several files open the merge dialog (order,
    /// range per file, position). A file that cannot be opened (spec 4.2) or a failure while taking the pages leaves the
    /// document unchanged. Returns true when pages were inserted.
    /// </summary>
    public async Task<bool> ImportPdfPagesAsync(IReadOnlyList<string>? paths = null, int? at = null)
    {
        if (document is not { } target || importing) return false;
        paths ??= ChoosePdfFiles(Strings.Get("import.chooseTitle"));
        if (paths is not { Count: > 0 }) return false;
        importing = true;
        var opened = new List<MergeFile>();
        PageImport[]? prepared = null;
        try
        {
            if (!await OpenForImportAsync(paths, opened) || opened.Count == 0 || !ReferenceEquals(document, target)) return false;
            IReadOnlyList<(MergeFile File, int[] Pages)>? chosen;
            MergePosition position = MergePosition.AfterCurrent;
            if (opened.Count == 1)
            {
                var range = new PdfImportDialog(opened[0].Name, opened[0].Shown) { Owner = this };
                int[]? pages;
                if (ImportRangeOverride is { } pick)
                {
                    try { pages = await pick(range); }
                    finally { range.Abandon(); }
                }
                else pages = range.ShowDialog() == true ? range.Result : null;
                chosen = pages is { Length: > 0 } ? [(opened[0], pages)] : null;
            }
            else
            {
                var merge = new PdfMergeDialog(opened, insert: true, () => AddMergeFilesAsync(opened)) { Owner = this };
                if (at is not null) merge.Position = MergePosition.AfterCurrent;
                bool ok = MergeDialogOverride is { } run ? await run(merge) && merge.Accept() : merge.ShowDialog() == true;
                chosen = ok ? merge.Result : null;
                position = merge.Position;
            }
            if (chosen is not { Count: > 0 } || !ReferenceEquals(document, target)) return false;

            StatusText.Text = Strings.Get("import.loading");
            var files = chosen.Select(c => (c.File.File, c.Pages)).ToArray();
            try { prepared = await Task.Run(() => target.PrepareImports(files)); }
            catch (Exception e) when (IsImportFailure(e))
            {
                StatusText.Text = "";
                ShowMessage(Strings.Get("import.failed"), MessageBoxImage.Warning);
                return false;
            }
            if (!ReferenceEquals(document, target)) return false;
            FinishTextEditing();
            View.CancelTool();
            int count = target.Session.Current.Pages.Count;
            int index = at ?? position switch { MergePosition.Start => 0, MergePosition.End => count, _ => ActivePageIndex + 1 };
            var imports = prepared;
            prepared = null; // owned by the document from here
            Guid[] ids;
            try { ids = target.InsertImported(imports, Math.Clamp(index, 0, count)); }
            catch (InvalidOperationException)
            {
                StatusText.Text = "";
                ShowMessage(Strings.Get("import.failed"), MessageBoxImage.Warning);
                return false;
            }
            SelectPages(ids);
            StatusText.Text = ids.Length == 1 ? Strings.Get("import.doneOne") : Strings.Format("import.done", ("count", ids.Length));
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
    public async Task<bool> MergePdfsAsync(IReadOnlyList<string>? paths = null)
    {
        if (busy || importing) return false;
        paths ??= ChoosePdfFiles(Strings.Get("merge.title"));
        if (paths is not { Count: > 0 }) return false;
        importing = true;
        var opened = new List<MergeFile>();
        PageImport[]? prepared = null;
        try
        {
            if (!await OpenForImportAsync(paths, opened) || opened.Count == 0) return false;
            var merge = new PdfMergeDialog(opened, insert: false, () => AddMergeFilesAsync(opened)) { Owner = this };
            bool ok = MergeDialogOverride is { } run ? await run(merge) && merge.Accept() : merge.ShowDialog() == true;
            if (!ok || merge.Result is not { Count: > 0 } chosen) return false;
            StatusText.Text = Strings.Get("merge.merging");
            var files = chosen.Select(c => (c.File.File, c.Pages)).ToArray();
            try { prepared = await Task.Run(() => OpenDocument.PrepareForNew(files)); }
            catch (Exception e) when (IsImportFailure(e))
            {
                StatusText.Text = "";
                ShowMessage(Strings.Get("merge.failed"), MessageBoxImage.Warning);
                return false;
            }
            StatusText.Text = "";
            importing = false; // closing the current document may ask to save it
            if (!await ConfirmDiscardChangesAsync()) return false;
            var parts = prepared;
            prepared = null;
            OpenDocument merged;
            try { merged = OpenDocument.CreateMerged(parts); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException)
            {
                foreach (var part in parts) part.Pdf?.Dispose();
                ShowMessage(Strings.Get("merge.failed"), MessageBoxImage.Warning);
                return false;
            }
            ShowDocument(merged);
            StatusText.Text = Strings.Format("merge.done", ("files", chosen.Count), ("pages", merged.Session.Current.Pages.Count));
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

    /// <summary>`파일 추가…` of the merge dialog: chooses and opens more files; they are disposed with the others.</summary>
    private async Task<IReadOnlyList<MergeFile>> AddMergeFilesAsync(List<MergeFile> owned)
    {
        if (ChoosePdfFiles(Strings.Get("merge.addFilesTitle")) is not { Length: > 0 } more) return [];
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
            StatusText.Text = Strings.Format("status.openingFile", ("name", name));
            try
            {
                var (file, shown) = await Task.Run(() =>
                {
                    var opened = PdfSourceDocument.Open(path, allowPermissionRestricted: true, allowSigned: true);
                    try
                    {
                        // The dialogs show the file as saved, with its annotations; the pages keep the order of the model.
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
            catch (PdfOpenException e) { failed.Add($"{name}\n\n{OpenFailureMessages.For(e.Failure)}"); }
        }
        StatusText.Text = "";
        if (failed.Count > 0) ShowMessage(string.Join("\n\n", failed), MessageBoxImage.Warning);
        if (signed) ShowMessage(Strings.Get("import.signedNotice"), MessageBoxImage.Information);
        return into.Count > 0;
    }
}
