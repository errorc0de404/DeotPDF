using Microsoft.Win32;
using PdfNotes.App.Views;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

/// <summary>
/// Pages from other PDFs (spec 11.9, D13g; several files and 파일 › PDF 합치기 D13j): 삽입 ▾ → PDF에서 불러오기…, PDF files
/// dropped on the page list, and a new document merged from several PDFs. The flow is <see cref="ImportExportViewModel"/>'s
/// (P1 V6); the windows are here (<see cref="IPageDialogs"/>).
/// </summary>
public partial class MainWindow : IPageDialogs
{
    /// <summary>Replaces the file dialog of the import (validation harness); null cancels.</summary>
    public Func<string?>? ImportFileDialogOverride { get; set; }

    /// <summary>Replaces the file dialog when several files may be chosen (validation harness); null cancels.</summary>
    public Func<string[]?>? ImportFilesDialogOverride { get; set; }

    /// <summary>Replaces showing the range dialog (validation harness): gets the dialog, returns the chosen pages or null.</summary>
    public Func<PdfImportDialog, Task<int[]?>>? ImportRangeOverride { get; set; }

    /// <summary>Replaces showing the merge dialog (validation harness): gets the dialog, returns true for 삽입/만들기.</summary>
    public Func<PdfMergeDialog, Task<bool>>? MergeDialogOverride { get; set; }

    /// <summary>
    /// Chooses PDFs (or takes <paramref name="paths"/>) and inserts pages of them at <paramref name="at"/> (default: after the
    /// active page) as one Undo unit (<see cref="ImportExportViewModel.ImportPagesAsync"/>). Returns true when pages were inserted.
    /// </summary>
    public Task<bool> ImportPdfPagesAsync(IReadOnlyList<string>? paths = null, int? at = null) => viewModel.ImportExport.ImportPagesAsync(paths, at);

    /// <summary>파일 › PDF 합치기 (D13j): a new unsaved document from several PDFs. Returns true when it is shown.</summary>
    public Task<bool> MergePdfsAsync(IReadOnlyList<string>? paths = null) => viewModel.ImportExport.MergeAsync(paths);

    // ----- the page windows (IPageDialogs) -----

    string[]? IPageDialogs.ChoosePdfFiles(string title)
    {
        if (ImportFilesDialogOverride is { } many) return many();
        if (ImportFileDialogOverride is { } one) return one() is { } path ? [path] : null;
        var dialog = new OpenFileDialog { Filter = Strings.Get("file.pdfFilter"), Title = title, Multiselect = true };
        return dialog.ShowDialog(this) == true ? dialog.FileNames : null;
    }

    async Task<int[]?> IPageDialogs.ChooseImportPagesAsync(MergeFile file)
    {
        var range = new PdfImportDialog(file.Name, file.Shown) { Owner = this };
        if (ImportRangeOverride is { } pick)
        {
            try { return await pick(range); }
            finally { range.Abandon(); }
        }
        return range.ShowDialog() == true ? range.Result : null;
    }

    async Task<MergeChoice?> IPageDialogs.ChooseMergeAsync(IReadOnlyList<MergeFile> files, bool insert, Func<Task<IReadOnlyList<MergeFile>>> addFiles)
    {
        var merge = new PdfMergeDialog(files, insert, addFiles) { Owner = this };
        bool ok = MergeDialogOverride is { } run ? await run(merge) && merge.Accept() : merge.ShowDialog() == true;
        return ok && merge.Result is { } result ? new MergeChoice(result, merge.Position) : null;
    }

    PageExportChoice? IPageDialogs.ChooseExport(int pageCount, string range, bool flatten)
    {
        var dialog = new PageExportDialog(pageCount, range) { Owner = this, Flatten = flatten };
        bool ok = ExportDialogOverride is { } run ? run(dialog) && dialog.Accept() : dialog.ShowDialog() == true;
        return ok && dialog.Result is { } groups ? new PageExportChoice(groups, dialog.Flatten) : null;
    }
}
