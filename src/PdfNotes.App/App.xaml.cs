using System.IO;
using System.Windows;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

public partial class App : Application
{
    // The name the user sees (window title, message boxes, Open with). The file-format id stays "PdfNotes" (SchemaV1.AppId).
    public const string DisplayName = "DeotPDF";

    private bool crashing;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        PdfNotes.Text.WpfText.Register();
        UseLanguage(AppSettings.Load().Language);
        // An unexpected error ends the app, but first the open document is saved as a crash backup (2026-10-15).
        DispatcherUnhandledException += (_, args) => { args.Handled = true; Crash(args.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Dispatcher.Invoke(() => Crash(args.ExceptionObject as Exception), System.Windows.Threading.DispatcherPriority.Send);
        // A file another window shows already: that window comes forward and this one does not start (spec 4.6, D14g).
        if (e.Args.Length == 1 && File.Exists(e.Args[0]) &&
            BackupStore.WindowShowing(Path.GetDirectoryName(AppSettings.DefaultPath)!, e.Args[0]) is { } other)
        {
            Services.WindowActivation.Bring(other);
            Shutdown();
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        // "DeotPDF.exe file.pdf" (also used by Explorer "Open with").
        // After an abnormal end the newest backup can be opened first (spec 18.2).
        if (!await window.OfferRecoveryAsync() && e.Args.Length == 1 && File.Exists(e.Args[0])) await window.OpenFileAsync(e.Args[0]);
        await window.CheckForUpdatesAtStartAsync();
    }

    /// <summary>
    /// Applies the language pack before any window exists (D14e). Problems (missing keys, placeholders, unreadable packs)
    /// fall back to Korean and go to <c>Languages\language.log</c> in the settings folder.
    /// </summary>
    private static void UseLanguage(string? language)
    {
        var problems = Strings.Apply(language, AppSettings.LanguageFolders);
        string log = Path.Combine(AppSettings.LanguageFolders[^1], "language.log");
        try
        {
            if (problems.Count == 0) { if (File.Exists(log)) File.Delete(log); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllLines(log, [$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {language}", .. problems]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Writes the crash backup, tells the user once and ends without the normal-end mark (so the next start offers it).</summary>
    private void Crash(Exception? error)
    {
        if (crashing) return;
        crashing = true;
        string? file = (MainWindow as MainWindow)?.WriteCrashBackup(error);
        try
        {
            MessageBox.Show(file is null ? Strings.Get("app.crash")
                : Strings.Get("app.crashBackedUp"),
                DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception) { }
        Environment.Exit(1);
    }
}
