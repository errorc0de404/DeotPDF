using Avalonia;
using PdfNotes.Text;

namespace PdfNotes.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        SkiaText.Register(); // P2: one text engine for the app, the CLI and the PDF appearances
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
