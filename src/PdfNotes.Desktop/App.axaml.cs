using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PdfNotes.Desktop.Platform;
using PdfNotes.Desktop.Themes;
using PdfNotes.Desktop.Views;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Services;

namespace PdfNotes.Desktop;

public sealed class App : Application
{
    /// <summary>The name the user sees (window title, message boxes). The file-format id stays "PdfNotes".</summary>
    public const string DisplayName = "DeotPDF";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = AppSettings.Load();
        UseLanguage(settings.Language);
        UsePlatformFont(this);
        DesktopTheme.Use(this, settings.Theme, settings.Accent);
        // Headless tests have no lifetime: they make their own windows.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            CatchCrashes(window);
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable) activatable.Activated += (_, e) => FilesActivated(window, e);
            window.Opened += async (_, _) =>
            {
                // "DeotPDF file.pdf" (Explorer, Open with, a .desktop launcher). After an abnormal end the newest backup can be opened first (spec 18.2).
                if (!await window.OfferRecoveryAsync() && desktop.Args is [{ } path] && File.Exists(path)) await window.OpenFileAsync(path);
                await window.CheckForUpdatesAtStartAsync(); // the first-run question and the daily check (development versions skip it)
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// macOS opens documents by event, not by argument (Finder, Open With, a file dropped on the Dock icon): each file is
    /// brought forward where another window shows it, opened here while this window still shows its untouched first
    /// note, and otherwise in a new process, so one window stays one document (spec 4.6).
    /// </summary>
    private static void FilesActivated(MainWindow window, ActivatedEventArgs e)
    {
        if (e is not FileActivatedEventArgs files) return;
        foreach (var item in files.Files)
        {
            if (item.TryGetLocalPath() is not { } path || !File.Exists(path)) continue;
            if (BackupStore.WindowShowing(AppSettings.Folder, path) is { } other) { WindowActivation.Bring(other); continue; }
            if (window.ShowsUntouchedNote) { _ = window.OpenFileAsync(path); continue; }
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, [path]) { UseShellExecute = false }); }
            catch (Exception x) when (x is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private bool crashing;

    /// <summary>
    /// An unexpected error ends the app, but first the open document is saved as a crash backup (spec 18.2, as the WPF app).
    /// </summary>
    private void CatchCrashes(MainWindow window)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) => { e.Handled = true; Crash(window, e.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Dispatcher.UIThread.Invoke(() => Crash(window, e.ExceptionObject as Exception), DispatcherPriority.Send);
    }

    /// <summary>Writes the crash backup, tells the user once and ends without the normal-end mark (so the next start offers it).</summary>
    private void Crash(MainWindow window, Exception? error)
    {
        if (crashing) return;
        crashing = true;
        string? file = window.WriteCrashBackup(error);
        try { window.TellCrash(Strings.Get(file is null ? "app.crash" : "app.crashBackedUp")); }
        catch (Exception) { } // the message is a courtesy; the backup is what matters
        Environment.Exit(1);
    }

    /// <summary>
    /// The UI font token (Font.Ui, Malgun Gothic as in the WPF app) on macOS and Linux is that system's Korean UI font, so
    /// menus and labels do not fall back glyph by glyph. Windows keeps the token.
    /// </summary>
    public static void UsePlatformFont(Application app)
    {
        if (OperatingSystem.IsWindows()) return;
        string family = OperatingSystem.IsMacOS() ? "Apple SD Gothic Neo" : "Noto Sans CJK KR";
        if (FontManager.Current.SystemFonts.Any(f => f.Name == family)) app.Resources["Font.Ui"] = new FontFamily(family);
    }

    /// <summary>
    /// Applies the language pack before any window exists (D14e, as the WPF app). Problems fall back to Korean and go to
    /// <c>Languages/language.log</c> in the settings folder.
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
}
