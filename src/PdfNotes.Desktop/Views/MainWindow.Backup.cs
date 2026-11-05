using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PdfNotes.Desktop.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.ViewModels;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// Automatic backup, recovery after an abnormal end (spec 18.2) and the running window's session marker (spec 4.6), as
/// the WPF window's <c>MainWindow.Backup</c>. The work is <see cref="BackupViewModel"/>'s.
/// </summary>
public sealed partial class MainWindow
{
    private BackupViewModel backup => viewModel.Backup;
    private bool buttonDown;
    private CancellationTokenSource? activationListener;

    /// <summary>Validation: replaces the chosen interval (seconds instead of minutes).</summary>
    public TimeSpan? BackupIntervalOverride { get => backup.IntervalOverride; set => backup.IntervalOverride = value; }

    public bool IsBackingUp => backup.IsRunning;
    public BackupStore Backups => backup.Store;
    public DateTime? LastBackupTime => backup.LastTime;
    public string? LastBackupFile => backup.LastFile;

    /// <summary>Tests: receives the window another DeotPDF names instead of bringing it forward.</summary>
    public Action<long>? BringOverride { get; set; }

    private void InitializeBackup()
    {
        // Only a shown window backs up and leaves a recovery marker (and has a handle to name in it).
        Opened += (_, _) =>
        {
            if (backup.Started) return;
            backup.Start();
            ShowingFile();
            if (!OperatingSystem.IsWindows()) ListenForActivation();
        };
        // A pressed button anywhere in the window (spec 18.2: the backup waits).
        AddHandler(PointerPressedEvent, (_, e) => buttonDown = AnyButton(e.GetCurrentPoint(this).Properties), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, e) => buttonDown = AnyButton(e.GetCurrentPoint(this).Properties), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, _) => buttonDown = false, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static bool AnyButton(PointerPointProperties p) => p.IsLeftButtonPressed || p.IsRightButtonPressed || p.IsMiddleButtonPressed;

    /// <summary>
    /// Work a backup waits for (spec 18.2): a tool at work, IME composition, a pressed button, an open dialog. Text being
    /// edited is backed up (the editor writes into the document as it goes) unless it is composing.
    /// </summary>
    bool IWindowCoordinator.InputPending =>
        Canvas.IsToolActive || dialogs.IsOpen || buttonDown || editSession?.Model.IsComposing == true; // the canvas's first-key composition is the session's too

    /// <summary>Tells the other windows which file this one shows (spec 4.6).</summary>
    private void ShowingFile()
    {
        if (!backup.Started) return; // before the window is shown it has no handle and no marker
        backup.Store.SetOpenDocument(Document?.FilePath, WindowActivation.WindowId(this));
    }

    /// <summary>macOS and Linux: another DeotPDF asks this window to come forward (<see cref="WindowActivation"/>).</summary>
    private void ListenForActivation()
    {
        activationListener = new CancellationTokenSource();
        _ = WindowActivation.Listen(WindowActivation.PipeName(Environment.ProcessId), () => Dispatcher.UIThread.Post(ComeForward), activationListener.Token);
    }

    /// <summary>Restores and activates this window.</summary>
    public void ComeForward()
    {
        if (WindowState == Avalonia.Controls.WindowState.Minimized) WindowState = Avalonia.Controls.WindowState.Normal;
        Activate();
    }

    /// <summary>The window closed normally: no marker is left (nothing to recover next time).</summary>
    private void EndBackup()
    {
        activationListener?.Cancel();
        if (!backup.Started) return;
        backup.Stop();
        backup.Store.End();
    }

    /// <summary>Writes one backup now (<see cref="BackupViewModel.RunAsync()"/>). Returns true when a file was written.</summary>
    public Task<bool> RunBackupAsync() => backup.RunAsync();

    /// <summary>백업 비우기 (spec 18.2): asks once, then deletes every backup the app made.</summary>
    public bool ClearBackups() => backup.Clear();

    /// <summary>The app is ending because of an unexpected error: the unsaved document goes to the backup folder.</summary>
    public string? WriteCrashBackup(Exception? error) => backup.WriteCrashBackup(error, () => { CloseNote(); FinishTextEditing(); });

    /// <summary>The app ends after an unexpected error: one message (the crash backup is already written).</summary>
    public void TellCrash(string text) => dialogs.Tell(text, PdfNotes.Editor.Platform.NoticeKind.Warning);

    /// <summary>The first note of a new window, not changed yet: a file opened by the system can take its place (macOS).</summary>
    public bool ShowsUntouchedNote => !busy && Document is { FilePath: null } doc && !doc.Session.IsDirty && !doc.Session.CanUndo;

    /// <summary>After an abnormal end (spec 18.2): offers the newest backup left behind. Returns true when it was opened.</summary>
    public Task<bool> OfferRecoveryAsync() => backup.OfferRecoveryAsync();
}
