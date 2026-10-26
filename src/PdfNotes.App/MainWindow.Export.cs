using System.IO;
using System.Windows;
using PdfNotes.App.Services;
using PdfNotes.App.Views;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

/// <summary>Page export and split (spec 11.10, D13j) and clearing the notes of pages (11.11).</summary>
public partial class MainWindow
{
    /// <summary>Replaces showing the export dialog (validation harness): gets the dialog, returns true for 내보내기.</summary>
    public Func<PageExportDialog, bool>? ExportDialogOverride { get; set; }

    /// <summary>
    /// 내보내기, one window (spec 11.10, 14.5, D14g): pages of the current state (several files split by <c>;</c>) become
    /// PDFs of their own, editable or with the notes merged (Flatten). From the 페이지 tab and the page list the range
    /// starts as the selected pages, from the 파일 tab as every page; <paramref name="flatten"/> preselects 필기 합친 PDF
    /// with every page (the former Flatten command). One file takes the chosen name; several files add <c>_1</c>, <c>_2</c>, …
    /// The open document's own file is never a target. The document, its path and its unsaved mark do not change.
    /// Returns the number of files written.
    /// </summary>
    public async Task<int> ExportPagesAsync(bool allPages = false, bool flatten = false)
    {
        if (busy || document is not { } doc) return 0;
        FinishTextEditing();
        var model = doc.Session.Current;
        var selected = SelectedPageIds.Select(model.IndexOfPage).Where(i => i >= 0);
        var dialog = new PageExportDialog(model.Pages.Count, allPages || flatten ? PageRanges.All : PageRanges.Format(selected, model.Pages.Count)) { Owner = this, Flatten = flatten };
        bool ok = ExportDialogOverride is { } run ? run(dialog) && dialog.Accept() : dialog.ShowDialog() == true;
        if (!ok || dialog.Result is not { Length: > 0 } groups) return 0;
        return await workflow.ExportPagesAsync(groups, dialog.Flatten);
    }

    /// <summary>
    /// 이 페이지의 필기 삭제 (spec 11.11): every annotation of the selected pages, locked ones too, after one confirmation,
    /// as one Undo unit. Returns true when something was deleted.
    /// </summary>
    public bool ClearSelectedPageNotes()
    {
        if (document is not { } doc) return false;
        FinishTextEditing();
        var ids = SelectedPageIds;
        var pages = ids.Select(doc.Session.Current.GetPage).ToList();
        int count = pages.Sum(p => p.Annotations.Count), locked = pages.Sum(p => p.Annotations.Count(a => a.Locked));
        if (count == 0) { StatusText.Text = Strings.Get("clearNotes.nothing"); return false; }
        string which = pages.Count == 1 ? Strings.Get("clearNotes.thisPage") : Strings.Format("clearNotes.selectedPages", ("count", pages.Count));
        string lockedNote = locked > 0 ? Strings.Format("clearNotes.lockedIncluded", ("count", locked)) : "";
        if (Prompt(Strings.Format("clearNotes.confirm", ("which", which), ("count", count), ("locked", lockedNote)), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        selectTool.SetSelection([]);
        bool done = RunPageCommand(m => DocumentEdits.ClearPages(m, ids));
        if (done) StatusText.Text = Strings.Format("clearNotes.done", ("count", count));
        return done;
    }

    private async void ExportPagesClick(object sender, RoutedEventArgs e) => await ExportPagesAsync(allPages: ReferenceEquals(sender, ExportPagesFileButton));
    private void ClearPageNotesClick(object sender, RoutedEventArgs e) => ClearSelectedPageNotes();
    private async void MergePdfsClick(object sender, RoutedEventArgs e) => await MergePdfsAsync();
}
