using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.App;
using PdfNotes.App.Canvas;
using PdfNotes.App.Tools;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Core.Serialization;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Writing;

namespace PdfNotes.Probe;

/// <summary>D05 actual WPF selection/input/clipboard/save checks, with separate-process reopen.</summary>
internal static class D05Check
{
    private sealed record Check(string Name, bool Passed, string Detail);
    private static readonly List<Check> Checks = [];
    private static readonly List<string> Messages = [];
    private static readonly List<string> KeyEvents = [];
    private static string folder = "";
    private static MainWindow window = null!;
    private static DocumentView View => (DocumentView)window.FindName("View");
    private static DocumentSession Session => window.Document.Session;
    private static string Editable => Path.Combine(folder, "editable.pdf");
    private static string Expected => Path.Combine(folder, "expected.json");
    private static string Metadata(DocumentModel model)
    {
        var data = SchemaV1.Serialize(model);
        return JsonSerializer.Serialize(new { data.Header, Envelopes = data.Annotations.Select(a => a.Envelope), Resources = data.Resources.Select(r => new { r.Id, r.Sha256 }) });
    }
    private static void Record(string name, bool passed, string detail = "")
    {
        Checks.Add(new(name, passed, detail));
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        if (!passed) throw new InvalidOperationException(name + ": " + detail);
    }
    public static int Run(string output, bool reopen, bool keysOnly = false, bool panOnly = false)
    {
        folder = output;
        Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.Combine(folder, reopen ? "settings-reopen" : "settings"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exitCode = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                window = new MainWindow { Width = 1200, Height = 860, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                window.MessageSink = Messages.Add;
                window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) => KeyEvents.Add($"{e.Key}:{Keyboard.Modifiers}:{e.Handled}")), true);
                window.PromptOverride = (_, _) => MessageBoxResult.No;
                window.Show(); await Settle();
                Record("foreground", await Foreground());
                if (keysOnly) await KeysOnly();
                else if (panOnly) { Record("open saved sample", await window.OpenFileAsync(Editable)); window.SetTool(ToolKind.Select); await CheckPan(); }
                else if (reopen) await Reopen(); else await FirstSession();
                exitCode = 0;
            }
            catch (Exception e) { Screenshot("failure.png"); Checks.Add(new("harness", false, e.ToString())); Console.WriteLine(e); }
            finally
            {
                File.WriteAllText(Path.Combine(folder, panOnly ? "d05-pan-result.json" : reopen ? "d05-reopen-result.json" : "d05-result.json"),
                    JsonSerializer.Serialize(new { Passed = exitCode == 0, Dpi = View.DpiScale, Checks, Messages }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close(); app.Shutdown();
            }
        };
        app.Run();
        if (!reopen && !keysOnly && !panOnly && exitCode == 0)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("d05-reopen"); start.ArgumentList.Add(folder);
            using var child = Process.Start(start)!; child.WaitForExit();
            exitCode = child.ExitCode;
        }
        return exitCode;
    }
    private static async Task KeysOnly()
    {
        View.SetZoom(1); await Settle();
        var ink = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(100, 200), new(250, 220)], 4, RgbColor.Black, 1);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, ink));
        window.SetTool(ToolKind.Select); window.Selection.Select(ink.Id); View.Focus();
        await Key(0x27); await Settle();
        double expected = View.PageTransform(0).DipLengthToPoints(1 / View.DpiScale);
        Record("arrow 1px", Math.Abs(Session.Current.FindAnnotation(ink.Id)!.Annotation.Transform.OffsetX - expected) < 1e-8);
        await Key(VkZ, control: true); await Settle();
        await Key(0x27, shift: true); await Settle();
        Record("Shift arrow 10px", Math.Abs(Session.Current.FindAnnotation(ink.Id)!.Annotation.Transform.OffsetX - expected * 10) < 1e-8, string.Join(";", KeyEvents));
    }
    private static byte[] SampleImage()
    {
        const int w = 320, h = 160;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            pixels[i] = y < h / 2 ? (byte)30 : (byte)220;
            pixels[i + 1] = 40; pixels[i + 2] = y < h / 2 ? (byte)230 : (byte)20;
            pixels[i + 3] = x < w / 4 ? (byte)0 : y < h / 2 ? (byte)255 : (byte)128;
        }
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream(); encoder.Save(buffer); return buffer.ToArray();
    }
    private static async Task Click(Point2 pagePoint, bool shift = false)
    {
        if (shift) keybd_event(VkShift, 0, 0, 0);
        await Drag(0, [pagePoint]);
        if (shift) keybd_event(VkShift, 0, KeyUp, 0);
        await Task.Delay(80);
    }
    private static async Task DragHandle(int handle, double factor = 1, double angle = 0, bool shift = false)
    {
        var t = View.PageTransform(0);
        var f = window.Selection.SelectionFrame()!.Value;
        var points = window.Selection.HandlePoints(t);
        var from = points[handle];
        Point2 to;
        if (handle == 4)
        {
            var center = f.Frame.Then(t.PageToDip).Transform(new(f.Bounds.X + f.Bounds.Width / 2, f.Bounds.Y + f.Bounds.Height / 2));
            to = Affine.Translation(-center.X, -center.Y).Then(Affine.Rotation(angle)).Then(Affine.Translation(center.X, center.Y)).Transform(from);
        }
        else
        {
            var opposite = points[(handle + 2) % 4];
            to = new(opposite.X + (from.X - opposite.X) * factor, opposite.Y + (from.Y - opposite.Y) * factor);
        }
        if (shift) keybd_event(VkShift, 0, 0, 0);
        await Drag(0, [t.DipToPage.Transform(from), t.DipToPage.Transform(to)]);
        if (shift) keybd_event(VkShift, 0, KeyUp, 0);
        await Settle();
    }
    private static void MenuCommand(string header, Point2 point)
    {
        var p = View.PageTransform(0).PageToDip.Transform(point);
        var menu = window.OpenAnnotationMenu(new(p.X, p.Y))!;
        var item = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == header);
        Record("menu enabled: " + header, item.IsEnabled);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
    }
    private static Point2 Center(ImageAnnotation image) => image.Transform.Transform(new(image.LocalBounds.X + image.LocalBounds.Width * .7, image.LocalBounds.Y + image.LocalBounds.Height * .3));
    private static async Task FirstSession()
    {
        View.SetZoom(1); await Settle();
        var imageFile = Path.Combine(folder, "source.png"); File.WriteAllBytes(imageFile, SampleImage());
        Record("image insert", window.InsertImage(File.ReadAllBytes(imageFile)));
        await Settle(); // wait for the newly selected image and toolbar/layout to settle before native input
        var image = (ImageAnnotation)Session.Current.AllAnnotations.Single();
        Record("image size and centered", Math.Abs(image.LocalBounds.Width - 240) < .1 && Math.Abs(image.LocalBounds.Height - 120) < .1,
            $"{image.LocalBounds.Width}x{image.LocalBounds.Height}pt (PNG DPI rounding)");
        var center = Center(image);
        await Drag(0, [center, new(center.X + 25, center.Y + 30)]);
        var moved = (ImageAnnotation)Session.Current.FindAnnotation(image.Id)!.Annotation;
        Record("actual image drag", Math.Abs(moved.Transform.OffsetX - image.Transform.OffsetX - 25) < 1 && Math.Abs(moved.Transform.OffsetY - image.Transform.OffsetY - 30) < 1,
            $"delta={moved.Transform.OffsetX - image.Transform.OffsetX:0.000},{moved.Transform.OffsetY - image.Transform.OffsetY:0.000}; tool={window.CurrentTool}; start={ScreenOf(0, center)}; foreground={GetForegroundWindow()}");
        await Key(VkZ, control: true); await Settle();
        Record("move one Undo", Session.Current.FindAnnotation(image.Id)!.Annotation == image);
        await DragHandle(2, 1.3);
        var scaled = (ImageAnnotation)Session.Current.FindAnnotation(image.Id)!.Annotation;
        Record("image proportional resize", Math.Abs(scaled.LocalBounds.Width / scaled.LocalBounds.Height - 2) < 1e-9 && Math.Abs(scaled.LocalBounds.Width / 240 - 1.3) < .02,
            $"{scaled.LocalBounds}; handle {window.Selection.HandlePoints(View.PageTransform(0))[2]}; view {View.ActualWidth}x{View.ActualHeight}");
        await Key(VkZ, control: true); await Settle();
        // Shift permits free image proportions.
        var handles = window.Selection.HandlePoints(View.PageTransform(0));
        var from = handles[2]; var to = new Point2(from.X + 80, from.Y + 10);
        keybd_event(VkShift, 0, 0, 0);
        await Drag(0, [View.PageTransform(0).DipToPage.Transform(from), View.PageTransform(0).DipToPage.Transform(to)]);
        keybd_event(VkShift, 0, KeyUp, 0);
        scaled = (ImageAnnotation)Session.Current.FindAnnotation(image.Id)!.Annotation;
        Record("Shift image free resize", Math.Abs(scaled.LocalBounds.Width / scaled.LocalBounds.Height - 2) > .2);
        await Key(VkZ, control: true); await Settle();
        var ink = new InkStroke(Guid.NewGuid(), false, Affine.Identity, [new(80, 200), new(220, 200)], 4, new(10, 120, 40), 1);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, ink)); await Settle();
        await Click(new(140, 200), shift: true);
        Record("actual Shift multi-select", window.Selection.SelectedIds.Count == 2);
        var before = Session.Current;
        await Drag(0, [new(140, 200), new(150, 210)]);
        Record("multi move", Session.Current.AllAnnotations.All(a => a.Transform.OffsetX > before.FindAnnotation(a.Id)!.Annotation.Transform.OffsetX + 9));
        await Key(VkZ, control: true); await Settle();
        Record("multi move single Undo", Metadata(Session.Current) == Metadata(before));
        await DragHandle(2, 1.2);
        var scaledInk = (InkStroke)Session.Current.FindAnnotation(ink.Id)!.Annotation;
        Record("multi scale geometry and width once", Math.Abs(scaledInk.WidthPt - 4.8) < .04 && Session.Current.AllAnnotations.All(a => a.Transform.IsRigid));
        await Key(VkZ, control: true); await Settle();
        await DragHandle(4, angle: Math.PI / 6, shift: true);
        Record("multi rotate Shift snap", Session.Current.AllAnnotations.All(a => Math.Abs(Math.Atan2(a.Transform.M12, a.Transform.M11) - Math.PI / 6) < 1e-8));
        var group = window.Selection.SelectionFrame()!.Value;
        Record("group selection rectangle rotates together", Math.Abs(Math.Atan2(group.Frame.M12, group.Frame.M11) - Math.PI / 6) < 1e-8);
        Screenshot("d05-rotated-selection.png");
        await Key(VkZ, control: true); await Settle();
        string cancelled = Metadata(Session.Current);
        await Drag(0, [new(140, 200), new(160, 220)], release: false);
        await Key(VkEscape); mouse_event(LeftUp, 0, 0, 0, 0); await Settle();
        Record("Esc preview cancellation", Metadata(Session.Current) == cancelled);
        View.Focus(); await Key(0x27); await Settle();
        double delta = Session.Current.FindAnnotation(ink.Id)!.Annotation.Transform.OffsetX;
        Record("arrow screen 1px", Math.Abs(delta - View.PageTransform(0).DipLengthToPoints(1 / View.DpiScale)) < 1e-8);
        await Key(VkZ, control: true); await Settle();
        Record("nudge Undo", Math.Abs(Session.Current.FindAnnotation(ink.Id)!.Annotation.Transform.OffsetX) < 1e-8, string.Join(";", KeyEvents.TakeLast(12)));
        await Key(0x27, shift: true); await Settle();
        Record("Shift arrow 10px", Math.Abs(Session.Current.FindAnnotation(ink.Id)!.Annotation.Transform.OffsetX - delta * 10) < 1e-8,
            $"actual={Session.Current.FindAnnotation(ink.Id)!.Annotation.Transform.OffsetX}, expected={delta * 10}, selected={window.Selection.SelectedIds.Count}, focus={View.IsKeyboardFocusWithin}, events={string.Join(";", KeyEvents.TakeLast(12))}");
        await Key(VkZ, control: true); await Settle();
        // The pointer off the pages: Ctrl+V keeps the place, 10pt aside (spec 5.7, D14g — over a page it pastes at the pointer).
        var away = window.PointToScreen(new(window.ActualWidth / 2, 12)); SetCursorPos((int)away.X, (int)away.Y); await Task.Delay(100);
        View.Focus(); await Key(0x43, control: true); await Task.Delay(100); await Key(0x56, control: true); await Settle();
        Record("actual clipboard multi paste", Session.Current.AllAnnotations.Count() == 4 && window.Selection.SelectedIds.Count == 2 && Session.Current.Resources.Count == 2);
        var copyImage = Session.Current.AllAnnotations.OfType<ImageAnnotation>().Last();
        Record("paste IDs offset and resources independent", copyImage.Id != image.Id && copyImage.ResourceId != image.ResourceId && Math.Abs(copyImage.Transform.OffsetX - image.Transform.OffsetX - 10) < 1e-8);
        await Key(0x58, control: true); await Settle();
        Record("actual cut", Session.Current.AllAnnotations.Count() == 2 && Session.Current.Resources.Count == 1);
        await Key(VkZ, control: true); await Settle(); Record("cut single Undo", Session.Current.AllAnnotations.Count() == 4 && Session.Current.Resources.Count == 2);
        Clipboard.SetImage(PdfNotes.App.Services.ImageAssets.Decode(SampleImage()));
        View.Focus(); await Key(0x56, control: true); await Settle();
        Record("actual clipboard image paste", Session.Current.AllAnnotations.Count() == 5 && Session.Current.Resources.Count == 3);
        await Key(VkZ, control: true); await Settle();
        Record("clipboard image paste one Undo", Session.Current.AllAnnotations.Count() == 4 && Session.Current.Resources.Count == 2);
        // Top image at overlap can be reordered and locked via the actual menu command path.
        window.Selection.Select(copyImage.Id);
        MenuCommand("맨 앞으로", Center(copyImage)); await Settle();
        Record("top-most hit follows z order", window.Selection.Pick(ToDip(Center(copyImage))) == copyImage.Id);
        MenuCommand("잠금", Center(copyImage)); await Settle();
        Record("lock removes editable selection", Session.Current.FindAnnotation(copyImage.Id)!.Annotation.Locked && !window.Selection.SelectedIds.Contains(copyImage.Id));
        await Click(Center(copyImage)); await Key(VkDelete); await Settle();
        Record("Delete cannot remove locked image", Session.Current.FindAnnotation(copyImage.Id) is not null);
        // Undo only the accidental unlocked pick/delete beneath the locked image, if present.
        if (Session.Current.AllAnnotations.Count() < 4) { await Key(VkZ, control: true); await Settle(); }
        MenuCommand("잠금 해제", Center(copyImage)); await Settle();
        Record("unlock", !Session.Current.FindAnnotation(copyImage.Id)!.Annotation.Locked);
        View.Focus(); await Key(0x41, control: true); await Settle();
        Record("Ctrl+A unlocked current page", window.Selection.SelectedIds.Count == Session.Current.AllAnnotations.Count());
        window.Selection.Select(image.Id);
        // Retain boundary data and rotation in the saved sample.
        Session.Execute(DocumentEdits.UpdateAnnotations(Session.Current, [AnnotationGeometry.Similarity(Session.Current.FindAnnotation(image.Id)!.Annotation, default, angle: .3, move: new(-220, -280))]));
        File.Move(imageFile, Path.Combine(folder, "moved-original.png"));
        window.SaveDialogOverride = _ => Editable;
        Record("save with embedded resource after original moved", await window.SaveAsync(saveAs: true));
        File.WriteAllText(Expected, Metadata(Session.Current));
        await Settle(); Screenshot("d05-selection.png");
        var visibleImage = (ImageAnnotation)Session.Current.FindAnnotation(copyImage.Id)!.Annotation;
        var sample = Center(visibleImage);
        var screenDip = ToDip(sample);
        var windowDip = View.TranslatePoint(screenDip, window);
        var pixel = new byte[4];
        Capture().CopyPixels(new Int32Rect((int)(windowDip.X * View.DpiScale), (int)(windowDip.Y * View.DpiScale), 1, 1), pixel, 4, 0);
        using (var pdf = new PdfNotes.Pdf.Engine.PdfiumDocument(Editable))
        {
            var raster = pdf.Render(0, 2, annotations: true);
            int index = (int)(sample.Y * 2) * raster.Stride + (int)(sample.X * 2) * 4;
            Record("WPF image overlay matches PDF RGB", Enumerable.Range(0, 3).All(i => Math.Abs(pixel[i] - raster.Bgra[index + i]) <= 3),
                $"WPF={pixel[2]},{pixel[1]},{pixel[0]} PDF={raster.Bgra[index + 2]},{raster.Bgra[index + 1]},{raster.Bgra[index]}");
        }
        Record("safe save no image warning", !Session.IsDirty && Messages.Count == 0, string.Join(";", Messages));
        await CheckPan();
    }
    private static async Task CheckPan()
    {
        View.SetZoom(2); View.ScrollToPagePoint(0, new(300, 400)); window.Selection.Select(null); await Settle();
        var candidates = from y in new[] { .2, .4, .6, .8 } from x in new[] { .2, .4, .6, .8 }
            select new Point(View.ViewportWidth * x, View.ViewportHeight * y);
        var point = candidates.First(p => View.HitTestPage(p) is not null && window.Selection.Pick(p, true) is null);
        var transform = View.PageTransform(0).DipToPage;
        var offset = View.Offset; string metadata = Metadata(Session.Current);
        await Drag(0, [transform.Transform(new(point.X, point.Y)), transform.Transform(new(point.X, point.Y - 40))]);
        Record("selection blank drag pans only", View.Offset != offset && Metadata(Session.Current) == metadata && !Session.IsDirty,
            $"offset {offset} -> {View.Offset}, document unchanged={Metadata(Session.Current) == metadata}");
    }
    private static Point ToDip(Point2 p)
    { var point = View.PageTransform(0).PageToDip.Transform(p); return new(point.X, point.Y); }
    private static async Task Reopen()
    {
        Record("separate-process reopen", await window.OpenFileAsync(Editable)); await Settle();
        Record("exact IDs geometry order resource bytes restored", Metadata(Session.Current) == File.ReadAllText(Expected) && Messages.Count == 0);
        window.SetTool(ToolKind.Select); View.SetZoom(1); await Settle();
        var image = Session.Current.AllAnnotations.OfType<ImageAnnotation>().Last();
        View.ScrollToPagePoint(0, Center(image)); await Settle();
        await Click(Center(image));
        Record("reopened image selectable", window.Selection.SelectedId == image.Id);
        await Key(0x27, shift: true); await Settle();
        Record("reopened image reeditable", Session.IsDirty && Session.Current.FindAnnotation(image.Id)!.Annotation.Transform != image.Transform);
        await Key(VkS, control: true); await WaitIdle();
        var read = EditablePdfReader.Read(File.ReadAllBytes(Editable), PdfPageGeometryReader.Read(File.ReadAllBytes(Editable)));
        Record("reedited image resaved", !Session.IsDirty && Metadata(read.Model) == Metadata(Session.Current));
        Screenshot("d05-reopened.png");
    }
    // OS input and screenshot helpers are shared in behavior with the D04 harness.
    private static async Task Drag(int pageIndex, IReadOnlyList<Point2> path, Func<Task>? midway = null, bool release = true)
    {
        var screen = path.Select(p => ScreenOf(pageIndex, p)).ToList();
        SetCursorPos((int)Math.Round(screen[0].X), (int)Math.Round(screen[0].Y));
        await Task.Delay(40);
        mouse_event(LeftDown, 0, 0, 0, 0);
        await Task.Delay(30);
        for (int s = 1; s < screen.Count; s++)
        {
            var a = screen[s - 1];
            var b = screen[s];
            int steps = Math.Max(1, (int)((b - a).Length / 6));
            for (int k = 1; k <= steps; k++)
            {
                var p = a + (b - a) * k / steps;
                SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y));
                await Task.Delay(6);
            }
            await Task.Delay(25); // dwell on the vertex so a coalesced mouse move cannot skip the corner sample
            if (midway is not null && s == 1) await midway();
        }
        if (release)
        {
            await Task.Delay(30);
            mouse_event(LeftUp, 0, 0, 0, 0);
        }
        await Task.Delay(60);
    }

    private static Point ScreenOf(int pageIndex, Point2 pagePoint)
    {
        var dip = View.PageTransform(pageIndex).PageToDip.Transform(pagePoint);
        return View.PointToScreen(new Point(dip.X, dip.Y));
    }

    private static async Task<bool> Foreground()
    {
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
        return GetForegroundWindow() == hwnd;
    }

    private static async Task Key(byte vk, bool control = false, bool shift = false)
    {
        if (control) keybd_event(VkControl, 0, 0, 0);
        if (shift) keybd_event(VkShift, 0, 0, 0);
        uint extended = vk is >= 0x25 and <= 0x28 ? 1u : 0u;
        keybd_event(vk, 0, extended, 0);
        await Task.Delay(60);
        keybd_event(vk, 0, KeyUp | extended, 0);
        if (shift) keybd_event(VkShift, 0, KeyUp, 0);
        if (control) keybd_event(VkControl, 0, KeyUp, 0);
    }

    private static async Task WaitIdle()
    {
        var watch = Stopwatch.StartNew();
        await Task.Delay(100);
        while (window.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(50);
        await Settle();
    }

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

    private static void Screenshot(string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Capture()));
        using var file = File.Create(Path.Combine(folder, name));
        encoder.Save(file);
    }

    private const byte VkAlt = 0x12, VkSpace = 0x20, VkControl = 0x11, VkShift = 0x10, VkEscape = 0x1B, VkDelete = 0x2E, VkZ = 0x5A, VkY = 0x59, VkS = 0x53;
    private const uint KeyUp = 0x2, LeftDown = 0x2, LeftUp = 0x4, RightDown = 0x8, RightUp = 0x10;
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nuint extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
