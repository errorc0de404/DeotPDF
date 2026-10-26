using PdfNotes.Editor.Localization;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.Editor.Session;

/// <summary>What the document workflow needs from the shell around it.</summary>
public interface IDocumentHost
{
    /// <summary>Commits a text box being edited into the document.</summary>
    void FinishTextEditing();

    /// <summary>Cancels a canvas operation in progress.</summary>
    void CancelTool();

    /// <summary>The open document is about to be replaced (still <see cref="DocumentWorkflow.Document"/>): remember its state.</summary>
    void DocumentLeaving(OpenDocument? previous);

    /// <summary>Shows <paramref name="next"/> in place of <paramref name="previous"/>, then releases the previous one.</summary>
    void DocumentReplaced(OpenDocument? previous, OpenDocument next);

    void SetStatus(string text);

    /// <summary>Opening <paramref name="name"/> started (show the loading card), or null: it ended.</summary>
    void Loading(string? name) { }

    /// <summary>Busy or the unsaved mark may have changed: refresh the title and the commands.</summary>
    void StateChanged();

    /// <summary>A file was opened from <paramref name="path"/> (recent list, last position).</summary>
    void Opened(string path);

    /// <summary>The document was written to <paramref name="path"/> (recent list, backup state).</summary>
    void Saved(string path);

    /// <summary>
    /// Another window shows <paramref name="path"/> already: bring it forward and return true, so the file is not opened
    /// twice (spec 4.6, D14g). False when no other window has it.
    /// </summary>
    bool ShowOpenElsewhere(string path);
}

/// <summary>
/// The document file flow of the editor (D14d, spec 4.2, 14, 18): new, open, save, save as, Flatten export and the
/// unsaved-changes question. The shell supplies the dialogs and shows the document.
/// </summary>
public sealed class DocumentWorkflow(IDocumentHost host, IEditorDialogs dialogs)
{
    private bool confirming;

    /// <summary>The open document (null only before the first one is shown and after closing).</summary>
    public OpenDocument? Document { get; private set; }

    /// <summary>True while a file is being opened or saved; document-switching commands are disabled meanwhile.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>True while the unsaved-changes question is shown.</summary>
    public bool IsConfirming => confirming;

    /// <summary>Shows another document; the previous one is released by the host.</summary>
    public void Replace(OpenDocument next)
    {
        var previous = Document;
        host.DocumentLeaving(previous);
        Document = next;
        host.DocumentReplaced(previous, next);
    }

    /// <summary>Takes the document out at closing; the caller disposes it.</summary>
    public OpenDocument? Detach()
    {
        var document = Document;
        Document = null;
        return document;
    }

    /// <summary>
    /// Unsaved changes before closing, a new note or another file (spec 4.2, 18): Save / Don't save / Cancel.
    /// Returns false when the user cancels, the save dialog is cancelled or the save fails.
    /// </summary>
    public async Task<bool> ConfirmDiscardChangesAsync()
    {
        host.FinishTextEditing();
        host.CancelTool();
        if (Document is not { Session.IsDirty: true } || confirming) return !confirming;
        confirming = true;
        try
        {
            return dialogs.Ask(Strings.Format("file.saveChangesConfirm", ("name", Document.DisplayName)), AskButtons.YesNoCancel, NoticeKind.Question) switch
            {
                Answer.Yes => await SaveAsync(saveAs: false),
                Answer.No => true,
                _ => false
            };
        }
        finally { confirming = false; }
    }

