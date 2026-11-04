using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using PdfNotes.Text;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// The app under Avalonia.Headless: the real <see cref="App"/> (its resources and theme), Skia drawing, no windowing
/// system. One session for the test run; every test body runs on its UI thread.
/// </summary>
internal static class Headless
{
#pragma warning disable CA2255 // registered before any test runs
    [ModuleInitializer]
    internal static void Setup()
    {
        // Never the user's settings file.
        Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.Combine(Path.GetTempPath(), "PdfNotes-desktop-tests", Guid.NewGuid().ToString("N")));
        SkiaText.Register();
    }
#pragma warning restore CA2255

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(Headless)));

    public static void Run(Action test) => Session.Value.Dispatch(test, CancellationToken.None).GetAwaiter().GetResult();

    public static T Run<T>(Func<T> test) => Session.Value.Dispatch(test, CancellationToken.None).GetAwaiter().GetResult();

    public static void Run(Func<Task> test) => Session.Value.Dispatch(test, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Runs queued UI work until <paramref name="done"/> or the time is up (background renders post back here).</summary>
    public static void Pump(Func<bool> done, int timeoutMs = 10_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!done())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The UI did not settle.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The repository folder (sources the tests compare against).</summary>
    public static string Repository
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "PdfNotes.slnx"))) return dir.FullName;
            throw new DirectoryNotFoundException("PdfNotes.slnx not found above the test folder.");
        }
    }

    public static string TempFolder()
    {
        string path = Path.Combine(Path.GetTempPath(), "PdfNotes-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
