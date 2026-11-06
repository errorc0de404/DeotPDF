using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.UI.Canvas;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.ViewModels;

using Page = PdfNotes.Core.Models.Page;

namespace PdfNotes.UI.Views;

/// <summary>
/// The settings window (spec 18.1) and the page windows of imports, merges and exports (spec 11.9–11.10, D13j; the flows
/// are <see cref="ImportExportViewModel"/>'s, the windows are here as <see cref="IPageDialogs"/>).
/// </summary>
public sealed partial class MainView : IPageDialogs
{
    /// <summary>
    /// Opens the settings window. OK stores the values in the app settings; nothing here is document state, so the
    /// unsaved mark and Undo stay. Returns true when OK was chosen.
    /// </summary>
    public bool ShowSettings()
    {
        var display = viewModel.Display.Stored;
        var dialog = new SettingsDialog(new(settings.QuickErase, settings.PageDrag,
            new(settings.AutoBackup, settings.BackupIntervalMinutes, settings.BackupMode, settings.BackupFolderIn(appFolder), settings.BackupKeep), display,
            settings.Language, settings.CheckUpdates == true, settings.FingerDrawing, settings.PenPressure),
            () => Confirm(Strings.Get("settings.backupInPlaceConfirm"), NoticeKind.Warning),
            PreviewDisplay, Strings.Discover(AppSettings.LanguageFoldersIn(appFolder)), new(viewModel.Backup.Store.Usage, viewModel.Backup.Clear), new(viewModel.Update.CheckAsync, OpenWebPage));
        if (!dialogs.ShowWindow(dialog) || dialog.Result is not { } values) { PreviewDisplay(display); return false; } // cancel undoes the preview
        SetDisplay(values.Display ?? display);
        settings.QuickErase = values.QuickErase;
        settings.PageDrag = values.PageDrag;
        settings.FingerDrawing = values.FingerDrawing;
        settings.PenPressure = values.PenPressure;
        settings.AutoBackup = values.Backup.Enabled;
        settings.BackupIntervalMinutes = values.Backup.IntervalMinutes;
        settings.BackupMode = values.Backup.Mode;
        settings.BackupFolder = FilePaths.Same(values.Backup.Folder, AppSettings.DefaultBackupFolderIn(appFolder)) ? null : values.Backup.Folder;
        settings.BackupKeep = values.Backup.Keep;
        bool languageChanged = !string.Equals(settings.Language, values.Language, StringComparison.OrdinalIgnoreCase);
        settings.Language = values.Language;
        if (values.CheckUpdates != (settings.CheckUpdates == true)) settings.CheckUpdates = values.CheckUpdates; // untouched: still asked at the first start
        viewModel.SaveSettings();
        if (languageChanged) Tell(Strings.Get("settings.languageNextStart")); // packs load at start (D14e)
        return true;
    }

    /// <summary>Chooses PDFs (or takes <paramref name="paths"/>) and inserts their pages at <paramref name="at"/> (default: after the active page), one Undo unit.</summary>
    public Task<bool> ImportPdfPagesAsync(IReadOnlyList<string>? paths = null, int? at = null) => viewModel.ImportExport.ImportPagesAsync(paths, at);

    /// <summary>파일 › PDF 합치기 (D13j): a new unsaved document from several PDFs.</summary>
    public Task<bool> MergePdfsAsync(IReadOnlyList<string>? paths = null) => viewModel.ImportExport.MergeAsync(paths);

    /// <summary>내보내기 (spec 11.10, 14.5, D14g): the selected pages (or every page) as new PDFs. Returns the files written.</summary>
    public Task<int> ExportPagesAsync(bool allPages = false, bool flatten = false) => viewModel.ImportExport.ExportPagesAsync(allPages, flatten);

    // ----- the page windows (IPageDialogs) -----

    string[]? IPageDialogs.ChoosePdfFiles(string title) => dialogs.ChoosePdfFiles(title);

    async Task<int[]?> IPageDialogs.ChooseImportPagesAsync(MergeFile file)
    {
        var range = new PdfImportDialog(file.Name, file.Shown);
        try { return await dialogs.ShowWindowAsync(range) ? range.Result : null; }
        finally { range.Abandon(); }
    }

    async Task<MergeChoice?> IPageDialogs.ChooseMergeAsync(IReadOnlyList<MergeFile> files, bool insert, Func<Task<IReadOnlyList<MergeFile>>> addFiles)
    {
        var merge = new PdfMergeDialog(files, insert, addFiles);
        merge.ShowPicker = picker => dialogs.ShowChildAsync(picker, merge);
        return await dialogs.ShowWindowAsync(merge) && merge.Result is { } result ? new MergeChoice(result, merge.Position) : null;
    }

    PageExportChoice? IPageDialogs.ChooseExport(int pageCount, string range, bool flatten)
    {
        var dialog = new PageExportDialog(pageCount, range) { Flatten = flatten };
        return dialogs.ShowWindow(dialog) && dialog.Result is { } groups ? new PageExportChoice(groups, dialog.Flatten) : null;
    }

    NewPageSpec? IPageDialogs.ChooseBlankPage(bool insert, PageSetup reference)
    {
        var (w, h) = PageEdits.DisplayedSize(reference);
        var dialog = new NewNoteDialog(insert ? Strings.Get("page.insertBlank") : Strings.Get("page.addBlank"),
            (Units.PointsToMillimeters(w), Units.PointsToMillimeters(h)), reference.Background?.Kind ?? PageBackgroundKind.Blank);
        return dialogs.ShowWindow(dialog) ? dialog.Result : null;
    }

    PageTransformRequest? IPageDialogs.ChooseTransform(bool resize, DocumentModel model, Page active, Func<PageApplyScope, PageSetup[]> targets)
    {
        if (Document is not { } doc) return null;
        double zoom = 700 / (Math.Max(active.Setup.Frame.DisplayWidthPt, active.Setup.Frame.DisplayHeightPt) * Units.DipsPerPoint);
        var dialog = new PageTransformDialog(resize, active.Setup, targets, PageSnapshots.Render(doc, model, active, zoom, RenderScaling));
        return dialogs.ShowWindow(dialog) ? dialog.Result : null;
    }
}
