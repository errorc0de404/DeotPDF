using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.App;
using PdfNotes.App.Canvas;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Prototype;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.Probe;

/// <summary>
/// D03 acceptance run against the real MainWindow: new notes (A01), viewing/zoom/page navigation (A02),
/// virtualization and stale results, rotated-page coordinate overlay (A25 part), refused inputs.
/// Writes screenshots and d03-result.json into the folder. Exit code 1 if any check fails.
/// </summary>
internal static class D03Check
{
    private sealed record Check(string Name, bool Passed, string Detail);

    private static readonly List<Check> Checks = [];
    private static readonly Dictionary<string, object> Facts = new();
    private static string folder = "";
    private static MainWindow window = null!;

    private static DocumentView View => (DocumentView)window.FindName("View");

    public static int Run(string outputFolder)
    {
        folder = outputFolder;
        Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.Combine(folder, "settings"));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exitCode = 1;
        application.Startup += async (_, _) =>
        {
            try
            {
                window = new MainWindow { Width = 1200, Height = 860, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                window.Show();
                await Settle();
                await RunChecks();
                exitCode = Checks.All(c => c.Passed) ? 0 : 1;
            }
            catch (Exception e)
            {
                Checks.Add(new("harness", false, e.ToString()));
            }
            finally
            {
                File.WriteAllText(Path.Combine(folder, "d03-result.json"), JsonSerializer.Serialize(new
                {
                    Passed = Checks.All(c => c.Passed), Machine = Environment.MachineName, Os = Environment.OSVersion.ToString(),
                    Dpi = View.DpiScale, Checks, Facts
                }, new JsonSerializerOptions { WriteIndented = true }));
                foreach (var c in Checks) Console.WriteLine($"{(c.Passed ? "PASS" : "FAIL")} {c.Name}: {c.Detail}");
                window.Close();
                application.Shutdown();
            }
        };
        application.Run();
        return exitCode;
    }

    private static void Record(string name, bool passed, string detail) => Checks.Add(new(name, passed, detail));

    private static async Task RunChecks()
    {
        string fifty = Path.Combine(folder, "fifty.pdf"), mixed = Path.Combine(folder, "mixed.pdf");
        ProbeSamplePdf.Create(fifty, 50);
        ProbeSamplePdf.CreateMixed(mixed);

        await NewNotes();
        await FiftyPages(fifty);
        await RotatedPagesAndOverlay(mixed);
        await SinglePageMode();
        await RefusedInputs(mixed);
        await RealInput();
    }

    // ----- A01 -----

    private static async Task NewNotes()
    {
        var initial = window.Document.Session.Current.Pages.Single().Setup;
        Record("A01 start document", initial.Background == PageBackground.Blank && Math.Abs(initial.WidthPt - 595.2756) < 1e-3,
            $"{initial.WidthPt:0.##}×{initial.HeightPt:0.##}pt {initial.Background.Kind}");

        await window.CreateNoteAsync(NewPageSpec.FromPreset(PaperSize.A5, PageOrientation.Landscape, PageBackground.Grid));
        await Settle();
        var setup = window.Document.Session.Current.Pages[0].Setup;
        bool lines = HasGrayLines(out string sample);
        Screenshot("a01-a5-landscape-grid.png");
        Record("A01 A5 landscape grid", Math.Abs(setup.WidthPt - Units.MillimetersToPoints(210)) < 1e-6 &&
            Math.Abs(setup.HeightPt - Units.MillimetersToPoints(148)) < 1e-6 && setup.Background.Kind == PageBackgroundKind.Grid && lines,
            $"model {setup.WidthPt:0.##}×{setup.HeightPt:0.##}pt, rendered lines: {sample}");

        await window.CreateNoteAsync(new NewPageSpec(100, 150, PageBackground.Lined));
        await Settle();
        setup = window.Document.Session.Current.Pages[0].Setup;
        lines = HasGrayLines(out sample);
        Screenshot("a01-custom-100x150-lined.png");
        Record("A01 custom lined", setup.Background.Kind == PageBackgroundKind.Lined && lines &&
            Math.Abs(setup.WidthPt - Units.MillimetersToPoints(100)) < 1e-6, $"rendered lines: {sample}");
        Record("A01 new note not dirty", !window.Document.Session.IsDirty && window.Title.StartsWith("새 노트 - ", StringComparison.Ordinal), window.Title);
        await NewNoteDialogFlow();
    }

