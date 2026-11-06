using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Services;
using PdfNotes.Text;
using PdfNotes.UI;
using PdfNotes.UI.Themes;
using PdfNotes.UI.Views;
using PdfNotes.ViewModels;

namespace PdfNotes.Android;

/// <summary>
/// DeotPDF on Android (roadmap P5a): the app's view in the one screen of the activity. Settings, backups and language
/// packs live in the app's own folder.
/// </summary>
public sealed class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            SkiaText.Register(); // one text engine for the screen and the PDF appearances (P2)
            string folder = Path.Combine(global::Android.App.Application.Context.FilesDir!.AbsolutePath, "settings");
            Directory.CreateDirectory(folder);
            string file = AppSettings.FileIn(folder);
            var settings = AppSettings.Load(file);
            Strings.Apply(settings.Language, AppSettings.LanguageFoldersIn(folder));
            DesktopTheme.Use(this, settings.Theme, settings.Accent);
            var app = new AppServices(settings, AppInfo.DisplayName, s => s.Save(file), folder) { Texts = Strings.Current };
            var view = new MainView(app, new AndroidDialogs(), compose: MainViewModels.Create);
            single.MainView = view;
            view.AttachedToVisualTree += (_, _) => view.Shown();
            OpenSample(view);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>P5a: a sample PDF until files can be opened (P5b).</summary>
    private static void OpenSample(MainView view)
    {
        string path = Path.Combine(global::Android.App.Application.Context.CacheDir!.AbsolutePath, "sample.pdf");
        if (!File.Exists(path))
        {
            if (global::Android.App.Application.Context.Assets!.List("")?.Contains("sample.pdf") != true) return;
            using var asset = global::Android.App.Application.Context.Assets!.Open("sample.pdf");
            using var file = File.Create(path);
            asset.CopyTo(file);
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(async () => await view.OpenFileAsync(path));
    }
}
