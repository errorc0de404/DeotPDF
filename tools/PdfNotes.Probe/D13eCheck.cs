using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.App;
using PdfNotes.App.Canvas;
using PdfNotes.App.Themes;
using PdfNotes.App.Tools;
using PdfNotes.App.Views;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Probe;

/// <summary>
/// D13e DPI and screen quality (no OS input: windows are not activated and sit outside the screen). Each scenario is a
/// common screen at a Windows scale, simulated inside the app: the window gets the work area of that screen in DIPs and
/// its root DPI is set to the scale (<see cref="VisualTreeHelper.SetRootDpi"/>), the dialogs get the work area through
/// <see cref="ScreenArea.Override"/>. Checks clipping of the ribbon, the settings bars and the status bar, the room left
/// for the document, page/thumbnail bitmaps at device pixels, the rotation cursor size and that dialogs fit the screen.
/// Real Windows scale changes and a second monitor stay in the user test list.
/// </summary>
internal static class D13eCheck
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static readonly List<string> written = [];
    private static readonly List<object> checks = [];
    private static readonly List<object> measures = [];
    private static int failed;
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => (DocumentView)window.FindName("View");

    /// <summary>Name, work area in DIPs (screen / scale minus a 48 px taskbar), scale.</summary>
    private static readonly (string Name, double Width, double Height, double Scale)[] Scenarios =
    [
        ("min-100", 720, 480, 1.0),
        ("1366x768-125", 1366 / 1.25, (768 - 48) / 1.25, 1.25),
        ("1280x800-150", 1280 / 1.5, (800 - 48) / 1.5, 1.5),
        ("1920x1080-150", 1920 / 1.5, (1080 - 48) / 1.5, 1.5),
        ("1920x1080-200", 1920 / 2.0, (1080 - 48) / 2.0, 2.0),
        ("3840x2160-175", 3840 / 1.75, (2160 - 48) / 1.75, 1.75),
        ("min-200", 720, 480, 2.0),
    ];

    public static int Run(string output)
    {
        folder = output;
        Directory.CreateDirectory(folder);
        Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.Combine(folder, "settings"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                Theme.SystemThemeOverride = () => AppTheme.Light;
                await RunScenarios();
                result = failed == 0 ? 0 : 1;
            }
            catch (Exception e) { Console.WriteLine(e); File.WriteAllText(Path.Combine(folder, "d13e-error.txt"), e.ToString()); }
            finally
            {
                ScreenArea.Override = null;
                File.WriteAllText(Path.Combine(folder, "d13e-result.json"), JsonSerializer.Serialize(new
                { Passed = result == 0, Input = "none (windows not activated, off screen, simulated DPI)", Checks = checks, Measures = measures, Files = written },
                    new JsonSerializerOptions { WriteIndented = true }));
                if (window is not null) { window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); }
                app.Shutdown();
            }
        };
        app.Run();
        return result;
    }

    private static async Task RunScenarios()
    {
        // The exe asks Windows for per-monitor DPI (v2); without it Windows stretches a 96-DPI bitmap on scaled monitors.
        var manifest = File.ReadAllBytes(typeof(MainWindow).Assembly.Location);
        var text = System.Text.Encoding.UTF8.GetString(manifest);
        Check("app manifest declares per-monitor v2 DPI awareness", text.Contains("PerMonitorV2") && text.Contains("true/pm"));
        Check("this process runs per-monitor v2 aware (same manifest)", AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), PerMonitorAwareV2));

        string pdf = Path.Combine(folder, "sample.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Left = SystemParameters.VirtualScreenLeft - 6000; window.Top = SystemParameters.VirtualScreenTop;
        window.Width = 1200; window.Height = 860;
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        window.MessageSink = _ => { };
        window.Show();
        await Settle(300);
        if (!await window.OpenFileAsync(pdf)) throw new InvalidOperationException("sample did not open");
        await Settle(600);
        var pageId = Session.Current.Pages[0].Id;
        var shape = new ShapeAnnotation(Guid.NewGuid(), false, Affine.Identity, ShapeKind.Rectangle, new(330, 120, 140, 80), [], ShapeStyle.Default);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, pageId, new InkStroke(Guid.NewGuid(), false, Affine.Identity,
            [.. Enumerable.Range(0, 30).Select(i => new Point2(80 + i * 6, 260 + 18 * Math.Sin(i / 4.0)))], 3, new(0xD0, 0x20, 0x20), 1)));
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, pageId, shape));

        foreach (var s in Scenarios) await Scenario(s.Name, s.Width, s.Height, s.Scale, shape);

        VisualTreeHelper.SetRootDpi(window, new DpiScale(1, 1));
        window.Width = 1200; window.Height = 860;
        await Settle(600);
        Check("back at 100 %: canvas and thumbnails return to 1 px per DIP", View.DpiScale == 1 && ThumbScale() is var t && Math.Abs(t - 1) < 0.02, $"thumb {ThumbScale():0.###}");
    }

    private static async Task Scenario(string name, double width, double height, double scale, ShapeAnnotation shape)
    {
        ScreenArea.Override = new Size(width, height);
        VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
        window.Width = Math.Max(width, window.MinWidth);
        window.Height = Math.Max(height, window.MinHeight);
        window.SetTool(ToolKind.Pen);
        window.ShowRibbonTab(RibbonTab.Home);
        await Settle(900);
        string at = $"{name} ({width:0}×{height:0} DIP, {scale * 100:0}%)";
        Check($"{at}: window fits the work area", window.ActualWidth <= width + 0.5 && window.ActualHeight <= height + 0.5 || width < window.MinWidth || height < window.MinHeight,
            $"{window.ActualWidth:0}×{window.ActualHeight:0}");
        Check($"{at}: canvas follows the scale", Math.Abs(View.DpiScale - scale) < 1e-9, View.DpiScale.ToString());

        // Page bitmaps at device pixels (sharp): the rendered base of a visible page has scale × its DIP width in pixels.
        await Settle(600);
        var page = View.RealizedPages.FirstOrDefault(v => v.BaseBitmap is not null);
        double pageRatio = page is null ? 0 : page.BaseBitmap!.PixelWidth / page.LocalTransform.PageWidthDip;
        Check($"{at}: page bitmap rendered at device pixels", page is not null && Math.Abs(pageRatio - scale) <= 0.02 * scale, $"{pageRatio:0.###} px/DIP");
        await WaitThumbs(scale);
        Check($"{at}: thumbnails rendered at device pixels", Math.Abs(ThumbScale() - scale) <= 0.02 * scale, $"{ThumbScale():0.###} px/DIP");
        Check($"{at}: rotation cursor is 32 px × scale", SelectionCursors.RotatePixels(scale) == (int)Math.Round(32 * scale)
            && ReferenceEquals(SelectionCursors.RotateFor(scale), SelectionCursors.RotateFor(scale)), SelectionCursors.RotatePixels(scale).ToString());

        // Ribbon: every tab and the tab row fit without clipping.
        foreach (var tab in Enum.GetValues<RibbonTab>())
        {
            window.ShowRibbonTab(tab);
            await Settle(150);
            var panel = (WrapPanel)window.FindName(tab + "Tab");
            var items = panel.Children.OfType<FrameworkElement>().Where(c => c.IsVisible).ToList();
            int rows = items.Select(c => Math.Round(LayoutInformation.GetLayoutSlot(c).Top)).Distinct().Count();
            double right = items.Max(c => c.TranslatePoint(new Point(c.ActualWidth, 0), panel).X);
            Check($"{at}: {tab} tab not clipped", right <= panel.ActualWidth + 0.5, $"{rows} rows, right {right:0.#} / {panel.ActualWidth:0.#}");
            measures.Add(new { Scenario = name, Part = $"ribbon {tab}", Rows = rows });
        }
        window.ShowRibbonTab(RibbonTab.Home);
        var gear = (FrameworkElement)window.FindName("SettingsButton");
        var lastTab = (FrameworkElement)window.FindName("ViewTabButton");
        double client = ((FrameworkElement)window.Content).ActualWidth;
        Check($"{at}: tab row fits", lastTab.TranslatePoint(new Point(lastTab.ActualWidth, 0), window).X <= gear.TranslatePoint(new Point(0, 0), window).X
            && gear.TranslatePoint(new Point(gear.ActualWidth, 0), window).X <= client + 0.5);

        // Settings bars: each tool's bar fits left of the favorites without clipping.
        var favorites = (FrameworkElement)window.FindName("FavoritesGroup");
        foreach (var (label, setup) in new (string, Action)[]
        {
            ("pen", () => window.SetTool(ToolKind.Pen)),
            ("highlight", () => window.SetTool(ToolKind.FreeHighlight)),
            ("eraser", () => window.SetTool(ToolKind.AreaEraser)),
            ("shape", () => { window.SetTool(ToolKind.Select); window.Selection.Select(shape.Id); }),
            ("text", () => { window.SetTool(ToolKind.Text); window.BeginNewText(0, new Point2(90, 420)); }),
        })
        {
            setup();
            await Settle(250);
            var bar = new[] { "StyleBar", "EraserBorder", "TextStyleBorder" }.Select(n => (Border)window.FindName(n)).Single(b => b.IsVisible);
            var wrap = (Panel)bar.Child;
            var parts = wrap.Children.OfType<FrameworkElement>().Where(c => c.IsVisible).ToList();
            double right = parts.Max(c => c.TranslatePoint(new Point(c.ActualWidth, 0), window).X);
            int rows = parts.Select(c => Math.Round(LayoutInformation.GetLayoutSlot(c).Top)).Distinct().Count();
            double limit = favorites.IsVisible ? favorites.TranslatePoint(new Point(0, 0), window).X : client;
            Check($"{at}: {label} bar not clipped and clear of the favorites", right <= limit + 0.5, $"{rows} rows, right {right:0.#} / {limit:0.#}");
            measures.Add(new { Scenario = name, Part = $"bar {label}", Rows = rows });
            if (label == "text")
            {
                // Choice boxes show their whole value (the default font name, every alignment) instead of cutting it off.
                foreach (var box in Descendants(wrap).OfType<ComboBox>().Where(b => b.IsVisible))
                {
                    var values = box.Items.Count <= 10 ? box.Items.OfType<string>().ToList() : [box.Text];
                    double room = box.ActualWidth - box.Padding.Left - box.Padding.Right - box.BorderThickness.Left - box.BorderThickness.Right - 4;
                    double widest = values.Max(v => new FormattedText(v, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch), box.FontSize, Brushes.Black, scale).WidthIncludingTrailingWhitespace);
                    Check($"{at}: text bar box shows '{string.Join("/", values)}' whole", widest <= room + 0.5, $"{widest:0.#} / {room:0.#} DIP");
                }
                window.FinishTextEditing(); await Settle();
            }
            if (label == "shape") window.Selection.Select(null);
            if (label == "pen") await Snapshot($"{name}-pen.png");
        }
        window.SetTool(ToolKind.Pen);
        await Settle(250);

        // Room for the document and an unclipped status bar.
        var scroller = (FrameworkElement)window.FindName("Scroller");
        double docShare = scroller.ActualHeight / ((FrameworkElement)window.Content).ActualHeight;
        measures.Add(new { Scenario = name, Part = "document area", Width = Math.Round(scroller.ActualWidth), Height = Math.Round(scroller.ActualHeight), Share = Math.Round(docShare, 2) });
        Check($"{at}: document area keeps at least half the window height", docShare >= 0.5, $"{scroller.ActualWidth:0}×{scroller.ActualHeight:0} DIP ({docShare:P0})");
        var zoom = (FrameworkElement)window.FindName("ZoomButton");
        Check($"{at}: status bar zoom button inside the window", zoom.IsVisible && zoom.TranslatePoint(new Point(zoom.ActualWidth, 0), window).X <= client + 0.5);
        var search = (FrameworkElement)window.FindName("SearchBar");
        window.OpenSearch();
        await Settle(300);
        double searchLeft = search.TranslatePoint(new Point(0, 0), scroller).X;
        Check($"{at}: search box fits the document area", searchLeft >= 0, $"left {searchLeft:0.#}");
        window.CloseSearch();

        // Dialogs open inside the (simulated) work area with their buttons on screen.
        var setupPage = Session.Current.Pages[0].Setup;
        await DialogFits(at, name, new SettingsDialog(new(true, false, new(true, 5, PdfNotes.Editor.Services.BackupMode.Folder, @"C:\Backups", 10)), () => true), width, height, scale);
        await DialogFits(at, name, new PageTransformDialog(true, setupPage, _ => [setupPage]), width, height, scale);
        await DialogFits(at, name, new NewNoteDialog(), width, height, scale);
        ScreenArea.Override = null;
    }

    private static async Task DialogFits(string at, string scenario, Window dialog, double width, double height, double scale)
    {
        dialog.ShowActivated = false; dialog.ShowInTaskbar = false; dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = SystemParameters.VirtualScreenLeft - 6000; dialog.Top = SystemParameters.VirtualScreenTop;
        dialog.Show();
        VisualTreeHelper.SetRootDpi(dialog, new DpiScale(scale, scale));
        await Settle(400);
        string kind = dialog.GetType().Name;
        Check($"{at}: {kind} fits the screen", dialog.ActualWidth <= width + 0.5 && dialog.ActualHeight <= height + 0.5, $"{dialog.ActualWidth:0}×{dialog.ActualHeight:0}");
        // Every button lies inside the dialog's client area (none cut off at the bottom/right).
        var root = (FrameworkElement)dialog.Content;
        var buttons = Descendants(root).OfType<Button>().Where(b => b.IsVisible && b.IsDefault | b.IsCancel).ToList();
        double worst = buttons.Count == 0 ? 0 : buttons.Max(b => Math.Max(b.TranslatePoint(new Point(b.ActualWidth, b.ActualHeight), root).Y - root.ActualHeight,
            b.TranslatePoint(new Point(b.ActualWidth, 0), root).X - root.ActualWidth));
        Check($"{at}: {kind} OK/Cancel buttons fully visible", buttons.Count >= 2 && worst <= 0.5, $"{buttons.Count} buttons, overflow {worst:0.#}");
        if (scenario is "1280x800-150" or "1920x1080-200") Save(Capture(dialog), $"{scenario}-{kind}.png");
        dialog.Close();
    }

    private static async Task WaitThumbs(double scale)
    {
        for (int i = 0; i < 40 && Math.Abs(ThumbScale() - scale) > 0.02 * scale; i++) await Settle(100);
    }

    private static double ThumbScale() =>
        window.PageThumbnails.FirstOrDefault()?.Image is BitmapSource b ? b.PixelWidth / window.PageThumbnails[0].ImageWidth : 0;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static readonly nint PerMonitorAwareV2 = -4;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AreDpiAwarenessContextsEqual(nint a, nint b);

    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail });
        if (!passed) failed++; // keep going: one run lists every clipped place
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
    }

    private static Task Settle(int ms = 250) => Task.Delay(ms);

    private static async Task Snapshot(string name)
    {
        await Settle(450);
        Save(Capture(window), name);
    }

    private static RenderTargetBitmap Capture(Window w)
    {
        double dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Round(w.ActualWidth * dpi), (int)Math.Round(w.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(w);
        return bitmap;
    }

    private static void Save(BitmapSource bitmap, string name)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
        written.Add(name);
    }
}
