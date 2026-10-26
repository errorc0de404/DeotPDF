using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PdfNotes.App;
using PdfNotes.App.Canvas;
using PdfNotes.App.Themes;
using PdfNotes.App.Tools;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfNotes.Probe;

/// <summary>
/// D13f <c>d13f</c> (spec 16.1/16.2, stage doc 18 "구현 작업" 1–5): performance and memory measurement without OS input.
/// The Release publish is started as separate processes for the startup time (its window appears on screen, no input is
/// sent); everything else runs in process on a MainWindow that is not activated and stays off screen. Tools get their
/// pointer calls directly, scrolling and zooming go through DocumentView, frames are counted with
/// CompositionTarget.Rendering. Writes d13f-result.json and d13f-table.md. D13F_QUICK=1 reduces repetitions (smoke only).
/// </summary>
internal static class D13fCheck
{
    private const double MB = 1024.0 * 1024.0;
    private static readonly bool Quick = Environment.GetEnvironmentVariable("D13F_QUICK") == "1";
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string folder = "", samples = "", work = "";
    private static MainWindow window = null!;
    private static readonly Dictionary<string, object?> result = new();
    private static readonly Dictionary<string, double?> summary = new();
    private static readonly List<object> errors = [];
    private static readonly List<string> messages = [];
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => ViewOf(window);
    private static DocumentView ViewOf(MainWindow w) => (DocumentView)w.FindName("View");

    // samples
    private static string normal50 = "", normal50b = "", scanned50 = "", images20 = "", mixed = "", small = "", complex = "", notes50 = "";

    public static int Run(string output)
    {
        folder = output;
        samples = Path.Combine(folder, "samples");
        work = Path.Combine(folder, "work");
        Directory.CreateDirectory(samples);
        Directory.CreateDirectory(work);
        Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.Combine(folder, "settings"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int code = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                Theme.SystemThemeOverride = () => AppTheme.Light;
                result["Input"] = "none for the in-process part (window not activated, off screen, tool pointer methods called directly); the startup runs show the published app's window on screen without input";
                result["Quick"] = Quick;
                result["StartedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                await Section("Machine", () => { result["Machine"] = Machine(); return Task.CompletedTask; });
                await Section("Samples", () => { result["Samples"] = CreateSamples(); return Task.CompletedTask; });
                await Section("Startup", async () => result["Startup"] = await Task.Run(Startup));
                CompositionTarget.Rendering += OnRendering;
                await Section("InProcessWindow", CreateWindow);
                await Section("FrameBaseline", async () => result["FrameBaseline"] = await FrameBaseline());
                await Section("FirstPage", FirstPages);
                await Section("Pen", Pen);
                await Section("FirstPageComplex", async () => result["FirstPageComplex"] = await FirstPageSeries(complex, "complex-1page-1000strokes", Quick ? 1 : 3));
                await Section("Scroll", Scroll);
                await Section("Zoom", Zoom);
                await Section("Cache", async () => result["Cache"] = await CacheSnapshot());
                await Section("SaveExportSearchMerge", SaveExportSearchMerge);
                await Section("FirstPageNotes", async () => result["FirstPageNotes"] = await FirstPageSeries(notes50, "normal50-with-1000-strokes", Quick ? 1 : 3));
                await Section("OpenCloseCycles", OpenCloseCycles);
                await Section("WindowCycles", WindowCycles);
                code = errors.Count == 0 ? 0 : 1;
            }
            catch (Exception e) { errors.Add(new { Section = "harness", Error = e.ToString() }); }
            finally
            {
                CompositionTarget.Rendering -= OnRendering;
                result["FinishedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                result["TotalSeconds"] = Math.Round(Clock.Elapsed.TotalSeconds, 1);
                result["Summary"] = summary;
                result["Errors"] = errors;
                result["Messages"] = messages;
                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                try { File.WriteAllText(Path.Combine(folder, "d13f-result.json"), JsonSerializer.Serialize(result, options)); }
                catch (Exception e) { File.WriteAllText(Path.Combine(folder, "d13f-error.txt"), e.ToString()); }
                try { File.WriteAllText(Path.Combine(folder, "d13f-table.md"), Table()); }
                catch (Exception e) { File.AppendAllText(Path.Combine(folder, "d13f-error.txt"), e.ToString()); }
                if (window is not null) { window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); }
                app.Shutdown();
            }
        };
        app.Run();
        Console.WriteLine($"d13f finished in {Clock.Elapsed.TotalSeconds:0}s, {errors.Count} section error(s).");
        return code;
    }

    private static async Task Section(string name, Func<Task> body)
    {
        double start = Now();
        Console.WriteLine($"[{Clock.Elapsed.TotalSeconds:0}s] {name}");
        try { await body(); }
        catch (Exception e) { errors.Add(new { Section = name, Error = e.ToString() }); Console.WriteLine($"ERROR {name}: {e.Message}"); }
        result[name + "SectionSeconds"] = Math.Round((Now() - start) / 1000, 1);
    }

    // ===================== machine =====================

