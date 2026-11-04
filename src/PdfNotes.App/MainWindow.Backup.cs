using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace PdfNotes.App;

/// <summary>Automatic backup and recovery after an abnormal end (spec 18.2). The work is <see cref="BackupViewModel"/>'s (P1 V6).</summary>
public partial class MainWindow : IWindowCoordinator
{
    private BackupViewModel backup => viewModel.Backup;
    private BackupStore backupStore => viewModel.Backup.Store;
    private bool backupRunning => viewModel.Backup.IsRunning;

    /// <summary>Validation harness: replaces the chosen interval (seconds instead of minutes).</summary>
    public TimeSpan? BackupIntervalOverride { get => backup.IntervalOverride; set => backup.IntervalOverride = value; }

    public bool IsBackingUp => backup.IsRunning;
    public BackupStore Backups => backup.Store;
    public DateTime? LastBackupTime => backup.LastTime;
    public string? LastBackupFile => backup.LastFile;

    private void InitializeBackup()
    {
        // Only a shown window backs up and leaves a recovery marker.
        Loaded += (_, _) =>
        {
            if (backup.Started) return;
            ShowingFile(); // the window handle exists now
            backup.Start();
        };
    }

    private void ResetBackup() => backup.Reset();
    private void BackupSaved() => backup.Saved();

    IWindowCoordinator IMainView.Window => this;

    /// <summary>A pressed button, IME composition, an open dialog or message box (spec 18.2: the backup waits).</summary>
    bool IWindowCoordinator.InputPending =>
        composingText ||
        Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed || Mouse.MiddleButton == MouseButtonState.Pressed ||
        ComponentDispatcher.IsThreadModal || !OwnerEnabled;

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

    /// <summary>Writes one backup now (<see cref="BackupViewModel.RunAsync()"/>). Returns true when a file was written.</summary>
    public Task<bool> RunBackupAsync() => backup.RunAsync();

    /// <summary>백업 비우기 (spec 18.2, D14g): asks once, then deletes every backup the app made. Returns true when it deleted.</summary>
    public bool ClearBackups() => backup.Clear();

    /// <summary>The app is ending because of an unexpected error: the unsaved document goes to the backup folder (<see cref="BackupViewModel.WriteCrashBackup"/>).</summary>
    public string? WriteCrashBackup(Exception? error) => backup.WriteCrashBackup(error, () => { CloseNote(); FinishTextEditing(); });

    /// <summary>After an abnormal end (spec 18.2): offers the newest backup left behind. Returns true when it was opened.</summary>
    public Task<bool> OfferRecoveryAsync() => backup.OfferRecoveryAsync();
}