    /// <summary>Opens a PDF. On failure the current document stays open and unchanged.</summary>
    public async Task<bool> OpenFileAsync(string path)
    {
        if (IsBusy || host.ShowOpenElsewhere(path)) return false; // that window is in front now; this document stays
        if (!await ConfirmDiscardChangesAsync()) return false;
        SetBusy(true);
        host.SetStatus(Strings.Get("status.opening"));
        host.Loading(Path.GetFileName(path));
        try
        {
            var opened = await OpenDocument.OpenPdfAsync(path);
            Replace(opened);
            host.Opened(path);
            host.SetStatus(OpenedStatus(opened));
            host.Loading(null);
            if (OpenedNotice(opened) is { } notice) dialogs.Tell(notice, NoticeKind.Information);
            return true;
        }
        catch (PdfOpenException e)
        {
            host.SetStatus("");
            TellOpenFailure(path, OpenFailureMessages.For(e.Failure));
        }
        catch (Exception)
        {
            // UI boundary: any other failure (invalid path, engine error, out of memory) is reported, never fatal.
            host.SetStatus("");
            TellOpenFailure(path, Strings.Get("openError.generic"));
        }
        finally
        {
            host.Loading(null);
            SetBusy(false);
        }
        return false;
    }

    /// <summary>The status bar text after opening: page count and restriction (spec 24.6). Other apps' annotations follow once counted (14.6).</summary>
    public static string OpenedStatus(OpenDocument opened) =>
        Strings.Format("status.opened", ("pages", opened.Session.Current.Pages.Count)) + (opened.Source is { PermissionRestricted: true } ? Strings.Get("status.restricted") : "");

    /// <summary>The one-time notice about view-only notes of an opened file, or null.</summary>
    public static string? OpenedNotice(OpenDocument opened) => opened.Source?.AppData is { ReadOnlyCount: > 0 } data
        ? data.HeaderStatus == HeaderStatus.Unsupported
            ? Strings.Get("notice.unsupportedVersion")
            : data.Warnings.Any(w => w.StartsWith("Missing font:", StringComparison.Ordinal)) ? Strings.Get("notice.missingFont")
            : Strings.Format("notice.readOnlyNotes", ("count", data.ReadOnlyCount))
        : null;

    private void TellOpenFailure(string path, string message)
    {
        host.Loading(null);
        dialogs.Tell($"{Path.GetFileName(path)}\n\n{message}", NoticeKind.OpenFailure);
    }

    public async Task<bool> CreateNoteAsync(NewPageSpec spec)
    {
        if (IsBusy || !await ConfirmDiscardChangesAsync()) return false;
        Replace(OpenDocument.CreateNote(spec));
        host.SetStatus("");
        return true;
    }

    // ----- saving (spec 14.1, 14.4, 18) -----

    /// <summary>
    /// Ctrl+S / Save As. A new note or <paramref name="saveAs"/> asks for a path. Saving to the current file that was
    /// changed by another program asks before overwriting. Returns true only when the file was written.
    /// </summary>
    public async Task<bool> SaveAsync(bool saveAs)
    {
        if (IsBusy || Document is not { } doc) return false;
        host.FinishTextEditing();
        string? path = doc.FilePath;
        if (saveAs || path is null)
        {
            path = dialogs.ChooseSavePath(new(Strings.Get("file.saveAsTitle"), doc.FilePath is null ? Strings.Get("file.newNoteFileName") : Path.GetFileName(doc.FilePath), FolderOf(doc)));
            if (path is null) return false;
        }
        return await SaveToAsync(doc, path);
    }

    /// <summary>Saves to <paramref name="path"/> without the dialog (also used by the validation harness).</summary>
    public Task<bool> SaveToAsync(string path) => IsBusy || Document is null ? Task.FromResult(false) : SaveToAsync(Document, path);

    private async Task<bool> SaveToAsync(OpenDocument doc, string path)
    {
        host.FinishTextEditing();
        if (IsOwnFile(doc, path) && doc.FileChangedExternally() &&
            dialogs.Ask(Strings.Format("file.changedExternally", ("name", doc.DisplayName)), AskButtons.YesNo, NoticeKind.Warning) != Answer.Yes)
            return false;
        TellRestrictionDropped(doc);
        SetBusy(true);
        host.SetStatus(Strings.Get("status.saving"));
        try
        {
            await doc.SaveAsync(path);
            host.Saved(path);
            host.SetStatus(Strings.Get("status.saved"));
            return true;
        }
        catch (Exception e)
        {
            // UI boundary: the document and the file on disk are unchanged; report and keep working.
            host.SetStatus("");
            dialogs.Tell($"{Path.GetFileName(path)}\n\n{SaveFailureMessages.For(e)}", NoticeKind.Warning);
            return false;
        }
        finally { SetBusy(false); }
    }

