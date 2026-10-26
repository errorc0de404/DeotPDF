using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
using LineGeometry = PdfNotes.Core.Geometry.LineGeometry;
using DashStyle = PdfNotes.Core.Models.DashStyle;

namespace PdfNotes.Probe;

/// <summary>
/// D07 real-app check: line/curve tools, automatic 1/2 control choice, conversion, handle editing, style/arrows/dash,
/// quick menu, zoom independence, save → separate-process reopen → edit → save. With <c>logic</c> the pointer events are
/// sent to the active canvas tool directly (no OS input); that run is reported separately and never as real input.
/// </summary>
internal static class D07Check
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static bool logic;
    private static readonly List<object> checks = [];
    private static readonly List<string> messages = [];
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => (DocumentView)window.FindName("View");
    private static LineAnnotation[] Lines => Session.Current.AllAnnotations.OfType<LineAnnotation>().ToArray();

    private static string Metadata(DocumentModel model)
    {
        var data = SchemaV2.Serialize(model);
        return JsonSerializer.Serialize(new { data.Header, Envelopes = data.Annotations.Select(a => a.Envelope) });
    }

    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        if (!passed) throw new InvalidOperationException(name + ": " + detail);
    }

    public static int Run(string output, bool reopen, bool logicOnly)
    {
        folder = output; logic = logicOnly;
        Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.Combine(folder, "settings"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                window = new MainWindow { Width = 1420, Height = 900, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                window.MessageSink = messages.Add; window.PromptOverride = (_, _) => MessageBoxResult.No;
                window.Show(); await Settle();
                if (!logic && !reopen) await Foreground();
                if (reopen) await Reopen(); else await Edit();
                result = 0;
            }
            catch (Exception e) { checks.Add(new { Name = "harness", Passed = false, Detail = e.ToString() }); Console.WriteLine(e); }
            finally
            {
                string name = reopen ? "d07-reopen-result.json" : logic ? "d07-logic-result.json" : "d07-result.json";
                File.WriteAllText(Path.Combine(folder, name), JsonSerializer.Serialize(new { Passed = result == 0, Input = logic ? "tool-calls" : "os-mouse", Checks = checks, Messages = messages },
                    new JsonSerializerOptions { WriteIndented = true }));
                window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); app.Shutdown();
            }
        };
        app.Run();
        if (!reopen && result == 0)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("d07-reopen"); start.ArgumentList.Add(folder);
            using var child = Process.Start(start)!; child.WaitForExit(); result = child.ExitCode;
        }
        return result;
    }

    private static async Task Foreground()
    {
        var handle = new WindowInteropHelper(window).Handle;
        SetForegroundWindow(handle); window.Activate(); await Settle();
        for (int attempt = 0; attempt < 5 && GetForegroundWindow() != handle; attempt++)
        { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); window.Activate(); SetForegroundWindow(handle); await Task.Delay(200); }
        if (GetForegroundWindow() != handle) { window.Topmost = true; window.Activate(); SetForegroundWindow(handle); await Settle(); window.Topmost = false; }
        Check("foreground", GetForegroundWindow() == handle, $"target={handle}, actual={GetForegroundWindow()}");
    }

    private static Point2[] Sampled(Point2[] polygon, int count) =>
        Enumerable.Range(0, count).Select(i => LineGeometry.Evaluate(polygon, (double)i / (count - 1))).ToArray();

    private static double ShapeDifference(LineAnnotation a, LineAnnotation b) =>
        Enumerable.Range(0, 101).Max(i => a.Transform.Transform(LineGeometry.Evaluate(LineGeometry.ControlPolygon(a), i / 100.0))
            .DistanceTo(b.Transform.Transform(LineGeometry.Evaluate(LineGeometry.ControlPolygon(b), i / 100.0))));

    private static async Task Edit()
    {
        View.SetZoom(1); await Settle();
        window.SetTool(ToolKind.Line); await Settle();
        int undo = Session.UndoCount;
        await Drag(new(100, 100), new(300, 160));
        var straight = Lines.Single();
        Check("straight line from press to release", straight.LineKind == LineKind.Straight && straight.Start.DistanceTo(new(100, 100)) < 1 &&
            straight.End.DistanceTo(new(300, 160)) < 1 && Session.UndoCount == undo + 1 && Session.IsDirty, $"{straight.Start} → {straight.End}");
        Check("new line is selected with point handles", window.SelectedLine?.Id == straight.Id && window.Selection.LineHandlePoints(View.PageTransform(0)).Length == 2);
        Check("line uses tool style", straight.WidthPt == window.LineStyle.WidthPt && straight.Color == window.LineStyle.Color);

        await Drag(new(100, 300), new(101.5, 301));
        Check("ends under 4px cancel", Lines.Length == 1 && Session.UndoCount == undo + 1,
            JsonSerializer.Serialize(new { Count = Lines.Length, Undo = Session.UndoCount - undo, Last = Lines.LastOrDefault()?.ToString(), View.DpiScale }));

        window.Selection.Select(straight.Id);
        window.SetLineArrows(end: true); window.SetLineDash(DashStyle.Dash);
        var styled = Lines.Single();
        Check("arrow and dash apply to the drawn line and tool", styled.EndArrow && !styled.StartArrow && styled.Dash == DashStyle.Dash &&
            window.LineToolOptions.EndArrow && window.LineToolOptions.Dash == DashStyle.Dash && Session.UndoCount == undo + 3);
        var panel = (StackPanel)window.FindName("LineOptionsPanel");
        Check("line bar shows the line", panel.Visibility == Visibility.Visible && panel.Children.OfType<ComboBox>().First().SelectedIndex == (int)DashStyle.Dash &&
            panel.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().Last().IsChecked == true);
        window.SetWidth(6); window.SetTransparency(30);
        Check("width and transparency on the line", Lines.Single().WidthPt == 6 && Math.Abs(Lines.Single().Opacity - .7) < 1e-9);
        window.SetLineArrows(end: false); window.SetLineDash(DashStyle.Solid); window.SetWidth(2); window.SetTransparency(0);

        window.SetTool(ToolKind.Curve); await Settle();
        Point2[] parabola = [new(100, 420), new(250, 280), new(400, 420)];
        await Drag(Sampled(parabola, 24));
        var quadratic = Lines.Single(l => l.Id != straight.Id);
        Check("curve: one control point within 3px", quadratic.LineKind == LineKind.Quadratic && quadratic.Start.DistanceTo(parabola[0]) < 1 &&
            quadratic.End.DistanceTo(parabola[2]) < 1 && LineGeometry.MaxDistance(Sampled(parabola, 24), LineGeometry.ControlPolygon(quadratic)) < View.PageTransform(0).DipLengthToPoints(3),
            JsonSerializer.Serialize(quadratic.Controls));
        var shape = (ComboBox)panel.Children.OfType<ComboBox>().Last();
        Check("top bar shows the automatic choice", shape.Visibility == Visibility.Visible && shape.SelectedIndex == 1 && (string)shape.SelectedItem == "조절점 1개");

        window.SetLineShape(2);
        var elevated = Lines.Single(l => l.Id == quadratic.Id);
        Check("1 → 2 keeps the shape", elevated.LineKind == LineKind.Cubic && ShapeDifference(quadratic, elevated) < 1e-6 && shape.SelectedIndex == 2);
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("conversion undo", Lines.Single(l => l.Id == quadratic.Id) == quadratic);
        ApplicationCommands.Redo.Execute(null, window); window.Selection.Select(quadratic.Id); window.SetLineShape(1);
        Check("2 → 1 keeps ends", Lines.Single(l => l.Id == quadratic.Id) is { LineKind: LineKind.Quadratic } back && back.Start == quadratic.Start && back.End == quadratic.End);

        Point2[] s = [new(450, 120), new(500, -20), new(560, 260), new(620, 120)];
        await Drag(Sampled(s, 40));
        var cubic = Lines.Single(l => l.Id != straight.Id && l.Id != quadratic.Id);
        Check("S curve: two control points", cubic.LineKind == LineKind.Cubic && cubic.Start.DistanceTo(s[0]) < 1 && cubic.End.DistanceTo(s[^1]) < 1, JsonSerializer.Serialize(cubic.Controls));

        undo = Session.UndoCount;
        var control = cubic.Controls[1]; // the first control point lies above the page, outside the viewport
        await Drag(control, new(control.X + 30, control.Y + 15));
        var moved = Lines.Single(l => l.Id == cubic.Id);
        Check("control handle drag in the curve tool", Lines.Length == 3 && moved.Controls[1].DistanceTo(new(control.X + 30, control.Y + 15)) < 1 &&
            moved.Controls[0] == cubic.Controls[0] && moved.Start == cubic.Start && Session.UndoCount == undo + 1, $"{moved.Controls[1]}");
        ApplicationCommands.Undo.Execute(null, window);
        Check("handle drag undo", Lines.Single(l => l.Id == cubic.Id) == cubic);

        window.SetTool(ToolKind.Select); await Settle();
        await Drag(new(200, 130), new(200, 130));
        Check("select tool picks the line on its stroke", window.SelectedLine?.Id == straight.Id);
        await Drag(new(300, 160), new(320, 200));
        var endMoved = Lines.Single(l => l.Id == straight.Id);
        Check("end point handle drag", endMoved.End.DistanceTo(new(320, 200)) < 1 && endMoved.Start == straight.Start);
        await Drag(new(210, 150), new(230, 170));
        var bodyMoved = Lines.Single(l => l.Id == straight.Id);
        Check("body drag moves the whole line", bodyMoved.Transform.Transform(bodyMoved.Start).DistanceTo(new(120, 120)) < 1 &&
            bodyMoved.Transform.Transform(bodyMoved.End).DistanceTo(new(340, 220)) < 1);
        var quadraticControl = Lines.Single(l => l.Id == quadratic.Id).Controls[0];
        await Drag(quadraticControl, quadraticControl);
        Check("control point is not part of the curve", window.SelectedLine is null, $"{quadraticControl}");

        string before = Metadata(Session.Current);
        View.SetZoom(2.5); await Settle(); View.SetZoom(.5); await Settle(); View.SetZoom(1); await Settle();
        Check("zoom does not change lines", Metadata(Session.Current) == before);

        var dip = View.PageTransform(0).PageToDip.Transform(new(500, 700));
        var menu = window.OpenAnnotationMenu(new(dip.X, dip.Y))!;
        var items = menu.Items.OfType<MenuItem>().Where(i => i.Header is string).ToDictionary(i => (string)i.Header);
        Check("quick menu line tools enabled", items["직선"].IsEnabled && items["곡선"].IsEnabled);
        items["곡선"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
        Check("quick menu switches to curve", window.CurrentTool == ToolKind.Curve && ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("ShapeToolButton")).IsChecked == true);
        Check("no line from the menu", Lines.Length == 3);

        window.Selection.SetSelection([straight.Id, cubic.Id]);
        Check("copy lines", window.CopySelection());
        Check("paste lines", window.PasteSelection() && Lines.Length == 5 && Lines.Select(l => l.Id).Distinct().Count() == 5);
        ApplicationCommands.Undo.Execute(null, window);

        // One sample per dash style with arrows and partial opacity, for comparing the screen with the saved PDF.
        var pageId = Session.Current.Pages[0].Id;
        foreach (var (dash, i) in new[] { DashStyle.Solid, DashStyle.Dash, DashStyle.Dot, DashStyle.DashDot }.Select((d, i) => (d, i)))
            Session.Execute(DocumentEdits.AddAnnotation(Session.Current, pageId, new LineAnnotation(Guid.NewGuid(), false, Affine.Identity, LineKind.Quadratic,
                new(80, 560 + i * 60), new(420, 560 + i * 60), [new(250, 520 + i * 60)], 4, dash, i % 2 == 0, true, new(0x1E, 0x88, 0xE5), i < 2 ? 1 : .5)));

        string path = Path.Combine(folder, "editable.pdf");
        Check("save", await window.SaveToAsync(path) && !Session.IsDirty);
        File.WriteAllText(Path.Combine(folder, "expected.json"), Metadata(Session.Current));
        window.SetTool(ToolKind.Curve); window.Selection.Select(cubic.Id); View.SetVerticalOffset(0); View.SetHorizontalOffset(0); await Settle();
        Snapshot(logic ? "d07-logic.png" : "d07.png");
        var bytes = File.ReadAllBytes(path);
        using var engine = new PdfNotes.Pdf.Engine.PdfiumDocument(bytes);
        PdfNotes.App.Prototype.ProbeFactory.SavePng(engine.Render(0, 1.5), Path.Combine(folder, "editable-page.png"));
    }

    private static async Task Reopen()
    {
        Check("open", await window.OpenFileAsync(Path.Combine(folder, "editable.pdf")));
        Check("restart exact metadata", Metadata(Session.Current) == File.ReadAllText(Path.Combine(folder, "expected.json")));
        var cubic = Lines.Single(l => l.LineKind == LineKind.Cubic);
        window.SetTool(ToolKind.Select); window.Selection.Select(cubic.Id);
        window.SetLineShape(1); window.SetLineDash(DashStyle.Dot); window.SetLineArrows(start: true);
        string path = Path.Combine(folder, "reedited.pdf");
        Check("re-edit and save", await window.SaveToAsync(path));
        var bytes = File.ReadAllBytes(path);
        var read = EditablePdfReader.Read(bytes, PdfPageGeometryReader.Read(bytes));
        var restored = (LineAnnotation)read.Model.FindAnnotation(cubic.Id)!.Annotation;
        Check("re-edit round trip", restored.LineKind == LineKind.Quadratic && restored.Dash == DashStyle.Dot && restored.StartArrow &&
            read.RestoredCount == 7 && read.ReadOnlyCount == 0);
        using var engine = new PdfNotes.Pdf.Engine.PdfiumDocument(bytes);
        PdfNotes.App.Prototype.ProbeFactory.SavePng(engine.Render(0, 1.5), Path.Combine(folder, "reedited-page.png"));
    }

    /// <summary>Press at the first page point, move through the rest, release at the last.</summary>
    private static async Task Drag(params Point2[] points)
    {
        var m = View.PageTransform(0).PageToDip;
        var dips = points.Select(p => m.Transform(p)).Select(p => new Point(p.X, p.Y)).ToArray();
        if (logic)
        {
            var tool = View.Tool!;
            if (!tool.PointerDown(Ptr(dips[0], ModifierKeys.None))) { await Settle(); return; }
            foreach (var p in dips.Skip(1)) tool.PointerMove(Ptr(p, ModifierKeys.None));
            tool.PointerUp(Ptr(dips[^1], ModifierKeys.None));
            await Settle(); return;
        }
        Cursor(dips[0]); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(50);
        foreach (var p in dips.Skip(1)) { Cursor(p); await Task.Delay(15); }
        await Task.Delay(40);
        mouse_event(4, 0, 0, 0, 0); await Settle();
    }

    private static void Cursor(Point dip)
    {
        var p = View.PointToScreen(dip);
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    private static Task Settle() => Task.Delay(150);

    private static void Snapshot(string name)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
