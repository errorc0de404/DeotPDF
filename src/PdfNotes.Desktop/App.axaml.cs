using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PdfNotes.Desktop.Themes;
using PdfNotes.Desktop.Views;
using PdfNotes.Editor.Services;

namespace PdfNotes.Desktop;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = AppSettings.Load();
        DesktopTheme.Use(this, settings.Theme, settings.Accent);
        // Headless tests have no lifetime: they make their own windows.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            if (desktop.Args is [{ } path, ..] && File.Exists(path)) _ = window.OpenFileAsync(path);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
