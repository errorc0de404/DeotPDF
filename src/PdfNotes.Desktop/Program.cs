using Avalonia;
using PdfNotes.Text;

namespace PdfNotes.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // A file another window shows: that window comes forward and this process ends (spec 4.6).
        if (Platform.WindowActivation.HandOver(args)) return 0;
        SkiaText.Register(); // P2: one text engine for the app, the CLI and the PDF appearances
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
