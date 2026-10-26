using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PdfNotes.App.Services;
using PdfNotes.Pdf.Writing;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

/// <summary>Automatic backup and recovery after an abnormal end (spec 18.2).</summary>
public partial class MainWindow
{
    private readonly BackupSchedule backupSchedule = new();
    private BackupStore backupStore = null!;
    private DispatcherTimer? backupTimer;
    private bool backupRunning;
    private string? lastBackupFailure;

    /// <summary>Validation harness: replaces the chosen interval (seconds instead of minutes).</summary>
    public TimeSpan? BackupIntervalOverride { get; set; }

    public bool IsBackingUp => backupRunning;
    public BackupStore Backups => backupStore;
    public DateTime? LastBackupTime { get; private set; }
    public string? LastBackupFile { get; private set; }

    private TimeSpan BackupInterval => BackupIntervalOverride ?? TimeSpan.FromMinutes(settings.BackupIntervalMinutes);

    private void InitializeBackup()
    {
        backupStore = new BackupStore(Path.GetDirectoryName(AppSettings.DefaultPath)!);
        // Only a shown window backs up and leaves a recovery marker.
        Loaded += (_, _) =>
        {
            if (backupTimer is not null) return;
            ShowingFile(); // the window handle exists now
            backupStore.Start();
            backupTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            backupTimer.Tick += async (_, _) => await BackupTickAsync();
            backupTimer.Start();
        };
    }

    /// <summary>The open document changed (new, opened, closed): its backup state starts over.</summary>
    private void ResetBackup()
    {
        backupSchedule.Reset();
        lastBackupFailure = null;
        if (backupTimer is not null) backupStore.SetRecoverable(null);
    }

    /// <summary>A save made the open document's backups older than its file.</summary>
    private void BackupSaved() { if (backupTimer is not null) backupStore.SetRecoverable(null); }

    private async Task BackupTickAsync()
    {
        if (document is not { } doc) return;
        var now = DateTime.Now;
        backupSchedule.Observe(doc.Session.Revision, doc.Session.SavedRevision, now);
        if (!settings.AutoBackup || backupRunning || !backupSchedule.Due(now, BackupInterval) || BackupDeferred) return;
        await RunBackupAsync(doc);
    }

    /// <summary>
    /// Work the backup waits for (spec 18.2): a stroke, drag or erase in progress, IME composition, a pressed button,
    /// an open dialog or message box, a save or an open in progress.
    /// </summary>
    private bool BackupDeferred =>
        busy || confirming || View.IsToolActive || composingText ||
        Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed || Mouse.MiddleButton == MouseButtonState.Pressed ||
        System.Windows.Interop.ComponentDispatcher.IsThreadModal || !OwnerEnabled || document?.IsWriting == true;