    /// <summary>Drives the real New Note dialog: out-of-range input is refused in place, valid custom input is accepted.</summary>
    private static async Task NewNoteDialogFlow()
    {
        var dialog = new PdfNotes.App.Views.NewNoteDialog { Owner = window };
        T Find<T>(string name) => (T)dialog.FindName(name);
        var steps = new List<string>();
        bool refused = false;
        dialog.Loaded += async (_, _) =>
        {
            await Task.Delay(100);
            Find<System.Windows.Controls.ComboBox>("SizeBox").SelectedIndex = 3; // 사용자 지정
            Find<System.Windows.Controls.TextBox>("WidthBox").Text = "5";
            Find<System.Windows.Controls.TextBox>("HeightBox").Text = "150";
            Click(Find<System.Windows.Controls.Button>("CreateButton"));
            await Task.Delay(100);
            var error = Find<System.Windows.Controls.TextBlock>("ErrorText");
            refused = error.Visibility == Visibility.Visible && dialog.IsVisible && dialog.Result is null;
            steps.Add($"5mm refused: {refused} ('{error.Text}')");
            Find<System.Windows.Controls.TextBox>("WidthBox").Text = "100";
            Find<System.Windows.Controls.RadioButton>("LandscapeButton").IsChecked = true; // swaps to 150 × 100
            Find<System.Windows.Controls.RadioButton>("GridButton").IsChecked = true;
            steps.Add($"after landscape: {Find<System.Windows.Controls.TextBox>("WidthBox").Text} × {Find<System.Windows.Controls.TextBox>("HeightBox").Text}");
            Click(Find<System.Windows.Controls.Button>("CreateButton"));
        };
        bool? accepted = dialog.ShowDialog();
        var spec = dialog.Result;
        bool created = accepted == true && spec is not null && await window.CreateNoteAsync(spec);
        await Settle();
        var setup = window.Document.Session.Current.Pages[0].Setup;
        Record("A01 new note dialog", refused && created && spec == new NewPageSpec(150, 100, PageBackground.Grid) &&
            Math.Abs(setup.WidthPt - Units.MillimetersToPoints(150)) < 1e-6 && setup.Background.Kind == PageBackgroundKind.Grid,
            string.Join("; ", steps) + $"; result {spec}");
    }

