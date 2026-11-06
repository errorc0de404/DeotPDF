using System.Globalization;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Writing;

namespace PdfNotes.ViewModels;

/// <summary>The window as the backup sees it (P1 V6): whether the user is in the middle of something it must not interrupt.</summary>
public interface IWindowCoordinator
{
    /// <summary>
    /// A pressed mouse button, IME composition, an open dialog or message box (the window is modal or disabled): work a
    /// backup waits for (spec 18.2).
    /// </summary>
    bool InputPending { get; }

    /// <summary>Another window shows <paramref name="path"/>: bring it forward and return true (spec 4.6).</summary>
    bool ShowOpenElsewhere(string path);
}

/// <summary>
/// Automatic backup and recovery after an abnormal end (spec 18.2; P1 V6). A 500 ms tick watches the document's revision
/// and writes a backup when one is due and nothing is in progress. Never a modal error: a failure shows once in the status
/// line and is tried again after the interval.
/// </summary>
public sealed class BackupViewModel : ObservableObject
{
    private readonly MainViewModel owner;
    private readonly IEditorDialogs dialogs;
    private readonly TimeProvider time;
    private readonly BackupSchedule schedule = new();
    private ITimer? timer;
    private bool running;
    private string? lastFailure;
    private readonly string settingsFolder;
    private Texts texts => owner.Texts;

    /// <param name="folder">The settings folder: the backup store keeps its records and the running windows' markers there.</param>
    public BackupViewModel(MainViewModel owner, IEditorDialogs dialogs, TimeProvider time, string folder)
    {
        this.owner = owner;
        this.dialogs = dialogs;
        this.time = time;
        settingsFolder = folder;
        Store = new BackupStore(folder);
    }

    public BackupStore Store { get; }

    /// <summary>Validation harness: replaces the chosen interval (seconds instead of minutes).</summary>
    public TimeSpan? IntervalOverride { get; set; }

    public bool IsRunning => running;

    /// <summary>The backup ticks (a shown window only: it also leaves the recovery marker).</summary>
    public bool Started => timer is not null;

    public DateTime? LastTime { get; private set; }
    public string? LastFile { get; private set; }

    private AppSettings Settings => owner.Settings;
    private TimeSpan Interval => IntervalOverride ?? TimeSpan.FromMinutes(Settings.BackupIntervalMinutes);
    private DateTime Now => time.GetLocalNow().DateTime;