    /// <summary>The first save or export of a permission-restricted PDF tells once that the copy keeps no restriction (spec 24.6).</summary>
    public void TellRestrictionDropped(OpenDocument doc)
    {
        if (!doc.RestrictionNoticePending) return;
        doc.RestrictionNoticePending = false;
        dialogs.Tell(Strings.Get("notice.restrictionDropped"), NoticeKind.Information);
    }

    /// <summary>
    /// 필기 합친 PDF of the whole document (spec 14.5, Flatten): a separate file, by default "name_flattened.pdf", from the
    /// current state with unsaved changes, keeping the outline. The document stays editable with its path and unsaved
    /// mark. The document's own file is never written (D14g). Returns true when the file was written.
    /// </summary>
    public async Task<bool> ExportFlattenedAsync()
    {
        if (IsBusy || Document is not { } doc) return false;
        host.FinishTextEditing();
        string suggested = (doc.FilePath is null ? Strings.Get("file.newNoteName") : Path.GetFileNameWithoutExtension(doc.FilePath)) + "_flattened.pdf";
        if (dialogs.ChooseSavePath(new(Strings.Get("file.exportFlattenedTitle"), suggested, FolderOf(doc))) is not { } path) return false;
        if (IsOwnFile(doc, path))
        {
            dialogs.Tell(Strings.Get("export.ownFile"), NoticeKind.Information);
            return false;
        }
        TellRestrictionDropped(doc);
        SetBusy(true);
        host.SetStatus(Strings.Get("status.exportingFlattened"));
        try
        {
            bool written = await doc.ExportFlattenedAsync(path, KeepForeign);
            host.SetStatus(written ? Strings.Get("status.exportedFlattened") : "");
            return written;
        }
        catch (Exception e)
        {
            // UI boundary: the document is unchanged and an existing target file was not replaced.
            host.SetStatus("");
            dialogs.Tell($"{Path.GetFileName(path)}\n\n{SaveFailureMessages.For(e)}", NoticeKind.Warning);
            return false;
        }
        finally { SetBusy(false); }
    }