    private static object Machine()
    {
        string cpu = ReadRegistry(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? "unknown";
        var gpus = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is not null)
                foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
                    try { using var k = cls.OpenSubKey(sub); if (k?.GetValue("DriverDesc") is string d) gpus.Add(d); } catch (Exception) { }
        }
        catch (Exception) { }
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        int refresh = EnumDisplaySettings(null, -1, ref mode) ? mode.dmDisplayFrequency : -1;
        string root = Path.GetPathRoot(folder) ?? "C:\\";
        object? disk = null;
        try { var d = new DriveInfo(root); disk = new { Root = root, d.DriveFormat, TotalGB = Math.Round(d.TotalSize / MB / 1024, 1), FreeGB = Math.Round(d.AvailableFreeSpace / MB / 1024, 1) }; }
        catch (Exception) { }
        string repo = RepoRoot();
        string exe = AppExe();
        var appAssembly = typeof(MainWindow).Assembly;
        var debuggable = appAssembly.GetCustomAttribute<DebuggableAttribute>();
        DateTime? newestSource = null;
        try
        {
            newestSource = Directory.EnumerateFiles(Path.Combine(repo, "src"), "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Max(File.GetLastWriteTime);
        }
        catch (Exception) { }
        string publishedDll = Path.Combine(Path.GetDirectoryName(exe) ?? "", "DeotPDF.dll");
        DateTime? published = File.Exists(publishedDll) ? File.GetLastWriteTime(publishedDll) : null;
        return new
        {
            Cpu = cpu.Trim(),
            LogicalProcessors = Environment.ProcessorCount,
            RamGB = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / MB / 1024, 1),
            Gpu = gpus,
            WpfRenderTier = RenderCapability.Tier >> 16,
            Os = RuntimeInformation.OSDescription,
            OsVersion = Environment.OSVersion.VersionString,
            Framework = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            PrimaryScreenDip = $"{SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight}",
            VirtualScreenDip = $"{SystemParameters.VirtualScreenWidth}x{SystemParameters.VirtualScreenHeight}",
            DisplayRefreshHz = refresh,
            DisplayPixels = refresh > 0 ? $"{mode.dmPelsWidth}x{mode.dmPelsHeight}" : null,
            Disk = disk,
            InProcessBuild = new { Assembly = appAssembly.Location, JitOptimizerDisabled = debuggable?.IsJITOptimizerDisabled ?? false, Note = "probe built -c Release" },
            PublishedExe = exe,
            PublishedExeExists = File.Exists(exe),
            PublishedAppDllTime = published?.ToString("yyyy-MM-dd HH:mm:ss"),
            NewestSourceTime = newestSource?.ToString("yyyy-MM-dd HH:mm:ss"),
            PublishOlderThanSource = published is { } p && newestSource is { } s && p < s,
        };
    }

    private static string? ReadRegistry(string key, string value)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(key); return k?.GetValue(value)?.ToString(); }
        catch (Exception) { return null; }
    }

    private static string RepoRoot([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    private static string AppExe() => Environment.GetEnvironmentVariable("D13F_APP_EXE") is { Length: > 0 } e ? e
        : Path.Combine(RepoRoot(), "artifacts", "publish", "win-x64", "DeotPDF.exe");

    // ===================== samples =====================

    private static object CreateSamples()
    {
        var list = new List<object>();
        double t = Now();
        small = Path.Combine(samples, "small-1page.pdf");
        ProbeSamplePdf.Create(small, 1); // also installs the sample font resolver used below ("D01-Malgun")
        list.Add(Describe(small, "small", "ProbeSamplePdf.Create(1): switch target between measurements", t));

        t = Now(); normal50 = Path.Combine(samples, "normal-50.pdf"); CreateText(normal50, 50, "A");
        list.Add(Describe(normal50, "normal50", "A4, 50 pages, 45 lines of Korean/English text + small vector chart per page (embedded Malgun Gothic subset)", t));
        t = Now(); normal50b = Path.Combine(samples, "normal-50b.pdf"); CreateText(normal50b, 50, "B");
        list.Add(Describe(normal50b, "normal50b", "second text sample (merge partner)", t));

        t = Now(); scanned50 = Path.Combine(samples, "scanned-50.pdf"); var scan = CreateScanned(scanned50, Quick ? 10 : 50);
        list.Add(Describe(scanned50, "scanned50", $"Letter, one full-page grayscale JPEG per page, {scan} (300 dpi), no text layer", t));

        t = Now(); images20 = Path.Combine(samples, "images-20.pdf"); var img = CreateImages(images20, Quick ? 4 : 20);
        list.Add(Describe(images20, "images20", img, t));

        t = Now(); mixed = Path.Combine(samples, "mixed-rotation.pdf"); ProbeSamplePdf.CreateMixed(mixed, 9);
        list.Add(Describe(mixed, "mixedRotation", "ProbeSamplePdf.CreateMixed(repeat 9): A4/Letter/A5/landscape/1000x1400, /Rotate 0/90/180/270, CropBox offsets, one UserUnit 1.5 page", t));
        complex = Path.Combine(samples, "complex-1page-notes.pdf");
        notes50 = Path.Combine(samples, "normal-50-notes.pdf");
        return list;
    }

    private static object Describe(string path, string name, string content, double startMs)
    {
        int pages; using (var d = new PdfiumDocument(path)) pages = d.PageCount;
        return new { Name = name, File = Path.GetFileName(path), Bytes = new FileInfo(path).Length, MB = Math.Round(new FileInfo(path).Length / MB, 2), Pages = pages, Annotations = 0, Content = content, GenerateMs = R(Now() - startMs) };
    }

    private static void CreateText(string path, int pageCount, string tag)
    {
        using var doc = new PdfDocument();
        var body = new XFont("D01-Malgun", 10.5, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
        var head = new XFont("D01-Malgun", 18, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
        var pen = new XPen(XColors.SteelBlue, 0.8);
        for (int p = 0; p < pageCount; p++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(595.28);
            page.Height = XUnit.FromPoint(841.89);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"{tag} 성능 측정 문서 — {p + 1}쪽 / Performance sample page {p + 1}", head, XBrushes.Black, new XPoint(56, 64));
            for (int line = 0; line < 45; line++)
                gfx.DrawString($"{line + 1:00}. PdfNotes 성능 측정 sample 문장입니다. The quick brown fox jumps over the lazy dog {p + 1}-{line + 1}.",
                    body, XBrushes.Black, new XPoint(56, 96 + line * 15));
            double x0 = 56, y0 = 790;
            gfx.DrawRectangle(pen, x0, y0 - 90, 480, 90);
            for (int i = 0; i < 24; i++)
            {
                double h = 10 + (i * 37 + p * 11) % 70;
                gfx.DrawRectangle(XBrushes.LightSteelBlue, x0 + 6 + i * 19.5, y0 - h, 14, h);
                gfx.DrawLine(pen, x0 + i * 20, y0 - 45 + 20 * Math.Sin(i + p), x0 + (i + 1) * 20, y0 - 45 + 20 * Math.Sin(i + 1 + p));
            }
        }
        doc.Save(path);
    }

    /// <summary>Scanned-like pages: a 2550x3300 (300 dpi Letter) grayscale JPEG with paper shading, text-like blocks and speckles.</summary>
    private static string CreateScanned(string path, int pageCount)
    {
        const int w = 2550, h = 3300;
        long jpegBytes = 0;
        using var doc = new PdfDocument();
        var px = new byte[w * h];
        for (int p = 0; p < pageCount; p++)
        {
            var rnd = new Random(1000 + p);
            for (int y = 0; y < h; y++)
            {
                byte shade = (byte)(226 + 8 * Math.Sin(y * 0.002 + p) + 4 * Math.Sin(y * 0.013));
                px.AsSpan(y * w, w).Fill(shade);
            }
            for (int line = 0; line < 56; line++)
            {
                int y0 = 300 + line * 50, x = 240 + rnd.Next(0, 30);
                while (x < w - 260)
                {
                    int word = rnd.Next(60, 260);
                    for (int gx = x; gx < Math.Min(x + word, w - 240); gx++)
                    {
                        if ((gx - x) % 13 >= 9) continue; // letter gaps
                        int top = y0 + ((gx * 7 + line) % 5), bottom = y0 + 26 - ((gx * 3) % 4);
                        byte ink = (byte)(40 + (gx + line) % 30);
                        for (int gy = top; gy < bottom; gy++) px[gy * w + gx] = ink;
                    }
                    x += word + rnd.Next(18, 34);
                }
            }
            for (int s = 0; s < 4000; s++) px[rnd.Next(0, h) * w + rnd.Next(0, w)] = (byte)rnd.Next(60, 160);
            var bitmap = BitmapSource.Create(w, h, 300, 300, PixelFormats.Gray8, null, px, w);
            var encoder = new JpegBitmapEncoder { QualityLevel = 75 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var jpeg = new MemoryStream();
            encoder.Save(jpeg);
            jpegBytes += jpeg.Length;
            jpeg.Position = 0;
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            using var gfx = XGraphics.FromPdfPage(page);
            using var image = XImage.FromStream(jpeg);
            gfx.DrawImage(image, 0, 0, 612, 792);
        }
        doc.Save(path);
        return $"{pageCount} pages, {w}x{h} px Gray8 JPEG q75, mean {jpegBytes / Math.Max(1, pageCount) / 1024} KiB/page";
    }

    /// <summary>Image-heavy pages: four 1600x1200 color JPEGs and one 1000x750 PNG with alpha per A4 page.</summary>
    private static string CreateImages(string path, int pageCount)
    {
        using var doc = new PdfDocument();
        var sin = Enumerable.Range(0, 1024).Select(i => Math.Sin(i * 2 * Math.PI / 1024)).ToArray();
        for (int p = 0; p < pageCount; p++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(595.28);
            page.Height = XUnit.FromPoint(841.89);
            using var gfx = XGraphics.FromPdfPage(page);
            for (int k = 0; k < 4; k++)
            {
                using var stream = Photo(1600, 1200, p * 4 + k, sin, png: false);
                using var image = XImage.FromStream(stream);
                gfx.DrawImage(image, 30 + (k % 2) * 272, 40 + (k / 2) * 214, 262, 196.5);
            }
            using (var stream = Photo(1000, 750, 999 + p, sin, png: true))
            using (var image = XImage.FromStream(stream))
                gfx.DrawImage(image, 60, 500, 470, 352.5 * 0.85);
        }
        doc.Save(path);
        return $"{pageCount} pages, per page 4 x 1600x1200 color JPEG q85 + 1 x 1000x750 PNG with alpha (SMask)";
    }

    private static MemoryStream Photo(int w, int h, int seed, double[] sin, bool png)
    {
        int bpp = png ? 4 : 3;
        var px = new byte[w * h * bpp];
        double cx = (seed * 97 % w), cy = (seed * 53 % h), r2 = (w / 5.0) * (w / 5.0);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * bpp;
                double dx = x - cx, dy = y - cy;
                bool disc = dx * dx + dy * dy < r2;
                px[i] = (byte)(128 + 120 * sin[(x + y + seed * 31) / 3 & 1023]);
                px[i + 1] = (byte)(disc ? 230 : 40 + y * 180 / h);
                px[i + 2] = (byte)(60 + x * 190 / w);
                if (png) px[i + 3] = (byte)(disc ? 255 : 160 + (x * 95 / w));
            }
        var bitmap = BitmapSource.Create(w, h, 96, 96, png ? PixelFormats.Bgra32 : PixelFormats.Bgr24, null, px, w * bpp);
        BitmapEncoder encoder = png ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return stream;
    }

    // ===================== startup (separate processes) =====================

    private static object Startup()
    {
        string exe = AppExe();
        if (!File.Exists(exe)) throw new FileNotFoundException("Release publish not found (dotnet publish src/PdfNotes.App -c Release -r win-x64 --self-contained false -o artifacts/publish/win-x64)", exe);
        string settings = Path.Combine(folder, "settings-startup");
        Directory.CreateDirectory(settings);
        int runs = Quick ? 2 : 6;
        var empty = new List<StartupRun>();
        var withFile = new List<StartupRun>();
        for (int i = 0; i < runs; i++) empty.Add(Launch(exe, settings, null));
        for (int i = 0; i < runs; i++) withFile.Add(Launch(exe, settings, normal50));
        double[] handles = [.. empty.Where(r => r.WindowHandleMs > 0).Select(r => r.WindowHandleMs)];
        double[] idles = [.. empty.Where(r => r.InputIdleMs > 0).Select(r => r.InputIdleMs)];
        double[] titles = [.. withFile.Where(r => r.DocumentTitleMs > 0).Select(r => r.DocumentTitleMs)];
        summary["StartupInputIdleMedianMs"] = Median(idles);
        summary["StartupInputIdleMaxMs"] = idles.Length > 0 ? idles.Max() : null;
        summary["StartupWindowHandleMedianMs"] = Median(handles);
        summary["StartupFileDocumentShownMedianMs"] = Median(titles);
        summary["IdlePrivateMBNoDocument"] = Median([.. empty.Where(r => r.PrivateMB > 0).Select(r => r.PrivateMB)]);
        summary["IdlePrivateMBWithNormal50"] = Median([.. withFile.Where(r => r.PrivateMB > 0).Select(r => r.PrivateMB)]);
        return new
        {
            Exe = exe,
            Method = "Process.Start → MainWindowHandle != 0 (polled every 2 ms) → WaitForInputIdle; with a file argument also until the window title names the file (document model shown, ShowDocument done). Killed after 3 s (memory read before). Settings in a temporary folder, its sessions folder cleared before each run. First run listed separately (the OS file cache was already warm from earlier builds/runs).",
            NoFile = new { Runs = empty, WindowHandle = Stats(handles), InputIdle = Stats(idles), FirstRunInputIdleMs = empty.FirstOrDefault()?.InputIdleMs },
            WithNormal50 = new { Runs = withFile, DocumentTitle = Stats(titles), Note = "first page bitmap time is measured in process (FirstPage)" },
        };
    }

    private sealed record StartupRun(double WindowHandleMs, double InputIdleMs, double DocumentTitleMs, double PrivateMB, double WorkingSetMB, string? Error);

    private static StartupRun Launch(string exe, string settings, string? file)
    {
        try { var sessions = Path.Combine(settings, "sessions"); if (Directory.Exists(sessions)) Directory.Delete(sessions, true); } catch (Exception) { }
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        if (file is not null) info.ArgumentList.Add(file);
        info.Environment["PDFNOTES_SETTINGS_DIR"] = settings;
        double handle = -1, idle = -1, title = -1, priv = -1, ws = -1;
        string? error = null;
        var watch = Stopwatch.StartNew();
        Process? p = null;
        try
        {
            p = Process.Start(info)!;
            string expected = file is null ? "" : Path.GetFileName(file);
            while (watch.ElapsedMilliseconds < 30000 && !p.HasExited)
            {
                p.Refresh();
                if (handle < 0 && p.MainWindowHandle != IntPtr.Zero)
                {
                    handle = watch.Elapsed.TotalMilliseconds;
                    if (file is null)
                    {
                        try { if (p.WaitForInputIdle(20000)) idle = watch.Elapsed.TotalMilliseconds; } catch (InvalidOperationException) { }
                        break;
                    }
                }
                if (handle >= 0 && file is not null && p.MainWindowTitle.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                {
                    title = watch.Elapsed.TotalMilliseconds;
                    try { if (p.WaitForInputIdle(20000)) idle = watch.Elapsed.TotalMilliseconds; } catch (InvalidOperationException) { }
                    break;
                }
                Thread.Sleep(2);
            }
            if (p.HasExited) error = $"exited with {p.ExitCode}";
            else if (handle < 0) error = "no window within 30 s";
            else if (file is not null && title < 0) error = "document title not seen within 30 s";
            Thread.Sleep(3000);
            if (!p.HasExited) { p.Refresh(); priv = p.PrivateMemorySize64 / MB; ws = p.WorkingSet64 / MB; }
        }
        catch (Exception e) { error = e.Message; }
        finally
        {
            try { if (p is { HasExited: false }) { p.Kill(entireProcessTree: true); p.WaitForExit(10000); } } catch (Exception) { }
            p?.Dispose();
            Thread.Sleep(700);
        }
        return new StartupRun(R(handle), R(idle), R(title), R(priv), R(ws), error);
    }

    // ===================== frames =====================

    private static readonly List<TaskCompletionSource<double>> frameWaiters = [];
    private static readonly List<TaskCompletionSource<(double Frame, double Ready)>> readyWaiters = [];
    private static Action<TimeSpan, double>? perFrame;
    private static TimeSpan lastRenderingTime;

    private static void OnRendering(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == lastRenderingTime) return; // WPF may raise Rendering more than once per frame
        lastRenderingTime = t;
        double now = Now();
        try { perFrame?.Invoke(t, now); }
        catch (Exception x) { perFrame = null; errors.Add(new { Section = "perFrame", Error = x.ToString() }); }
        if (frameWaiters.Count > 0)
        {
            var waiting = frameWaiters.ToArray(); frameWaiters.Clear();
            foreach (var w in waiting) w.TrySetResult(now);
        }
        if (readyWaiters.Count > 0)
        {
            var waiting = readyWaiters.ToArray(); readyWaiters.Clear();
            // A Render-priority operation posted during the frame runs after this frame's layout and render pass.
            foreach (var w in waiting) Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Render, () => w.TrySetResult((now, Now())));
        }
    }

    private static Task<double> NextFrame()
    {
        var tcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        frameWaiters.Add(tcs);
        return WithTimeout(tcs.Task, 5000, -1);
    }

    /// <summary>The next frame's Rendering time and the moment its UI-thread work (layout, render, commit) is done.</summary>
    private static Task<(double Frame, double Ready)> FrameReady()
    {
        var tcs = new TaskCompletionSource<(double, double)>(TaskCreationOptions.RunContinuationsAsynchronously);
        readyWaiters.Add(tcs);
        return WithTimeout(tcs.Task, 5000, (-1.0, -1.0));
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, int ms, T fallback)
    {
        if (await Task.WhenAny(task, Task.Delay(ms)) == task) return await task;
        messages.Add($"frame wait timed out after {ms} ms");
        return fallback;
    }

    private static async Task Frames(int n) { for (int i = 0; i < n; i++) await NextFrame(); }

    private static async Task<object> FrameBaseline()
    {
        var intervals = new List<double>();
        TimeSpan? previous = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int n = 0;
        perFrame = (t, _) =>
        {
            if (previous is { } p) intervals.Add((t - p).TotalMilliseconds);
            previous = t;
            if (++n >= 180) { perFrame = null; done.TrySetResult(); }
        };
        await Task.WhenAny(done.Task, Task.Delay(10000));
        perFrame = null;
        summary["IdleFrameIntervalMedianMs"] = Median([.. intervals]);
        return new { Note = "idle CompositionTarget.Rendering interval (RenderingTime differences) with the probe window open; the refresh base of all fps numbers", Intervals = Stats(intervals) };
    }

    // ===================== window =====================

    private static T OffScreen<T>(T w) where T : Window
    {
        w.ShowActivated = false; w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = SystemParameters.VirtualScreenLeft - 6000; w.Top = SystemParameters.VirtualScreenTop;
        return w;
    }

    private static MainWindow NewWindow()
    {
        var w = OffScreen(new MainWindow());
        w.Width = 1280; w.Height = 800;
        w.MessageSink = messages.Add;
        w.OpenErrorSink = messages.Add;
        w.PromptOverride = (text, _) => { messages.Add("prompt: " + text.Replace('\n', ' ')); return MessageBoxResult.No; };
        w.BackupIntervalOverride = TimeSpan.FromHours(3); // no automatic backup inside a measurement
        return w;
    }

    private static async Task CreateWindow()
    {
        double t0 = Now();
        window = NewWindow();
        double constructed = Now();
        window.Show();
        double shown = Now();
        var (frame, ready) = await FrameReady();
        var dpi = VisualTreeHelper.GetDpi(window);
        await Frames(10);
        result["InProcessWindow"] = new
        {
            Note = "first MainWindow of the probe process (includes JIT of the app code); window 1280x800 DIP off screen",
            ConstructMs = R(constructed - t0), ShowMs = R(shown - constructed), FirstFrameReadyMs = R(ready - t0),
            DpiScale = dpi.DpiScaleX, PixelsPerInchX = dpi.PixelsPerInchX,
            ViewportDip = $"{View.ViewportWidth:0}x{View.ViewportHeight:0}",
        };
    }

    // ===================== helpers on the document =====================

    private static int BlankVisible(DocumentView view)
    {
        if (view.Layout is not { } layout || view.ViewportWidth <= 0) return 0;
        var visible = layout.VisiblePages(new Rect2(view.HorizontalOffset, view.VerticalOffset, view.ViewportWidth, view.ViewportHeight));
        var withBase = view.RealizedPages.Where(v => v.BaseBitmap is not null).Select(v => v.Index).ToHashSet();
        return visible.Count(i => !withBase.Contains(i));
    }

    private static bool HasBase(DocumentView view, int index) => view.RealizedPages.Any(v => v.Index == index && v.BaseBitmap is not null);

    /// <summary>Waits until the render plan ran, the scheduler is idle and every visible page has a background. Returns ms or -1.</summary>
    private static async Task<double> Idle(int timeoutMs, DocumentView? view = null)
    {
        view ??= View;
        double t0 = Now();
        await Task.Delay(170); // ZoomPlanDelay 140 ms / ScrollPlanDelay 16 ms debounce
        while (Now() - t0 < timeoutMs)
        {
            if (view.Scheduler is { IsIdle: true } && BlankVisible(view) == 0) return R(Now() - t0);
            await NextFrame();
        }
        messages.Add($"Idle timed out after {timeoutMs} ms");
        return -1;
    }

    private static async Task<bool> Open(string path)
    {
        bool ok = await window.OpenFileAsync(path);
        if (!ok) throw new InvalidOperationException("could not open " + path);
        return ok;
    }

    private static Point Dip(Point2 pagePoint, int page)
    {
        var p = View.PageTransform(page).PageToDip.Transform(pagePoint);
        return new(p.X, p.Y);
    }

    private static long ThumbnailBytes(MainWindow w) =>
        w.PageThumbnails.Select(t => t.Image as BitmapSource).Where(b => b is not null).Sum(b => (long)b!.PixelWidth * b.PixelHeight * 4);

    private static object CacheNow(MainWindow w)
    {
        var s = ViewOf(w).Scheduler;
        return new
        {
            PageCacheMB = R((s?.CacheBytes ?? 0) / MB), PageCacheCount = s?.CacheCount ?? 0,
            ThumbnailMB = R(ThumbnailBytes(w) / MB), Thumbnails = w.PageThumbnails.Count(t => t.Image is not null),
            RendersStarted = s?.RendersStarted ?? 0, RendersCompleted = s?.RendersCompleted ?? 0, RequestsDropped = s?.RequestsDropped ?? 0,
        };
    }

    private sealed record MemorySample(double PrivateMB, double WorkingSetMB, double ManagedMB, int Handles, int Threads, uint GdiObjects, uint UserObjects);

    private static async Task<MemorySample> MemoryNow(bool collect = true)
    {
        if (collect)
        {
            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            GC.Collect();
            await Frames(3); // the render thread releases its resources a frame later
            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            GC.Collect();
        }
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return new MemorySample(R(p.PrivateMemorySize64 / MB), R(p.WorkingSet64 / MB), R(GC.GetTotalMemory(false) / MB),
            p.HandleCount, p.Threads.Count, GetGuiResources(p.Handle, 0), GetGuiResources(p.Handle, 1));
    }

    // ===================== first page =====================

    private static async Task FirstPages()
    {
        int reps = Quick ? 1 : 5, few = Quick ? 1 : 3;
        var normal = await FirstPageSeries(normal50, "normal50", reps);
        result["FirstPage"] = new
        {
            Method = "a 1-page document is opened before each run (new scheduler, empty cache); t0 = OpenFileAsync call. OpenMs = OpenFileAsync returned (file read, model, ShowDocument). AppliedMs = first Rendering frame at which page 1's PageVisual has its background bitmap (internal). ShownMs = that frame's UI-thread layout/render pass finished (display prepared; the compositor presents it at the next vsync). AllVisibleMs = every visible page has a background. ThumbnailsMs = every page thumbnail rendered.",
            Normal50 = normal,
            Scanned50 = await FirstPageSeries(scanned50, "scanned50", few),
            Images20 = await FirstPageSeries(images20, "images20", few),
            MixedRotation = await FirstPageSeries(mixed, "mixedRotation", few),
        };
    }

    private static async Task<object> FirstPageSeries(string path, string name, int reps)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        var runs = new List<object>();
        var open = new List<double>(); var applied = new List<double>(); var shown = new List<double>();
        for (int i = 0; i < reps; i++)
        {
            await Open(small);
            await Idle(5000);
            await MemoryNow();
            double t0 = Now();
            await Open(path);
            double t1 = Now();
            double a = -1, s = -1;
            while (Now() - t0 < 30000)
            {
                var (f, r) = await FrameReady();
                if (f < 0) continue;
                if (HasBase(View, 0)) { a = f - t0; s = r - t0; break; }
            }
            double all = -1;
            while (Now() - t0 < 30000) { if (BlankVisible(View) == 0) { all = Now() - t0; break; } await NextFrame(); }
            double thumbs = -1;
            while (Now() - t0 < 120000) { if (window.PageThumbnails.All(t => t.Image is not null)) { thumbs = Now() - t0; break; } await Task.Delay(20); }
            await Idle(10000);
            var cache = CacheNow(window);
            var memory = await MemoryNow();
            open.Add(t1 - t0); if (a >= 0) applied.Add(a); if (s >= 0) shown.Add(s);
            runs.Add(new { Run = i + 1, OpenMs = R(t1 - t0), AppliedMs = R(a), ShownMs = R(s), AllVisibleMs = R(all), ThumbnailsMs = R(thumbs), Zoom = R(View.Zoom), Cache = cache, Memory = memory });
        }
        int annotations = Session.Current.Pages.Sum(p => p.Annotations.Count);
        if (name == "normal50") { summary["FirstPageNormal50ShownMedianMs"] = Median([.. shown]); summary["FirstPageNormal50ShownMaxMs"] = shown.Count > 0 ? shown.Max() : null; }
        else summary[$"FirstPage_{name}_ShownMedianMs"] = Median([.. shown]);
        return new { Sample = name, Pages = Session.Current.Pages.Count, Annotations = annotations, Open = Stats(open), Applied = Stats(applied), Shown = Stats(shown), Runs = runs };
    }

    // ===================== pen =====================

    private static async Task Pen()
    {
        await Open(normal50);
        await Idle(10000);
        var plain = await PenRun(0, "normal page (no notes)");
        // A complex annotation load on page 1: 1000 ink strokes of 40 points each.
        var page = Session.Current.Pages[0];
        var rnd = new Random(7);
        var strokes = new List<(Annotation, Resource?)>();
        for (int i = 0; i < 1000; i++)
        {
            double x = 40 + rnd.NextDouble() * (page.Setup.WidthPt - 120), y = 40 + rnd.NextDouble() * (page.Setup.HeightPt - 120);
            var pts = new Point2[40];
            double angle = rnd.NextDouble() * Math.PI * 2;
            for (int k = 0; k < pts.Length; k++)
            {
                angle += (rnd.NextDouble() - 0.5) * 0.6;
                x += Math.Cos(angle) * 2.0; y += Math.Sin(angle) * 2.0;
                pts[k] = new(x, y);
            }
            strokes.Add((new InkStroke(Guid.NewGuid(), false, Affine.Identity, ImmutableArray.Create(pts), 1 + rnd.NextDouble() * 2,
                new((byte)rnd.Next(0, 200), (byte)rnd.Next(0, 200), (byte)rnd.Next(0, 200)), 1), null));
        }
        var ready = FrameReady();
        double t0 = Now();
        bool added = Session.Execute(DocumentEdits.AddAnnotations(Session.Current, page.Id, strokes));
        double t1 = Now();
        var (_, r) = await ready;
        await Idle(10000);
        var heavy = await PenRun(0, "page with 1000 ink strokes");
        double s0 = Now();
        bool saved = await window.SaveToAsync(complex);
        double s1 = Now();
        result["Pen"] = new
        {
            Method = "pen tool PointerDown/PointerMove/PointerUp called directly at zoom 100% (no OS input), one move per frame like a 60 Hz mouse. HandlerMs = PointerMove call; ToNextFrameMs = until the next Rendering frame; ToFrameReadyMs = until that frame's UI-thread layout/render pass is done (internal processing to next display prepared; vsync presentation not included). Commit = PointerUp (Execute + overlay refresh) to frame ready.",
            Normal = plain,
            AddThousandStrokes = new { Added = added, ExecuteMs = R(t1 - t0), ToFrameReadyMs = R(r - t0) },
            Complex = heavy,
            SaveComplex = new { Saved = saved, Ms = R(s1 - s0), Bytes = File.Exists(complex) ? new FileInfo(complex).Length : 0, Annotations = Session.Current.Pages.Sum(p => p.Annotations.Count) },
        };
    }

    private sealed record PenStats(string Label, int Moves, int AnnotationsOnPage, object Handler, object ToNextFrame, object ToFrameReady, double CommitHandlerMs, double CommitFrameReadyMs, bool Committed, double P95FrameReady);

    private static async Task<PenStats> PenRun(int pageIndex, string label)
    {
        window.SetTool(ToolKind.Pen);
        View.SetZoom(1.0);
        View.GoToPage(pageIndex);
        await Idle(10000);
        var page = Session.Current.Pages[pageIndex];
        int before = page.Annotations.Count;
        // 300 points along a wave in the upper part of the page (in view at 100% after GoToPage).
        var pts = Enumerable.Range(0, 300).Select(i => new Point2(70 + i * 1.5, Math.Min(300, page.Setup.HeightPt * 0.3) + 40 * Math.Sin(i / 15.0))).ToArray();
        var handler = new List<double>(); var toFrame = new List<double>(); var toReady = new List<double>();
        await NextFrame();
        var tool = View.Tool ?? throw new InvalidOperationException("no pen tool");
        if (!tool.PointerDown(Ptr(Dip(pts[0], pageIndex), ModifierKeys.None, 1))) throw new InvalidOperationException("pen did not start");
        var active = View.Tool!;
        for (int i = 1; i < pts.Length; i++)
        {
            var ready = FrameReady();
            double a = Now();
            active.PointerMove(Ptr(Dip(pts[i], pageIndex), ModifierKeys.None));
            double b = Now();
            var (f, r) = await ready;
            handler.Add(b - a);
            if (f >= 0) { toFrame.Add(f - a); toReady.Add(r - a); }
        }
        var up = FrameReady();
        double u0 = Now();
        active.PointerUp(Ptr(Dip(pts[^1], pageIndex), ModifierKeys.None));
        double u1 = Now();
        var (_, ur) = await up;
        bool committed = Session.Current.Pages[pageIndex].Annotations.Count == before + 1;
        window.SetTool(ToolKind.Select);
        await Idle(10000);
        double p95 = toReady.Count > 0 ? Pct([.. toReady.Order()], .95) : -1;
        summary[$"Pen_{(before >= 1000 ? "Complex" : "Normal")}_P95FrameReadyMs"] = p95;
        summary[$"Pen_{(before >= 1000 ? "Complex" : "Normal")}_P95HandlerMs"] = handler.Count > 0 ? Pct([.. handler.Order()], .95) : null;
        summary[$"Pen_{(before >= 1000 ? "Complex" : "Normal")}_CommitFrameReadyMs"] = R(ur - u0);
        return new PenStats(label, pts.Length - 1, before, Stats(handler), Stats(toFrame), Stats(toReady), R(u1 - u0), R(ur - u0), committed, R(p95));
    }

    // ===================== scroll =====================

    private static async Task Scroll()
    {
        var runs = new List<object>();
        int maxFrames = Quick ? 240 : 4000;
        await Open(normal50);
        runs.Add(await ScrollRun("normal50", 60, maxFrames));
        if (!Quick) runs.Add(await ScrollRun("normal50 (repeat)", 60, maxFrames));
        runs.Add(await ScrollRun("normal50", 240, maxFrames));
        runs.Add(await JumpRun("normal50"));
        await Open(scanned50);
        runs.Add(await ScrollRun("scanned50", 60, maxFrames));
        runs.Add(await ScrollRun("scanned50", 240, maxFrames));
        runs.Add(await JumpRun("scanned50"));
        await Open(images20);
        runs.Add(await ScrollRun("images20", 60, maxFrames));
        await Open(mixed);
        runs.Add(await ScrollRun("mixedRotation", 60, maxFrames));
        result["Scroll"] = new
        {
            Method = "fit width, from the top: in every Rendering frame the vertical offset grows by StepDip (60 DIP/frame ≈ 3600 DIP/s at 60 Hz, 240 ≈ fast fling) until the end. Fps and intervals from RenderingEventArgs.RenderingTime; UiFrameMs = offset change to the end of that frame's layout/render pass; BlankFrames = frames in which a visible page had no background yet (previous frame's state). Jump = GoToPage to every 5th page, one per frame.",
            Runs = runs,
        };
    }

    private static async Task<object> ScrollRun(string name, double step, int maxFrames)
    {
        View.FitWidth();
        View.GoToPage(0);
        await Idle(15000);
        var intervals = new List<double>(); var ui = new List<double>(); var set = new List<double>();
        int frames = 0, blank = 0;
        long peakCache = 0;
        TimeSpan? previous = null, first = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = View;
        var dispatcher = window.Dispatcher;
        perFrame = (t, now) =>
        {
            if (previous is { } p) intervals.Add((t - p).TotalMilliseconds);
            previous = t; first ??= t; frames++;
            if (BlankVisible(view) > 0) blank++;
            peakCache = Math.Max(peakCache, view.Scheduler?.CacheBytes ?? 0);
            double end = view.ExtentHeight - view.ViewportHeight;
            if (view.VerticalOffset >= end - 0.5 || frames >= maxFrames) { perFrame = null; done.TrySetResult(); return; }
            double a = Now();
            view.SetVerticalOffset(Math.Min(end, view.VerticalOffset + step));
            set.Add(Now() - a);
            dispatcher.BeginInvoke(DispatcherPriority.Render, () => ui.Add(Now() - a));
        };
        await Task.WhenAny(done.Task, Task.Delay(180000));
        perFrame = null;
        double seconds = previous is { } l && first is { } f ? (l - f).TotalSeconds : 0;
        double fps = seconds > 0 ? (frames - 1) / seconds : 0;
        await Idle(15000);
        double p95 = intervals.Count > 0 ? Pct([.. intervals.Order()], .95) : -1;
        summary[$"Scroll_{name.Replace(" (repeat)", "")}_{step}_Fps"] = summary.TryGetValue($"Scroll_{name.Replace(" (repeat)", "")}_{step}_Fps", out var old) && old is { } o ? Math.Min(o, R(fps)) : R(fps);
        summary[$"Scroll_{name.Replace(" (repeat)", "")}_{step}_P95IntervalMs"] = summary.TryGetValue($"Scroll_{name.Replace(" (repeat)", "")}_{step}_P95IntervalMs", out var oldP) && oldP is { } op ? Math.Max(op, R(p95)) : R(p95);
        summary["PeakPageCacheMB"] = Math.Max(summary.GetValueOrDefault("PeakPageCacheMB") ?? 0, R(peakCache / MB));
        return new
        {
            Sample = name, StepDip = step, Frames = frames, Seconds = R(seconds), Fps = R(fps),
            Interval = Stats(intervals), Over25Ms = intervals.Count(x => x > 25), Over50Ms = intervals.Count(x => x > 50),
            SetOffsetMs = Stats(set), UiFrameMs = Stats(ui), BlankFrames = blank, PeakPageCacheMB = R(peakCache / MB), After = CacheNow(window),
        };
    }

    private static async Task<object> JumpRun(string name)
    {
        View.FitWidth();
        View.GoToPage(0);
        await Idle(15000);
        var ui = new List<double>(); var intervals = new List<double>();
        TimeSpan? previous = null;
        int index = 0, blank = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = View; var dispatcher = window.Dispatcher;
        perFrame = (t, _) =>
        {
            if (previous is { } p) intervals.Add((t - p).TotalMilliseconds);
            previous = t;
            if (BlankVisible(view) > 0) blank++;
            index += 5;
            if (index >= view.PageCount) { perFrame = null; done.TrySetResult(); return; }
            double a = Now();
            view.GoToPage(index);
            dispatcher.BeginInvoke(DispatcherPriority.Render, () => ui.Add(Now() - a));
        };
        await Task.WhenAny(done.Task, Task.Delay(60000));
        perFrame = null;
        double settle = await Idle(30000);
        return new { Sample = name, Kind = "jump every 5th page per frame", Interval = Stats(intervals), UiFrameMs = Stats(ui), BlankFrames = blank, LastPageSharpMs = settle, After = CacheNow(window) };
    }

    // ===================== zoom =====================

    private static async Task Zoom()
    {
        await Open(normal50);
        var normal = await ZoomSteps("normal50");
        var smooth = await ZoomContinuous("normal50");
        await Open(scanned50);
        var scanned = await ZoomSteps("scanned50");
        var smoothScan = await ZoomContinuous("scanned50");
        result["Zoom"] = new
        {
            Method = "ZoomByStep (Ctrl+= steps) 100%→800%→50% on page 1. HandlerMs = call; FrameReadyMs = next frame's layout/render pass done (old bitmap scaled); SharpMs = plan debounce (140 ms) + scheduler idle + every visible page has a background. Continuous = SetZoom ×1.03 per frame for 40 frames then back (pinch/Ctrl+wheel like).",
            Normal50 = normal, Normal50Continuous = smooth, Scanned50 = scanned, Scanned50Continuous = smoothScan,
        };
    }

    private static async Task<object> ZoomSteps(string name)
    {
        View.SetZoom(1.0);
        View.GoToPage(0);
        await Idle(15000);
        var steps = new List<object>();
        var sharp = new List<double>();
        for (int direction = 1, guard = 0; guard < 24; guard++)
        {
            if (direction > 0 && View.Zoom >= 7.99) direction = -1;
            if (direction < 0 && View.Zoom <= 0.501) break;
            double from = View.Zoom;
            var ready = FrameReady();
            double a = Now();
            window.ZoomByStep(direction);
            double b = Now();
            var (_, r) = await ready;
            double s = await Idle(20000);
            if (s >= 0) sharp.Add(s);
            steps.Add(new { From = R(from * 100), To = R(View.Zoom * 100), HandlerMs = R(b - a), FrameReadyMs = R(r - a), SharpMs = s, Cache = CacheNow(window) });
        }
        summary[$"Zoom_{name}_SharpP95Ms"] = sharp.Count > 0 ? Pct([.. sharp.Order()], .95) : null;
        View.FitWidth();
        await Idle(15000);
        return new { Sample = name, Steps = steps, Sharp = Stats(sharp) };
    }

    private static async Task<object> ZoomContinuous(string name)
    {
        View.SetZoom(1.0);
        View.GoToPage(0);
        await Idle(15000);
        var intervals = new List<double>(); var ui = new List<double>();
        TimeSpan? previous = null;
        int n = 0, blank = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = View; var dispatcher = window.Dispatcher;
        perFrame = (t, _) =>
        {
            if (previous is { } p) intervals.Add((t - p).TotalMilliseconds);
            previous = t;
            if (BlankVisible(view) > 0) blank++;
            if (++n > 80) { perFrame = null; done.TrySetResult(); return; }
            double a = Now();
            view.SetZoom(view.Zoom * (n <= 40 ? 1.03 : 1 / 1.03));
            dispatcher.BeginInvoke(DispatcherPriority.Render, () => ui.Add(Now() - a));
        };
        await Task.WhenAny(done.Task, Task.Delay(60000));
        perFrame = null;
        double settle = await Idle(20000);
        double seconds = intervals.Sum() / 1000;
        View.FitWidth();
        await Idle(15000);
        return new { Sample = name, Frames = n, Fps = R(seconds > 0 ? intervals.Count / seconds : 0), Interval = Stats(intervals), UiFrameMs = Stats(ui), BlankFrames = blank, SharpAfterMs = settle };
    }

    private static async Task<object> CacheSnapshot()
    {
        await Open(scanned50);
        View.SetZoom(2.0);
        for (int i = 0; i < View.PageCount; i += 3) { View.GoToPage(i); await Frames(2); }
        await Idle(30000);
        var heavy = CacheNow(window);
        for (double w0 = Now(); !window.PageThumbnails.All(t => t.Image is not null) && Now() - w0 < 120000;) await Task.Delay(50);
        double total = ((View.Scheduler?.CacheBytes ?? 0) + ThumbnailBytes(window)) / MB;
        summary["CacheAfterScan200PercentMB"] = R(total);
        summary["PageCacheBudgetMB"] = PageRenderScheduler.DefaultCacheBytes / MB;
        View.FitWidth();
        return new
        {
            Method = "scanned50 at 200%, every 3rd page visited, then idle: page cache (PageRenderScheduler, LRU with byte budget) + page thumbnails (no budget, one bitmap per page)",
            PageCacheBudgetMB = PageRenderScheduler.DefaultCacheBytes / MB,
            SpecBudgetMB = 256,
            Scanned200 = heavy,
            PageCachePlusThumbnailsMB = R(total),
            Memory = await MemoryNow(),
        };
    }

    // ===================== save / export / search / merge =====================

    private static async Task SaveExportSearchMerge()
    {
        int reps = Quick ? 1 : 3;
        await Open(normal50);
        await Idle(10000);
        var rnd = new Random(3);
        double a0 = Now();
        foreach (var page in Session.Current.Pages.ToArray())
        {
            var items = new List<(Annotation, Resource?)>();
            for (int i = 0; i < 20; i++)
            {
                var pts = new Point2[30];
                double x = 60 + rnd.NextDouble() * 400, y = 80 + rnd.NextDouble() * 650;
                for (int k = 0; k < pts.Length; k++) pts[k] = new(x + k * 3, y + 6 * Math.Sin(k / 3.0));
                items.Add((new InkStroke(Guid.NewGuid(), false, Affine.Identity, ImmutableArray.Create(pts), 2, new(0, 0, 0xC0), 1), null));
            }
            Session.Execute(DocumentEdits.AddAnnotations(Session.Current, page.Id, items));
        }
        double a1 = Now();
        await Idle(10000);
        var saves = new List<object>(); var saveMs = new List<double>();
        for (int i = 0; i < reps; i++)
        {
            string target = i == 0 ? notes50 : Path.Combine(work, $"notes-save-{i + 1}.pdf");
            double t0 = Now();
            bool ok = await window.SaveToAsync(target);
            double ms = Now() - t0;
            saveMs.Add(ms);
            saves.Add(new { Run = i + 1, Saved = ok, Ms = R(ms), Bytes = File.Exists(target) ? new FileInfo(target).Length : 0 });
            if (i == 0 && ok) Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id,
                new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(100, 100), new(140, 120)], 2, new(0xC0, 0, 0), 1))); // dirty again
        }
        summary["Save50WithNotesMedianMs"] = Median([.. saveMs]);

        var ids = Session.Current.Pages.Select(p => p.Id).ToArray();
        var exports = new List<object>(); var exportMs = new List<double>();
        for (int i = 0; i < reps; i++)
        {
            string target = Path.Combine(work, $"export-50-{i + 1}.pdf");
            double t0 = Now();
            bool ok = await window.Document.ExportPagesAsync(target, ids, flatten: false);
            double ms = Now() - t0;
            exportMs.Add(ms);
            exports.Add(new { Run = i + 1, Flatten = false, Exported = ok, Ms = R(ms), Bytes = File.Exists(target) ? new FileInfo(target).Length : 0 });
        }
        {
            string target = Path.Combine(work, "export-50-flatten.pdf");
            double t0 = Now();
            bool ok = await window.Document.ExportPagesAsync(target, ids, flatten: true);
            exports.Add(new { Run = 1, Flatten = true, Exported = ok, Ms = R(Now() - t0), Bytes = File.Exists(target) ? new FileInfo(target).Length : 0 });
        }
        summary["Export50MedianMs"] = Median([.. exportMs]);

        // search: the first query also reads the glyphs of every page
        window.OpenSearch();
        var box = (TextBox)window.FindName("SearchBox");
        var searches = new List<object>();
        double? firstSearch = null;
        foreach (var query in new[] { "측정", "lazy dog 3", "없는단어xyz", "측정 sample" })
        {
            double t0 = Now();
            box.Text = query;
            await Task.Delay(5);
            while (window.IsSearching && Now() - t0 < 60000) await NextFrame();
            double ms = Now() - t0;
            firstSearch ??= R(ms - MainWindow.SearchDelayMs);
            searches.Add(new { Query = query, TotalMs = R(ms), WithoutDelayMs = R(ms - MainWindow.SearchDelayMs), Hits = window.SearchHits.Count });
        }
        window.CloseSearch();
        summary["SearchFirstMs"] = firstSearch;

        // merge two 50-page PDFs into a new document, then save it (the sources are combined on save)
        var merges = new List<object>();
        for (int i = 0; i < (Quick ? 1 : 2); i++)
        {
            double dialogMs = 0;
            window.MergeDialogOverride = async dialog =>
            {
                double d0 = Now();
                OffScreen(dialog);
                dialog.Show();
                await Task.Delay(100);
                dialog.Close();
                dialogMs = Now() - d0;
                return true;
            };
            double t0 = Now();
            bool ok = await window.MergePdfsAsync([normal50, normal50b]);
            double t1 = Now();
            await Idle(15000);
            string target = Path.Combine(work, $"merged-{i + 1}.pdf");
            double s0 = Now();
            bool saved = ok && await window.SaveToAsync(target);
            double s1 = Now();
            merges.Add(new { Run = i + 1, Merged = ok, Pages = Session.Current.Pages.Count, MergeMs = R(t1 - t0), DialogShownMs = R(dialogMs), MergeWithoutDialogMs = R(t1 - t0 - dialogMs), SaveMs = R(s1 - s0), Saved = saved, Bytes = File.Exists(target) ? new FileInfo(target).Length : 0 });
            if (i == 0) { summary["Merge2x50Ms"] = R(t1 - t0 - dialogMs); summary["MergedSaveMs"] = R(s1 - s0); }
        }
        window.MergeDialogOverride = null;

        result["SaveExportSearchMerge"] = new
        {
            Notes = new { Strokes = 1000, PerPage = 20, AddMs = R(a1 - a0) },
            Save = new { Runs = saves, Stats = Stats(saveMs), Note = "window.SaveToAsync (snapshot → EditablePdfWriter → SafeFileSaver verify/replace)" },
            Export = new { Runs = exports, Stats = Stats(exportMs), Note = "OpenDocument.ExportPagesAsync of all 50 pages (editable ×N, flatten ×1)" },
            Search = new { Runs = searches, DelayMs = MainWindow.SearchDelayMs, Note = "search box text set; until IsSearching is false (includes the 150 ms typing delay; WithoutDelayMs subtracts it)" },
            Merge = new { Runs = merges, Note = "파일 › PDF 합치기 with normal50 + normal50b (all pages); the merge dialog is shown off screen for 100 ms (subtracted)" },
        };
    }

    // ===================== leaks =====================

    private static async Task OpenCloseCycles()
    {
        int cycles = Quick ? 4 : 20;
        await Open(small);
        await Idle(5000);
        var baseline = await MemoryNow();
        var points = new List<object>();
        var priv = new List<double>(); var managed = new List<double>();
        var documents = new List<WeakReference>();
        for (int c = 0; c < cycles; c++)
        {
            double t0 = Now();
            await Open(normal50);
            documents.Add(new WeakReference(window.Document));
            await Idle(10000);
            View.GoToPage(25); await Idle(10000);
            window.ZoomByStep(1); window.ZoomByStep(1); await Idle(10000);
            View.SetVerticalOffset(View.VerticalOffset + 3000); await Idle(10000);
            View.FitWidth(); View.GoToPage(49); await Idle(10000);
            await Open(small); // closes (disposes) the 50-page document
            await Idle(5000);
            var m = await MemoryNow();
            priv.Add(m.PrivateMB); managed.Add(m.ManagedMB);
            points.Add(new { Cycle = c + 1, Ms = R(Now() - t0), Memory = m, OldDocumentsAlive = documents.Count(d => d.IsAlive) });
        }
        var slopePriv = Slope(priv.TakeLast(10).ToList());
        var slopeManaged = Slope(managed.TakeLast(10).ToList());
        summary["OpenClosePrivateSlopeMBPerCycle"] = R(slopePriv);
        summary["OpenCloseManagedSlopeMBPerCycle"] = R(slopeManaged);
        summary["OpenCloseOldDocumentsAlive"] = documents.Count(d => d.IsAlive);
        result["OpenCloseCycles"] = new
        {
            Method = "one window: open normal50 → page 26 → zoom 2 steps → scroll → fit width → last page → open the 1-page document (disposes the 50-page one) → GC.Collect ×3 + WaitForPendingFinalizers, 3 frames, again → memory. OldDocumentsAlive = WeakReferences to the replaced OpenDocument objects still alive after GC (a non-zero, growing count would point at a retained document).",
            Baseline = baseline, Points = points,
            Last10SlopePrivateMBPerCycle = R(slopePriv), Last10SlopeManagedMBPerCycle = R(slopeManaged),
            Note = "a slope near 0 and OldDocumentsAlive staying 0 means no sustained growth in this run; one value is not called a leak",
        };
    }

    private static async Task WindowCycles()
    {
        int cycles = Quick ? 3 : 20;
        var points = new List<object>();
        var priv = new List<double>(); var managed = new List<double>();
        var windows = new List<WeakReference>(); var documents = new List<WeakReference>();
        var before = await MemoryNow();
        for (int c = 0; c < cycles; c++)
        {
            double t0 = Now();
            var w = NewWindow();
            w.Show();
            await Frames(2);
            await w.OpenFileAsync(normal50);
            documents.Add(new WeakReference(w.Document));
            windows.Add(new WeakReference(w));
            await Idle(10000, ViewOf(w));
            ViewOf(w).GoToPage(20);
            await Idle(10000, ViewOf(w));
            w.Close();
            w = null!;
            await Frames(2);
            var m = await MemoryNow();
            priv.Add(m.PrivateMB); managed.Add(m.ManagedMB);
            points.Add(new { Cycle = c + 1, Ms = R(Now() - t0), Memory = m, ClosedWindowsAlive = windows.Count(x => x.IsAlive), DocumentsAlive = documents.Count(x => x.IsAlive) });
        }
        summary["WindowCyclePrivateSlopeMBPerCycle"] = R(Slope(priv.TakeLast(10).ToList()));
        summary["WindowCycleClosedWindowsAlive"] = windows.Count(x => x.IsAlive);
        result["WindowCycles"] = new
        {
            Method = "a second MainWindow (off screen) per cycle: open normal50 → page 21 → Close → GC → memory; the probe's main window stays open with the 1-page document. ClosedWindowsAlive/DocumentsAlive = WeakReferences still alive after GC (static event handlers would keep closed windows).",
            Before = before, Points = points,
            Last10SlopePrivateMBPerCycle = R(Slope(priv.TakeLast(10).ToList())), Last10SlopeManagedMBPerCycle = R(Slope(managed.TakeLast(10).ToList())),
        };
    }

    // ===================== table =====================

    private static string Table()
    {
        string V(string key, string unit = "ms", string format = "0") => summary.TryGetValue(key, out var v) && v is { } x && (x >= 0 || key.Contains("Slope")) && !double.IsNaN(x) ? x.ToString(format) + unit : "측정 실패";
        string Judge(string key, double limit, bool upper = true) => summary.TryGetValue(key, out var v) && v is { } x && x >= 0
            ? (upper ? x <= limit : x >= limit) ? "목표 이내" : "목표 초과" : "—";
        var m = new StringBuilder();
        m.AppendLine("# D13f 성능·메모리 측정 결과");
        m.AppendLine();
        m.AppendLine($"- 측정: {result.GetValueOrDefault("StartedAt")} ~ {result.GetValueOrDefault("FinishedAt")} ({result.GetValueOrDefault("TotalSeconds")}초){(Quick ? ", D13F_QUICK=1 축소 실행(참고용)" : "")}");
        m.AppendLine("- 측정 PC·빌드·샘플: `d13f-result.json`의 `Machine`, `Samples` 참고. 시작 시간은 Release publish(`artifacts/publish/win-x64`), 나머지는 Release로 빌드한 Probe 안의 MainWindow(화면 밖, 활성화 안 함, OS 입력 없음).");
        m.AppendLine("- 명세 16.2 값은 초기 목표이며 사용자 확정 SLA가 아니다. 내부 처리 시간과 화면 표시 준비 시간을 구분했다(표시 준비 = 다음 프레임의 UI 스레드 레이아웃·렌더 완료, 모니터 표시는 그다음 vsync).");
        m.AppendLine();
        m.AppendLine("| 항목 (명세 16.2) | 초기 목표 | 측정값 | 판정 |");
        m.AppendLine("|---|---|---|---|");
        m.AppendLine($"| 앱 시작 화면 표시 (별도 프로세스, 입력 대기까지 중앙값 / 최대) | 2초 안팎 | {V("StartupInputIdleMedianMs")} / {V("StartupInputIdleMaxMs")} | {Judge("StartupInputIdleMedianMs", 2000)} |");
        m.AppendLine($"| 50페이지 PDF를 인자로 실행 → 문서 표시(제목 변경) 중앙값 | 참고 | {V("StartupFileDocumentShownMedianMs")} | — |");
        m.AppendLine($"| 일반 50페이지 첫 페이지 표시 (앱 안, 중앙값 / 최대) | 2초 안팎 | {V("FirstPageNormal50ShownMedianMs")} / {V("FirstPageNormal50ShownMaxMs")} | {Judge("FirstPageNormal50ShownMedianMs", 2000)} |");
        m.AppendLine($"| 스캔 50페이지 첫 페이지 표시 중앙값 | 2초 안팎(부하 샘플) | {V("FirstPage_scanned50_ShownMedianMs")} | {Judge("FirstPage_scanned50_ShownMedianMs", 2000)} |");
        m.AppendLine($"| 이미지 중심 문서 첫 페이지 표시 중앙값 | 참고 | {V("FirstPage_images20_ShownMedianMs")} | — |");
        m.AppendLine($"| 혼합 회전 문서 첫 페이지 표시 중앙값 | 참고 | {V("FirstPage_mixedRotation_ShownMedianMs")} | — |");
        m.AppendLine($"| 복잡한 주석 1페이지(획 1000개) 첫 페이지 표시 중앙값 | 참고 | {V("FirstPage_complex-1page-1000strokes_ShownMedianMs")} | — |");
        m.AppendLine($"| 펜 입력 처리~다음 표시 준비 p95 (일반 페이지) | 33ms 이하 | {V("Pen_Normal_P95FrameReadyMs", "ms", "0.0")} (처리기 p95 {V("Pen_Normal_P95HandlerMs", "ms", "0.00")}) | {Judge("Pen_Normal_P95FrameReadyMs", 33)} |");
        m.AppendLine($"| 펜 입력 처리~다음 표시 준비 p95 (획 1000개 페이지) | 33ms 이하 | {V("Pen_Complex_P95FrameReadyMs", "ms", "0.0")} (처리기 p95 {V("Pen_Complex_P95HandlerMs", "ms", "0.00")}) | {Judge("Pen_Complex_P95FrameReadyMs", 33)} |");
        m.AppendLine($"| 펜 획 완료(PointerUp)~표시 준비 (일반 / 획 1000개) | 참고 | {V("Pen_Normal_CommitFrameReadyMs", "ms", "0.0")} / {V("Pen_Complex_CommitFrameReadyMs", "ms", "0.0")} | — |");
        m.AppendLine($"| 일반 스크롤 fps (일반 50쪽, 60 DIP/프레임) | 60fps | {V("Scroll_normal50_60_Fps", "fps", "0.0")} (p95 간격 {V("Scroll_normal50_60_P95IntervalMs", "ms", "0.0")}) | {Judge("Scroll_normal50_60_Fps", 57, upper: false)} |");
        m.AppendLine($"| 빠른 스크롤 fps (일반 50쪽, 240 DIP/프레임) | 60fps | {V("Scroll_normal50_240_Fps", "fps", "0.0")} (p95 간격 {V("Scroll_normal50_240_P95IntervalMs", "ms", "0.0")}) | {Judge("Scroll_normal50_240_Fps", 57, upper: false)} |");
        m.AppendLine($"| 스크롤 fps (스캔 50쪽, 60 / 240 DIP/프레임) | 60fps | {V("Scroll_scanned50_60_Fps", "fps", "0.0")} / {V("Scroll_scanned50_240_Fps", "fps", "0.0")} | {Judge("Scroll_scanned50_60_Fps", 57, upper: false)} |");
        m.AppendLine($"| 스크롤 fps (이미지 중심 / 혼합 회전) | 60fps | {V("Scroll_images20_60_Fps", "fps", "0.0")} / {V("Scroll_mixedRotation_60_Fps", "fps", "0.0")} | {Judge("Scroll_images20_60_Fps", 57, upper: false)} |");
        m.AppendLine($"| 확대 단계 후 선명해질 때까지 p95 (일반 / 스캔, 지연 140ms 포함) | 참고 | {V("Zoom_normal50_SharpP95Ms")} / {V("Zoom_scanned50_SharpP95Ms")} | — |");
        m.AppendLine($"| 렌더 캐시: 본문 예산 / 관측 최대 / 스캔 200% 후 본문+썸네일 | 합계 256MiB | {V("PageCacheBudgetMB", "MiB")} / {V("PeakPageCacheMB", "MiB", "0.0")} / {V("CacheAfterScan200PercentMB", "MiB", "0.0")} | {Judge("CacheAfterScan200PercentMB", 256)} |");
        m.AppendLine($"| 반복 열기/닫기 20회: 마지막 10회 기울기 (private / managed) | 지속 증가 없음 | {V("OpenClosePrivateSlopeMBPerCycle", "MB/회", "0.00")} / {V("OpenCloseManagedSlopeMBPerCycle", "MB/회", "0.00")}, GC 후 남은 이전 문서 {V("OpenCloseOldDocumentsAlive", "개")} | 아래 주의 |");
        m.AppendLine($"| 창 열기/닫기 20회: 기울기 / GC 후 남은 닫힌 창 | 지속 증가 없음 | {V("WindowCyclePrivateSlopeMBPerCycle", "MB/회", "0.00")} / {V("WindowCycleClosedWindowsAlive", "개")} | 아래 주의 |");
        m.AppendLine($"| 유휴 메모리 (publish, 문서 없음 / 50쪽 열림, private) | 기록 | {V("IdlePrivateMBNoDocument", "MB", "0")} / {V("IdlePrivateMBWithNormal50", "MB", "0")} | — |");
        m.AppendLine();
        m.AppendLine("| 부가 측정 | 측정값 |");
        m.AppendLine("|---|---|");
        m.AppendLine($"| 50쪽 + 획 1000개 저장 중앙값 | {V("Save50WithNotesMedianMs")} |");
        m.AppendLine($"| 50쪽 페이지 내보내기(편집 가능) 중앙값 | {V("Export50MedianMs")} |");
        m.AppendLine($"| 50쪽 + 50쪽 PDF 합치기 / 합친 문서 저장 | {V("Merge2x50Ms")} / {V("MergedSaveMs")} |");
        m.AppendLine($"| 50쪽 검색 첫 실행(글자 읽기 포함, 입력 지연 150ms 제외) | {V("SearchFirstMs")} |");
        m.AppendLine($"| 유휴 프레임 간격 중앙값(측정 기준) | {V("IdleFrameIntervalMedianMs", "ms", "0.0")} |");
        m.AppendLine();
        m.AppendLine("- 주의: 메모리 기울기는 한 번의 실행에서 GC 후 값의 추세다. 수치 하나로 누수라고 단정하지 않는다. GC 후에도 이전 문서/닫힌 창 개수가 늘면 참조가 남는 경로를 따로 확인한다.");
        m.AppendLine("- 시작 시간은 이미 디스크 캐시가 따뜻한 상태(콜드 부팅 아님)이며 설치본이 아닌 publish 폴더 실행이다.");
        m.AppendLine("- 프레임 간격은 WPF CompositionTarget.Rendering 기준이다. 모니터가 꺼져 있거나 다른 프로그램이 GPU를 쓰면 값이 달라질 수 있다.");
        if (errors.Count > 0) m.AppendLine($"- 실패한 구간 {errors.Count}개: `d13f-result.json`의 `Errors` 참고.");
        return m.ToString();
    }

    // ===================== numbers =====================

    private static double Now() => Clock.Elapsed.TotalMilliseconds;
    private static double R(double x) => Math.Round(x, 2);
    private static double Pct(double[] sorted, double q) => sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
    private static double? Median(double[] values) => values.Length == 0 ? null : R(Pct([.. values.Order()], .5));

    private static object Stats(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) return new { Count = 0 };
        var s = values.Order().ToArray();
        return new { Count = s.Length, P50 = R(Pct(s, .5)), P95 = R(Pct(s, .95)), Max = R(s[^1]), Min = R(s[0]), Mean = R(s.Average()) };
    }

    /// <summary>Least-squares slope per step.</summary>
    private static double Slope(IReadOnlyList<double> y)
    {
        int n = y.Count;
        if (n < 2) return 0;
        double mx = (n - 1) / 2.0, my = y.Average(), num = 0, den = 0;
        for (int i = 0; i < n; i++) { num += (i - mx) * (y[i] - my); den += (i - mx) * (i - mx); }
        return num / den;
    }

    // ===================== native (read-only queries) =====================

    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
}