    /// <summary>The window is shown: the store marks this session and the 500 ms tick starts.</summary>
    public void Start()
    {
        if (timer is not null) return;
        Store.Start();
        timer = time.CreateTimer(_ => owner.Ui.Run(() => _ = TickAsync()), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    /// <summary>The window closed.</summary>
    public void Stop()
    {
        timer?.Dispose();
        timer = null;
    }

    /// <summary>The open document changed (new, opened, closed): its backup state starts over.</summary>
    public void Reset()
    {
        schedule.Reset();
        lastFailure = null;
        if (timer is not null) Store.SetRecoverable(null);
    }

    /// <summary>A save made the open document's backups older than its file.</summary>
    public void Saved() { if (timer is not null) Store.SetRecoverable(null); }

    /// <summary>One tick: a backup when it is due and nothing is in progress.</summary>
    public async Task TickAsync()
    {
        if (owner.Document is not { } doc) return;
        var now = Now;
        schedule.Observe(doc.Session.Revision, doc.Session.SavedRevision, now);
        if (!Settings.AutoBackup || running || !schedule.Due(now, Interval) || Deferred) return;
        await RunAsync(doc);
    }

    /// <summary>
    /// Work the backup waits for (spec 18.2): a stroke, drag or erase in progress, IME composition, a pressed button,
    /// an open dialog or message box, a save or an open in progress.
    /// </summary>
    public bool Deferred =>
        owner.IsBusy || owner.Workflow.IsConfirming || owner.Viewport.IsToolActive || owner.Window.InputPending || owner.Document?.IsWriting == true;

    /// <summary>Writes one backup now (see <see cref="RunAsync(OpenDocument)"/>). Returns true when a file was written.</summary>
    public async Task<bool> RunAsync() => owner.Document is { } doc && !running && await RunAsync(doc);

    /// <summary>
    /// Folder mode: an editable PDF in the backup folder; the document, its file and its unsaved mark stay. Original mode:
    /// the same safe save as Ctrl+S (the unsaved mark clears, Undo stays), unless the note has no file, the file is read-only
    /// or locked, or it changed outside the app: then the backup folder is used.
    /// </summary>
    private async Task<bool> RunAsync(OpenDocument doc)
    {
        running = true;
        try
        {
            if (Settings.BackupMode == BackupMode.Original && doc.FilePath is { } path && OriginalWritable(doc, path))
            {
                try
                {
                    await doc.SaveAsync(path);
                    schedule.Done(doc.Session.SavedRevision);
                    Backed(null);
                    Saved();
                    return true;
                }
                catch (SaveFailedException e) when (e.Failure is SaveFailure.Locked or SaveFailure.AccessDenied) { } // into the backup folder
            }
            string folder = Settings.BackupFolderIn(settingsFolder);
            Directory.CreateDirectory(folder);
            string file = Store.NewFile(folder, doc.Location?.Name); // a content URI's document is named too (P5b)
            long revision = await doc.WriteCopyAsync(file);
            Store.Added(doc.Location?.Key ?? BackupStore.NewNotesKey, file, Settings.BackupKeep); // a file's key is BackupStore.DocumentKey's
            schedule.Done(revision);
            if (ReferenceEquals(doc, owner.Document) && doc.Session.SavedRevision != revision)
                Store.SetRecoverable(new(doc.DisplayNameIn(texts), file, Now));
            Backed(file);
            return true;
        }
        catch (Exception e)
        {
            schedule.Failed(Now);
            string message = texts.Format("backup.failed", ("reason", SaveFailureMessages.For(e, texts)));
            if (message != lastFailure && ReferenceEquals(doc, owner.Document)) owner.Status = message;
            lastFailure = message;
            return false;
        }
        finally
        {
            running = false;
            owner.Refresh();
        }
    }

    private void Backed(string? file)
    {
        LastTime = Now;
        LastFile = file;
        lastFailure = null;
        if (!owner.IsBusy) owner.Status = texts.Format("backup.done", ("time", string.Format(CultureInfo.CurrentCulture, "{0:HH:mm}", LastTime)));
    }

    private static bool OriginalWritable(OpenDocument doc, string path)
    {
        if (doc.FileChangedExternally()) return false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.IsReadOnly) return false;
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// 백업 비우기 (spec 18.2, D14g): asks once with the count and size, then deletes every backup the app made (files
    /// another window is using stay). Returns true when it deleted.
    /// </summary>
    public bool Clear()
    {
        var (count, bytes) = Store.Usage();
        if (count == 0) return false;
        if (dialogs.Ask(texts.Format("settings.clearBackupsConfirm", ("count", count), ("size", FileSizes.Format(bytes))), AskButtons.YesNo, NoticeKind.Warning) != Answer.Yes) return false;
        Store.Clear();
        LastFile = null;
        return true;
    }

    /// <summary>
    /// The app is ending because of an unexpected error (2026-10-15 user request): the open document with its unsaved
    /// changes is written to the backup folder as <c>crash_yyyyMMdd_HHmmss.pdf</c> (the error text next to it as .txt) and
    /// left as the recoverable backup, so the next start asks to open it. <paramref name="keepTyped"/> commits text still
    /// being typed. Nothing is written for a document without unsaved changes. Every step is best effort.
    /// </summary>
    public string? WriteCrashBackup(Exception? error, Action keepTyped)
    {
        try
        {
            string folder = Settings.BackupFolderIn(settingsFolder);
            Directory.CreateDirectory(folder);
            string stamp = Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            if (error is not null)
                try { File.WriteAllText(Path.Combine(folder, $"crash_{stamp}.txt"), error.ToString()); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            try { keepTyped(); } catch (Exception) { } // typed text is kept when the editors still work
            if (owner.Document is not { } doc || !doc.Session.IsDirty) return null;
            string file = Path.Combine(folder, $"crash_{stamp}.pdf");
            doc.WriteCrashCopy(file);
            Store.Added(BackupStore.CrashKey, file, Settings.BackupKeep);
            Store.SetRecoverable(new(doc.DisplayNameIn(texts), file, Now));
            return file;
        }
        catch (Exception) { return null; } // the original error is what matters now
    }

    /// <summary>
    /// After an abnormal end (spec 18.2): when a window left a backup newer than its last save, asks to open it.
    /// Ignoring keeps the backup file. Returns true when a backup was opened.
    /// </summary>
    public async Task<bool> OfferRecoveryAsync()
    {
        var abandoned = Store.TakeAbandoned();
        if (abandoned.Count == 0) return false;
        var newest = abandoned[0];
        bool crash = Path.GetFileName(newest.File).StartsWith("crash_", StringComparison.OrdinalIgnoreCase);
        string when = string.Format(CultureInfo.CurrentCulture, "{0:yyyy-MM-dd HH:mm}", newest.Created);
        string text = crash ? texts.Format("backup.recoverCrash", ("name", newest.DocumentName), ("time", when))
            : texts.Format("backup.recoverUnclean", ("name", newest.DocumentName), ("time", when));
        if (await dialogs.AskAsync(text, AskButtons.YesNo, NoticeKind.Question) != Answer.Yes) return false; // Android answers later (P5b)
        return await owner.Workflow.OpenFileAsync(newest.File);
    }
}

/// <summary>File sizes for people.</summary>
public static class FileSizes
{
    /// <summary>KB below one MB, MB with one decimal, GB with one decimal.</summary>
    public static string Format(long bytes) => bytes switch
    {
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0} KB", Math.Ceiling(bytes / 1024.0)), // unit symbols: not UI text (D14e)
        < 1024L * 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.0} MB", bytes / (1024.0 * 1024)), // unit symbols: not UI text (D14e)
        _ => string.Format(CultureInfo.CurrentCulture, "{0:0.0} GB", bytes / (1024.0 * 1024 * 1024)) // unit symbols: not UI text (D14e)
    };
}