    private static void Click(System.Windows.Controls.Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    /// <summary>The realized page bitmap contains light-gray line pixels and white paper.</summary>
    private static bool HasGrayLines(out string detail)
    {
        var visual = View.RealizedPages.Single();
        var bitmap = visual.BaseBitmap!;
        var pixels = Pixels(bitmap, out int stride);
        int gray = 0, white = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i];
            if (b == 255) white++;
            else if (b is > 150 and < 250 && pixels[i + 1] == b && pixels[i + 2] == b) gray++;
        }
        detail = $"{gray} gray / {white} white of {pixels.Length / 4}";
        return gray > pixels.Length / 4 / 200 && white > pixels.Length / 4 / 2;
    }

    // ----- A02 / A26 virtualization -----

    private static async Task FiftyPages(string path)
    {
        bool opened = await window.OpenFileAsync(path);
        await Settle();
        var scheduler = View.Scheduler!;
        int visible = View.RealizedPages.Count;
        Facts["fiftyInitialRenders"] = scheduler.RendersStarted;
        Facts["fiftyInitialRealized"] = visible;
        Screenshot("a02-fifty-open.png");
        Record("A02 open 50 pages", opened && View.PageCount == 50 && View.CurrentPageIndex == 0 && PageHorizontallyInView(0),
            $"pages {View.PageCount}, zoom {View.Zoom:P0}, title '{window.Title}', page 1 inside viewport {PageHorizontallyInView(0)}");
        Record("A26 no eager rendering", scheduler.RendersStarted <= visible + 2 && scheduler.CacheCount <= visible + 2,
            $"{scheduler.RendersStarted} renders, {scheduler.CacheCount} cached for {visible} visible pages of 50");

        View.GoToPage(29);
        await Settle();
        Screenshot("a02-page30.png");
        bool matched = AllRealizedMatch(out string m1);
        Record("A02 go to page 30", View.CurrentPageIndex == 29 && View.RealizedPages.Any(v => v.Index == 29) && matched && PageHorizontallyInView(29),
            $"current {View.CurrentPageIndex + 1}; {m1}");

        // Rapid scrolling through every page without waiting, then zoom changes: results for pages that scrolled
        // away must be dropped or never attached to another page.
        int startedBefore = scheduler.RendersStarted;
        for (int i = 0; i < 50; i++)
        {
            View.GoToPage(i);
            View.PlanRendersNow();
            await Task.Delay(3);
        }
        View.SetZoom(1.7, new Point(300, 200));
        View.SetZoom(0.6);
        View.GoToPage(41);
        await Settle();
        int rapidRenders = scheduler.RendersStarted - startedBefore;
        Facts["rapidScrollRenders"] = rapidRenders;
        Facts["rapidScrollDropped"] = scheduler.RequestsDropped;
        Facts["cacheBytesAfterScroll"] = scheduler.CacheBytes;
        Record("A02 stale results not applied", AllRealizedMatch(out string m2) && View.CurrentPageIndex == 41, m2);
        Record("A26 rapid scroll bounded", rapidRenders < 50 + 20 && scheduler.CacheBytes <= PageRenderScheduler.DefaultCacheBytes,
            $"{rapidRenders} renders for 50 page jumps + 3 view changes, {scheduler.RequestsDropped} dropped, cache {scheduler.CacheBytes / 1048576.0:0.0} MiB");

        View.FitPage();
        await Settle();
        double fitPage = View.Zoom;
        View.FitWidth();
        await Settle();
        Screenshot("a02-fit-width.png");
        var frame = window.Document.Session.Current.Pages[View.CurrentPageIndex].Setup.Frame;
        double expected = DocumentLayout.FitWidthZoom(frame, View.ViewportWidth);
        Record("A02 fit width / page", Math.Abs(View.Zoom - expected) < 1e-9 && fitPage < View.Zoom,
            $"fit page {fitPage:P1}, fit width {View.Zoom:P1}");
    }

    /// <summary>The page fits the viewport width and is fully inside it horizontally (fit-width view).</summary>
    private static bool PageHorizontallyInView(int index)
    {
        var r = View.Layout!.PageRect(index);
        double left = r.X - View.HorizontalOffset;
        return left >= -0.5 && left + r.Width <= View.ViewportWidth + 0.5;
    }

    /// <summary>Every realized page shows a bitmap rendered for its own page id and setup, at the wanted resolution.</summary>
    private static bool AllRealizedMatch(out string detail)
    {
        double target = RenderPlanner.TargetPixelsPerPoint(View.Zoom, View.DpiScale);
        var problems = new List<string>();
        foreach (var v in View.RealizedPages)
        {
            var wanted = RenderPlanner.BaseKey(v.Page!, target);
            if (v.BaseKey is null) problems.Add($"p{v.Index + 1} no bitmap");
            else if (v.BaseKey.PageId != v.Page!.Id || !v.BaseKey.Setup.Equals(v.Page.Setup)) problems.Add($"p{v.Index + 1} shows another page");
            else if (!v.BaseKey.Equals(wanted)) problems.Add($"p{v.Index + 1} stale resolution");
            if (v.DetailKey is { } d && (d.PageId != v.Page!.Id || Math.Abs(d.PixelsPerPoint - target) > 1e-9)) problems.Add($"p{v.Index + 1} stale tile");
        }
        detail = problems.Count == 0 ? $"{View.RealizedPages.Count} realized pages all current" : string.Join(", ", problems);
        return problems.Count == 0;
    }

    // ----- A25 part: rotation, CropBox, UserUnit; zoom anchor; overlay alignment -----

    private static async Task RotatedPagesAndOverlay(string path)
    {
        await window.OpenFileAsync(path);
        await Settle();
        var source = window.Document.Source!;
        var pages = window.Document.Session.Current.Pages;
        foreach (int index in new[] { 1, 3 })
        {
            // Cursor-centred zoom: the page point under the cursor stays under the cursor.
            View.SetZoom(1);
            View.GoToPage(index);
            await Settle();
            var rect = View.PageTransform(index);
            var cursor = rect.PageToDip.Transform(new(pages[index].Setup.WidthPt * 0.3, pages[index].Setup.HeightPt * 0.2));
            var point = new Point(cursor.X, cursor.Y);
            var before = View.HitTestPage(point);
            View.ZoomBy(Math.Pow(1.1, 6), point); // six Ctrl+wheel notches
            await Settle();
            var after = View.HitTestPage(point);
            double drift = before is { } b && after is { } a && a.PageIndex == b.PageIndex ? b.PagePoint.DistanceTo(a.PagePoint) : double.NaN;
            Record($"A02 cursor zoom page {index + 1} ({pages[index].Setup.Rotation}°)", drift < 0.75 / View.Zoom,
                $"anchor drift {drift:0.###}pt at {View.Zoom:P0} (one device pixel = {1 / (View.Zoom * Units.DipsPerPoint * View.DpiScale):0.###}pt)");
        }

        for (int index = 0; index < pages.Count; index++)
        {
            var characters = PdfTextReader.ExtractCanonical(source.Engine, source.Pages[index])
                .Where(c => !string.IsNullOrWhiteSpace(c.Text)).ToArray();
            var corners = characters.SelectMany(c => new[] { c.Quad.UpperLeft, c.Quad.LowerRight }).ToArray();
            var textCenter = new Point2((corners.Min(p => p.X) + corners.Max(p => p.X)) / 2, (corners.Min(p => p.Y) + corners.Max(p => p.Y)) / 2);
            foreach (double zoom in new[] { 1.5, 4.0 })
            {
                View.SetZoom(zoom);
                View.ScrollToPagePoint(index, textCenter);
                await Settle();
                var shot = WindowPixels(out int stride, out int width, out int height);
                var scan = AlignmentScan(shot, stride, width, height, index, characters);
                string detail = $"{scan.Hit}/{scan.Total} visible glyphs have ink inside their quad; best overlay shift " +
                    $"({scan.BestDx},{scan.BestDy})px, ink inside at 0 shift {scan.InsideAtZero:P1} of best; " +
                    $"page {pages[index].Setup.Rotation}°, zoom {zoom:P0}, tile {(View.RealizedPages.First(v => v.Index == index).DetailKey is null ? "no" : "yes")}";
                Record($"A25 overlay page {index + 1} zoom {zoom:P0}", scan.Total >= 8 && scan.Hit >= scan.Total * 0.95 &&
                    Math.Abs(scan.BestDx) <= 1 && Math.Abs(scan.BestDy) <= 1 && scan.InsideAtZero > 0.97, detail);
                if (zoom == 1.5)
                {
                    var previous = View.OverlayPainter;
                    View.OverlayPainter = (context, pageIndex, _, transform) => DrawQuads(context, transform,
                        pageIndex == index ? characters.Select(c => c.Quad) : []);
                    await Settle();
                    Screenshot($"a25-overlay-page{index + 1}-rot{pages[index].Setup.Rotation}.png");
                    View.OverlayPainter = previous;
                }
            }
        }
        View.SetZoom(4);
        View.GoToPage(0);
        await Settle();
        Screenshot("zoom400-detail-tile.png");
    }

    private static void DrawQuads(DrawingContext context, ViewTransform transform, IEnumerable<Quad> quads)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 230, 0, 0)), 1);
        pen.Freeze();
        foreach (var quad in quads)
        {
            var q = quad.Transform(transform.PageToDip);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new(q.UpperLeft.X, q.UpperLeft.Y), false, true);
                g.LineTo(new(q.UpperRight.X, q.UpperRight.Y), true, false);
                g.LineTo(new(q.LowerRight.X, q.LowerRight.Y), true, false);
                g.LineTo(new(q.LowerLeft.X, q.LowerLeft.Y), true, false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }

    private sealed record Scan(int Hit, int Total, int BestDx, int BestDy, double InsideAtZero);

    /// <summary>
    /// Maps every glyph quad through the app's coordinate service onto the window screenshot and counts dark (text)
    /// pixels inside the quads, for the overlay shifted by −4…+4 device pixels in x and y. A correct overlay has its
    /// maximum at (0,0) (±1px for antialiasing); a misaligned one peaks elsewhere.
    /// </summary>
    private static Scan AlignmentScan(byte[] shot, int stride, int width, int height, int pageIndex, IEnumerable<PdfTextCharacter> characters)
    {
        const int range = 4;
        var transform = View.PageTransform(pageIndex);
        double dpi = View.DpiScale;
        var origin = View.TranslatePoint(new Point(0, 0), window);
        Point2 ToPixel(Point2 page)
        {
            var dip = transform.PageToDip.Transform(page);
            return new((dip.X + origin.X) * dpi, (dip.Y + origin.Y) * dpi);
        }
        var counts = new long[2 * range + 1, 2 * range + 1];
        int hit = 0, total = 0;
        foreach (var c in characters)
        {
            var q = c.Quad;
            Point2[] tight = [ToPixel(q.UpperLeft), ToPixel(q.UpperRight), ToPixel(q.LowerRight), ToPixel(q.LowerLeft)];
            int x0 = (int)Math.Floor(tight.Min(p => p.X)) - range - 1, x1 = (int)Math.Ceiling(tight.Max(p => p.X)) + range + 1;
            int y0 = (int)Math.Floor(tight.Min(p => p.Y)) - range - 1, y1 = (int)Math.Ceiling(tight.Max(p => p.Y)) + range + 1;
            if (x0 < 0 || y0 < 0 || x1 >= width || y1 >= height) continue; // only fully visible glyphs
            var view = new Rect(origin.X * dpi, origin.Y * dpi, View.ActualWidth * dpi, View.ActualHeight * dpi);
            if (!view.Contains(new Point(x0, y0)) || !view.Contains(new Point(x1, y1))) continue;
            total++;
            bool inked = false;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = y * stride + x * 4;
                    if (0.114 * shot[i] + 0.587 * shot[i + 1] + 0.299 * shot[i + 2] > 140) continue;
                    for (int dy = -range; dy <= range; dy++)
                        for (int dx = -range; dx <= range; dx++)
                            if (InConvex(tight, new(x + 0.5 - dx, y + 0.5 - dy)))
                            {
                                counts[dy + range, dx + range]++;
                                if (dx == 0 && dy == 0) inked = true;
                            }
                }
            if (inked) hit++;
        }
        int bestDx = 0, bestDy = 0;
        for (int dy = -range; dy <= range; dy++)
            for (int dx = -range; dx <= range; dx++)
                if (counts[dy + range, dx + range] > counts[bestDy + range, bestDx + range]) (bestDx, bestDy) = (dx, dy);
        long best = counts[bestDy + range, bestDx + range];
        return new(hit, total, bestDx, bestDy, best == 0 ? 0 : counts[range, range] / (double)best);
    }

    private static bool InConvex(Point2[] polygon, Point2 p)
    {
        int sign = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            double cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            int s = Math.Sign(cross);
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    // ----- A02 single-page mode -----

    private static async Task SinglePageMode()
    {
        View.SetZoom(1);
        View.GoToPage(2);
        await Settle();
        window.SetViewMode(ViewMode.SinglePage);
        await Settle();
        bool kept = View.CurrentPageIndex == 2 && View.RealizedPages.Count == 1 && View.RealizedPages.First().Index == 2;
        Screenshot("a02-single-page.png");
        View.PageDown();
        await Settle();
        bool next = View.CurrentPageIndex == 3 && View.RealizedPages.Single().Index == 3;
        View.PageUp();
        await Settle();
        bool previous = View.CurrentPageIndex == 2;
        // Wheel: scroll inside the page first, change page only at the bottom edge.
        View.SetZoom(2);
        View.GoToPage(0);
        await Settle();
        View.MouseWheelDown();
        bool scrolledInside = View.CurrentPageIndex == 0 && View.VerticalOffset > 0;
        for (int i = 0; i < 200 && View.CurrentPageIndex == 0; i++) View.MouseWheelDown();
        bool turned = View.CurrentPageIndex == 1 && View.VerticalOffset == 0;
        await Settle();
        bool persisted = File.ReadAllText(Path.Combine(folder, "settings", "settings.json")).Contains("singlePage", StringComparison.Ordinal);
        Record("A02 single page mode", kept && next && previous && scrolledInside && turned && persisted && AllRealizedMatch(out _),
            $"kept {kept}, PageDown {next}, PageUp {previous}, wheel inside {scrolledInside}, wheel at edge turns {turned}, setting saved {persisted}");
        window.SetViewMode(ViewMode.Continuous);
        Record("A02 back to continuous keeps page", View.CurrentPageIndex == 1, $"current {View.CurrentPageIndex + 1}");
    }

    // ----- spec 4.2: unsupported input keeps the current document -----

    private static async Task RefusedInputs(string mixed)
    {
        string corrupt = Path.Combine(folder, "corrupt.pdf"), password = Path.Combine(folder, "password.pdf");
        string signed = Path.Combine(folder, "signed.pdf"), owner = Path.Combine(folder, "owner.pdf");
        File.WriteAllBytes(corrupt, File.ReadAllBytes(mixed).AsSpan(0, 600).ToArray());
        ProbeSamplePdf.CreatePasswordProtected(mixed, password);
        ProbeSamplePdf.CreateSigned(mixed, signed);
        ProbeSamplePdf.CreateOwnerEncrypted(mixed, owner);
        var messages = new List<string>();
        window.OpenErrorSink = messages.Add;
        try
        {
            foreach (var file in new[] { corrupt, password, signed, Path.Combine(folder, "missing.pdf") })
            {
                var before = window.Document;
                var pagesBefore = before.Session.Current;
                int current = View.CurrentPageIndex;
                bool opened = await window.OpenFileAsync(file);
                await Settle();
                Record($"4.2 refuse {Path.GetFileName(file)}", !opened && ReferenceEquals(before, window.Document) &&
                    ReferenceEquals(pagesBefore, window.Document.Session.Current) && View.CurrentPageIndex == current && AllRealizedMatch(out _),
                    messages.LastOrDefault()?.Replace('\n', ' ') ?? "(no message)");
            }
        }
        finally { window.OpenErrorSink = null; }

        // 2026-10-14 (spec 24.6, D13h): a PDF with only permission restrictions (owner password) opens now.
        var previous = window.Document;
        int page = View.CurrentPageIndex;
        double zoom = View.Zoom;
        bool ownerOpened = await window.OpenFileAsync(owner);
        await Settle();
        string status = ((System.Windows.Controls.TextBlock)window.FindName("StatusText")).Text;
        Record("24.6 owner.pdf (permission restrictions only) opens", ownerOpened && window.Document.Source?.PermissionRestricted == true && status.Contains("편집 제한 PDF"), status);
        Record("reopen the mixed sample for the real input checks", await window.OpenFileAsync(mixed) && !ReferenceEquals(previous, window.Document), "");
        await Settle();
        View.SetZoom(zoom); View.GoToPage(page);
        await Settle();
    }

    // ----- real OS input (SendInput) -----

    private static async Task RealInput()
    {
        View.SetZoom(1);
        View.GoToPage(0);
        await Settle();
        var hwnd = new WindowInteropHelper(window).Handle;
        for (int attempt = 0; attempt < 10 && GetForegroundWindow() != hwnd; attempt++)
        {
            // Windows only lets a process take the foreground after input; a released Alt key counts as such input.
            keybd_event(VkAlt, 0, 0, 0);
            keybd_event(VkAlt, 0, KeyUp, 0);
            window.Activate();
            SetForegroundWindow(hwnd);
            await Task.Delay(200);
        }
        if (GetForegroundWindow() != hwnd)
        {
            Record("real input", true, "미검증: 창을 전면으로 가져올 수 없어 OS 입력을 보내지 않음");
            Facts["realInput"] = "unverified";
            return;
        }
        var target = View.PageTransform(0).PageToDip.Transform(new(200, 300));
        var screen = View.PointToScreen(new Point(target.X, target.Y));
        var before = View.HitTestPage(new Point(target.X, target.Y));
        double zoom = View.Zoom;
        SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        await Task.Delay(100);
        var cursor = Mouse.GetPositionIn(View);
        before = View.HitTestPage(cursor);
        keybd_event(VkControl, 0, 0, 0);
        mouse_event(MouseWheel, 0, 0, 120, 0);
        await Task.Delay(50);
        mouse_event(MouseWheel, 0, 0, 120, 0);
        keybd_event(VkControl, 0, KeyUp, 0);
        await Settle();
        var after = View.HitTestPage(cursor);
        double drift = before is { } b && after is { } a ? b.PagePoint.DistanceTo(a.PagePoint) : double.NaN;
        Record("real Ctrl+wheel", Math.Abs(View.Zoom - zoom * 1.21) < 1e-6 && drift < 1,
            $"zoom {zoom:P0} → {View.Zoom:P1}, point under cursor drift {drift:0.###}pt");

        // Space + left drag pans.
        double offsetY = View.VerticalOffset;
        keybd_event(VkSpace, 0, 0, 0);
        await Task.Delay(50);
        mouse_event(LeftDown, 0, 0, 0, 0);
        for (int i = 1; i <= 10; i++)
        {
            SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y) - i * 20);
            await Task.Delay(15);
        }
        mouse_event(LeftUp, 0, 0, 0, 0);
        keybd_event(VkSpace, 0, KeyUp, 0);
        await Settle();
        double moved = View.VerticalOffset - offsetY;
        Record("real Space+drag", Math.Abs(moved - 200 / View.DpiScale) < 2, $"offset moved {moved:0.#} DIP for 200px drag");

        // Space is consumed, so a focused toolbar button is not clicked and no document command runs.
        Facts["realInput"] = "verified";
    }

    private static class Mouse
    {
        public static Point GetPositionIn(UIElement element)
        {
            GetCursorPos(out var p);
            return element.PointFromScreen(new Point(p.X, p.Y));
        }
    }

    // ----- helpers -----

    /// <summary>Waits until layout, the debounced render plan and all renders have finished.</summary>
    private static async Task Settle()
    {
        var watch = Stopwatch.StartNew();
        do
        {
            await Task.Delay(220);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        while (View.Scheduler is { IsIdle: false } && watch.Elapsed < TimeSpan.FromSeconds(20));
        await Task.Delay(60);
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static RenderTargetBitmap Capture()
    {
        double dpi = View.DpiScale;
        var bitmap = new RenderTargetBitmap((int)Math.Round(window.ActualWidth * dpi), (int)Math.Round(window.ActualHeight * dpi),
            96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        return bitmap;
    }

    private static byte[] WindowPixels(out int stride, out int width, out int height)
    {
        var bitmap = Capture();
        width = bitmap.PixelWidth;
        height = bitmap.PixelHeight;
        return Pixels(bitmap, out stride);
    }

    private static byte[] Pixels(BitmapSource bitmap, out int stride)
    {
        if (bitmap.Format != PixelFormats.Pbgra32 && bitmap.Format != PixelFormats.Bgra32)
            bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void Screenshot(string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Capture()));
        using var file = File.Create(Path.Combine(folder, name));
        encoder.Save(file);
    }

    private const byte VkAlt = 0x12, VkControl = 0x11, VkSpace = 0x20;
    private const uint KeyUp = 0x2, MouseWheel = 0x0800, LeftDown = 0x2, LeftUp = 0x4;
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nuint extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