    private bool OwnerEnabled
    {
        get
        {
            var handle = new WindowInteropHelper(this).Handle;
            return handle == IntPtr.Zero || IsWindowEnabled(handle); // a native message box disables its owner
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    /// <summary>
    /// Writes one backup now. Folder mode: an editable PDF in the backup folder; the document, its file and its unsaved
    /// mark stay. Original mode: the same safe save as Ctrl+S (the unsaved mark clears, Undo stays), unless the note has
    /// no file, the file is read-only or locked, or it changed outside the app: then the backup folder is used. A failure
    /// shows once in the status bar and is tried again after the interval. Returns true when a file was written.
    /// </summary>
    public async Task<bool> RunBackupAsync() => document is { } doc && !backupRunning && await RunBackupAsync(doc);

    private async Task<bool> RunBackupAsync(OpenDocument doc)
    {
        backupRunning = true;
        try
        {
            if (settings.BackupMode == BackupMode.Original && doc.FilePath is { } path && OriginalWritable(doc, path))
            {
                try
                {
                    await doc.SaveAsync(path);
                    backupSchedule.Done(doc.Session.SavedRevision);
                    Backed(null);
                    BackupSaved();
                    return true;
                }
                catch (SaveFailedException e) when (e.Failure is SaveFailure.Locked or SaveFailure.AccessDenied) { } // into the backup folder
            }
            string folder = settings.EffectiveBackupFolder;
            Directory.CreateDirectory(folder);
            string file = backupStore.NewFile(folder, doc.FilePath);
            long revision = await doc.WriteCopyAsync(file);
            backupStore.Added(BackupStore.DocumentKey(doc.FilePath), file, settings.BackupKeep);
            backupSchedule.Done(revision);
            if (ReferenceEquals(doc, document) && doc.Session.SavedRevision != revision)
                backupStore.SetRecoverable(new(doc.DisplayName, file, DateTime.Now));
            Backed(file);
            return true;
        }
        catch (Exception e)
        {
            // Never a modal error (spec 18.2): a short status once per kind of failure, retried after the interval.
            backupSchedule.Failed(DateTime.Now);
            string message = Strings.Format("backup.failed", ("reason", SaveFailureMessages.For(e)));
            if (message != lastBackupFailure && ReferenceEquals(doc, document)) StatusText.Text = message;
            lastBackupFailure = message;
            return false;
        }
        finally
        {
            backupRunning = false;
            UpdateTitle();
        }
    }

    /// <summary>
    /// 백업 비우기 (spec 18.2, D14g): asks once with the count and size, then deletes every backup the app made (files
    /// another window is using stay). Returns true when it deleted.
    /// </summary>
    public bool ClearBackups()
    {
        var (count, bytes) = backupStore.Usage();
        if (count == 0) return false;
        if (Prompt(Strings.Format("settings.clearBackupsConfirm", ("count", count), ("size", Views.SettingsDialog.Size(bytes))),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        backupStore.Clear();
        LastBackupFile = null;
        return true;
    }

    private void Backed(string? file)
    {
        LastBackupTime = DateTime.Now;
        LastBackupFile = file;
        lastBackupFailure = null;
        if (!busy) StatusText.Text = Strings.Format("backup.done", ("time", string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:HH:mm}", LastBackupTime)));
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
    /// The app is ending because of an unexpected error (2026-10-15 user request): the open document with its unsaved
    /// changes is written to the backup folder as <c>crash_yyyyMMdd_HHmmss.pdf</c> (the error text next to it as .txt) and
    /// left as the recoverable backup, so the next start asks to open it. Nothing is written for a document without
    /// unsaved changes. Every step is best effort. Returns the written file or null.
    /// </summary>
    public string? WriteCrashBackup(Exception? error)
    {
        try
        {
            string folder = settings.EffectiveBackupFolder;
            Directory.CreateDirectory(folder);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            if (error is not null)
                try { File.WriteAllText(Path.Combine(folder, $"crash_{stamp}.txt"), error.ToString()); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            try { CloseNote(); FinishTextEditing(); } catch (Exception) { } // typed text is kept when the editors still work
            if (document is not { } doc || !doc.Session.IsDirty) return null;
            string file = Path.Combine(folder, $"crash_{stamp}.pdf");
            doc.WriteCrashCopy(file);
            backupStore.Added(BackupStore.CrashKey, file, settings.BackupKeep);
            backupStore.SetRecoverable(new(doc.DisplayName, file, DateTime.Now));
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
        var abandoned = backupStore.TakeAbandoned();
        if (abandoned.Count == 0) return false;
        var newest = abandoned[0];
        bool crash = Path.GetFileName(newest.File).StartsWith("crash_", StringComparison.OrdinalIgnoreCase);
        if (Prompt(crash ? Strings.Format("backup.recoverCrash", ("name", newest.DocumentName), ("time", string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:yyyy-MM-dd HH:mm}", newest.Created)))
                : Strings.Format("backup.recoverUnclean", ("name", newest.DocumentName), ("time", string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:yyyy-MM-dd HH:mm}", newest.Created))),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        return await OpenFileAsync(newest.File);
    }
}