    /// <summary>
    /// 페이지 내보내기 after the shell's dialog (spec 11.10, D13j): page index <paramref name="groups"/> of the current state
    /// become PDFs of their own, editable or flattened. One file takes the chosen name; several files add <c>_1</c>, <c>_2</c>, …
    /// The open document, its path and its unsaved mark do not change. Returns the number of files written.
    /// </summary>
    public async Task<int> ExportPagesAsync(IReadOnlyList<int[]> groups, bool flatten)
    {
        if (IsBusy || Document is not { } doc || groups.Count == 0) return 0;
        var model = doc.Session.Current;
        // Every page as one merged file is the whole-document export: its default name, and the outline stays (24.4).
        if (flatten && groups is [var all] && all.SequenceEqual(Enumerable.Range(0, model.Pages.Count))) return await ExportFlattenedAsync() ? 1 : 0;
        string baseName = doc.FilePath is null ? Strings.Get("file.newNoteName") : Path.GetFileNameWithoutExtension(doc.FilePath);
        string suggested = baseName + (groups.Count == 1 ? $" ({PageRanges.Format(groups[0], model.Pages.Count)}).pdf" : ".pdf");
        suggested = string.Concat(suggested.Split(Path.GetInvalidFileNameChars()));
        string title = groups.Count == 1 ? Strings.Get("export.title") : Strings.Format("export.titleSplit", ("files", groups.Count));
        if (dialogs.ChooseSavePath(new(title, suggested, FolderOf(doc), OverwritePrompt: groups.Count == 1)) is not { } path) return 0;
        var targets = ExportTargets(path, groups.Count);
        if (doc.FilePath is { } current && targets.Any(t => string.Equals(Path.GetFullPath(t), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)))
        {
            dialogs.Tell(Strings.Get("export.ownFile"), NoticeKind.Information);
            return 0;
        }
        if (groups.Count > 1 && targets.Count(File.Exists) is > 0 and var existing &&
            dialogs.Ask(Strings.Format("export.replaceExisting", ("count", existing)), AskButtons.YesNo, NoticeKind.Warning) != Answer.Yes)
            return 0;

        int written = 0;
        for (int g = 0; g < groups.Count; g++)
        {
            var ids = groups[g].Select(i => model.Pages[i].Id).ToArray();
            host.SetStatus(groups.Count == 1 ? Strings.Get("status.exportingPages") : Strings.Format("status.exportingPagesOf", ("current", g + 1), ("total", groups.Count)));
            try
            {
                if (!await doc.ExportPagesAsync(targets[g], ids, flatten, KeepForeign)) break;
                written++;
            }
            catch (Exception e)
            {
                host.SetStatus("");
                string done = written > 0 ? Strings.Format("export.partlyDone", ("count", written)) : "";
                dialogs.Tell($"{Path.GetFileName(targets[g])}\n\n{SaveFailureMessages.For(e)}{done}", NoticeKind.Warning);
                return written;
            }
        }
        host.SetStatus(written == 0 ? "" : written == 1 ? Strings.Format("status.exportedFile", ("name", Path.GetFileName(targets[0]))) : Strings.Format("status.exportedFiles", ("count", written)));
        return written;
    }

    /// <summary>One file keeps the chosen name; several get <c>name_1.pdf</c>, <c>name_2.pdf</c>, … next to it.</summary>
    public static string[] ExportTargets(string path, int count)
    {
        if (count == 1) return [path];
        string folder = Path.GetDirectoryName(path) ?? "", name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        if (ext.Length == 0) ext = ".pdf";
        return [.. Enumerable.Range(1, count).Select(i => Path.Combine(folder, $"{name}_{i}{ext}"))];
    }

    /// <summary>Foreign annotations that cannot be flattened stay annotations: continue? (spec 14.5)</summary>
    public bool KeepForeign(int kept) =>
        dialogs.Ask(Strings.Format("file.keepForeignConfirm", ("count", kept)), AskButtons.YesNo, NoticeKind.Question) == Answer.Yes;

    /// <summary>True when <paramref name="path"/> is the document's own file.</summary>
    public static bool IsOwnFile(OpenDocument doc, string path) =>
        doc.FilePath is { } current && string.Equals(Path.GetFullPath(path), current, StringComparison.OrdinalIgnoreCase);

    private static string? FolderOf(OpenDocument doc) => doc.FilePath is null ? null : Path.GetDirectoryName(doc.FilePath);

    private void SetBusy(bool value)
    {
        IsBusy = value;
        host.StateChanged();
    }

    /// <summary>What closing the window does now (spec 18).</summary>
    public CloseStep CloseStep(bool backupRunning) =>
        IsBusy ? Session.CloseStep.WaitForSave
        : backupRunning ? Session.CloseStep.WaitForBackup
        : Document is { Session.IsDirty: true } ? Session.CloseStep.Confirm
        : Session.CloseStep.Close;
}

/// <summary>The answer to a close request.</summary>
public enum CloseStep
{
    /// <summary>Nothing unsaved: close now.</summary>
    Close,

    /// <summary>A save is writing the file: refuse; the user closes again after it finishes.</summary>
    WaitForSave,

    /// <summary>An automatic backup is writing: close when it is done.</summary>
    WaitForBackup,

    /// <summary>Unsaved changes: ask (<see cref="DocumentWorkflow.ConfirmDiscardChangesAsync"/>) and close when allowed.</summary>
    Confirm
}
