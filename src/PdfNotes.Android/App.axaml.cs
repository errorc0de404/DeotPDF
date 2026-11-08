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
/// DeotPDF on Android (roadmap P5a–P5b): the app's view in the one screen of the activity. Settings, backups and language
/// packs live in the app's own folder; documents come from the system's picker or from other apps as content URIs.
/// </summary>
public sealed class App : Avalonia.Application
{
    private static MainView? view;
    private static DocumentLocation? pending;

    /// <summary>Startup timing in the log (adb logcat -s DEOTSTART), debug builds only.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    public static void Mark(string what) => global::Android.Util.Log.Info("DEOTSTART", $"{(global::Android.OS.SystemClock.ElapsedRealtime() - global::Android.OS.Process.StartElapsedRealtime)} ms {what}");

    public override void Initialize() { Mark("Initialize"); AvaloniaXamlLoader.Load(this); Mark("Initialize done"); }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            Mark("framework ready");
#if DEBUG
            MainView.StartupMark = what => Mark(what);
#endif
            SkiaText.Register(); Mark("skia text"); // one text engine for the screen and the PDF appearances (P2)
            // A command's failure would otherwise vanish with its task: it goes to the log (adb logcat -s DeotPDF).
            TaskScheduler.UnobservedTaskException += (_, e) => global::Android.Util.Log.Error("DeotPDF", "unobserved: " + e.Exception);
            var context = global::Android.App.Application.Context;
            string folder = Path.Combine(context.FilesDir!.AbsolutePath, "settings");
            Directory.CreateDirectory(folder);
            string file = AppSettings.FileIn(folder);
            var settings = AppSettings.Load(file); Mark("settings");
            Strings.Apply(settings.Language, AppSettings.LanguageFoldersIn(folder));
            Mark("settings+strings");
            DesktopTheme.Use(this, settings.Theme, settings.Accent); Mark("theme");
            var app = new AppServices(settings, AppInfo.DisplayName, s => s.Save(file), folder) { Texts = Strings.Current };
            MainView? shown = null;
            shown = new MainView(app, new AndroidDialogs(() => shown), compose: MainViewModels.Create,
                locations: new AndroidLocations(context));
            shown.UseTouchLayout(); // two rows at the top, the other tabs over the document (2026-11-08)
            view = shown; Mark("MainView made");
            single.MainView = shown;
            shown.AttachedToVisualTree += (_, _) =>
            {
                Mark("attached");
#if DEBUG
                if (Avalonia.Controls.TopLevel.GetTopLevel(shown) is { } top)
                {
                    void Laid(object? sender, EventArgs e) { top.LayoutUpdated -= Laid; Mark("first layout"); }
                    top.LayoutUpdated += Laid;
                    top.RequestAnimationFrame(_ => Mark("first frame"));
                }
#endif
                shown.Shown(); Mark("shown");
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Mark("first idle"), Avalonia.Threading.DispatcherPriority.Background);
                if (pending is { } location) { pending = null; Open(location); }
                else Avalonia.Threading.Dispatcher.UIThread.Post(async () => await shown.OfferRecoveryAsync()); // after an abnormal end (spec 18.2)
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Opens a document another app sent; one that arrives before the view is shown opens when it is.</summary>
    public static void Open(DocumentLocation location)
    {
        if (view is not { } shown || Avalonia.Controls.TopLevel.GetTopLevel(shown) is null) { pending = location; return; }
        Avalonia.Threading.Dispatcher.UIThread.Post(async () => await shown.ViewModel.Workflow.OpenAsync(location));
    }
}
