using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
            window.Opened += async (_, _) =>
            {
                if (desktop.Args is [{ } path, ..] && File.Exists(path)) await window.OpenFileAsync(path);
                await window.CheckForUpdatesAtStartAsync(); // the first-run question and the daily check (development versions skip it)
            };
        }
        base.OnFrameworkInitializationCompleted();
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
