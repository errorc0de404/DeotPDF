using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
using DashStyle = PdfNotes.Core.Models.DashStyle;
using TextAlignment = PdfNotes.Core.Models.TextAlignment;

namespace PdfNotes.Probe;

/// <summary>
/// D08 real-app check: stroke/area eraser (fast moves, live preview, Esc, edge-only cut, zoom, rotated/scaled strokes,
/// locks, one Undo per rub), lasso (open path, complete containment, locked/partial exclusion, click pick, Shift add,
/// dragging the lassoed group, Delete), quick menu and toolbar sync, save → separate-process reopen → edit → save.
/// With <c>logic</c> the pointer events go to the canvas tool directly (no OS input) and are reported as such.
/// </summary>
internal static class D08Check
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static bool logic;
    private static readonly List<object> checks = [];
    private static readonly List<string> messages = [];
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => (DocumentView)window.FindName("View");
    private static IReadOnlyList<Annotation> Annotations => Session.Current.Pages[0].Annotations;
    private const byte VkShift = 0x10, VkEscape = 0x1B, VkDelete = 0x2E;

    private static readonly RgbColor Red = new(0xE5, 0x39, 0x35), Blue = new(0x1E, 0x88, 0xE5), Green = new(0x43, 0xA0, 0x47);

    private static string Metadata(DocumentModel model)
    {
        var data = SchemaV3.Serialize(model);
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
                string name = reopen ? "d08-reopen-result.json" : logic ? "d08-logic-result.json" : "d08-result.json";
                File.WriteAllText(Path.Combine(folder, name), JsonSerializer.Serialize(new { Passed = result == 0, Input = reopen || logic ? "tool-calls" : "os-mouse", Checks = checks, Messages = messages },
                    new JsonSerializerOptions { WriteIndented = true }));
                window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); app.Shutdown();
            }
        };
        app.Run();
        if (!reopen && result == 0)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("d08-reopen"); start.ArgumentList.Add(folder);
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

    private static InkStroke Ink(double y, double width, RgbColor color, bool locked = false) =>
        new(Guid.NewGuid(), locked, Affine.Identity, [new(100, y), new(250, y), new(400, y)], width, color, 1);

    private static Annotation[] Pieces(Guid originalId, IEnumerable<Guid> before) =>
        Annotations.Where(a => a.Id == originalId || !before.Contains(a.Id)).ToArray();

    private static bool Painted(Point2 p) => Annotations.Any(a => AnnotationGeometry.Hit(a, p, 0, PdfNotes.Text.TextLayout.Bounds));

    private static async Task Edit()
    {
        View.SetZoom(1); View.SetVerticalOffset(0); View.SetHorizontalOffset(0); await Settle();
        var pageId = Session.Current.Pages[0].Id;
        var thick = Ink(100, 20, Red);
        var thin = Ink(170, 4, Blue);
        var highlight = new FreeHighlightStroke(Guid.NewGuid(), false, Affine.Identity, [new(100, 230), new(400, 230)], 14, new(0xFF, 0xE6, 0x00), .4);
        var locked = Ink(290, 4, Green, locked: true);
        var line = new LineAnnotation(Guid.NewGuid(), false, Affine.Identity, LineKind.Straight, new(100, 340), new(400, 340), [], 3, DashStyle.Solid, false, true, Blue, 1);
        var turned = AnnotationGeometry.Similarity(new InkStroke(Guid.NewGuid(), false, Affine.Rigid(Math.PI / 6, 120, 380), [new(0, 0), new(120, 0)], 4, Red, 1),
            new(120, 380), 1.5);
        var text = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Translation(450, 90), "지우개 제외", [new TextRun(0, 6, "Malgun Gothic", 14, RgbColor.Black, false, false, false, false)],
            TextAlignment.Left, 110, TextBoxStyle.Default, []);
        foreach (var a in new Annotation[] { thick, thin, highlight, locked, line, turned, text })
            Session.Execute(DocumentEdits.AddAnnotation(Session.Current, pageId, a));
        Session.MarkSaved(Session.CreateSnapshot());
        await Settle();

        // ----- stroke eraser -----
        PressToolButton("EraserToolButton");
        window.SetTool(ToolKind.StrokeEraser); await Settle();
        var bar = (Border)window.FindName("EraserBorder");
        Check("eraser bar replaces the style bar", bar.Visibility == Visibility.Visible && ((Border)window.FindName("StyleBar")).Visibility == Visibility.Collapsed &&
            ((ToggleButton)window.FindName("StrokeEraserButton")).IsChecked == true && window.StrokeEraserDiameter == 10 && window.AreaEraserDiameter == 20,
            $"stroke {window.StrokeEraserDiameter}pt, area {window.AreaEraserDiameter}pt");

        await Hover(new(520, 450));
        var ring = RingDarkness(new(520, 450), 10 / 2.0);
        Check("hover shows the erase circle in page points", ring < 225, $"darkest ring pixel {ring}");

        var before = Annotations.ToArray();
        int undo = Session.UndoCount;
        await Drag(new(250, 150), new(250, 310)); // one fast move: only its two ends are input points
        Check("stroke eraser removes touched pen/highlighter strokes between input points", Annotations.Select(a => a.Id).SequenceEqual(
            [thick.Id, locked.Id, line.Id, turned.Id, text.Id]) && Session.UndoCount == undo + 1 && Session.UndoLabel == "undo.strokeEraser" && Session.IsDirty,
            string.Join(",", Annotations.Select(a => a.Kind)));
        Check("locked stroke and line are not erased", Annotations.Any(a => a.Id == locked.Id) && Annotations.Any(a => a.Id == line.Id));
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("stroke erase undo restores ids, shapes and order", Annotations.SequenceEqual(before));

        // ----- area eraser -----
        ((RadioButton)window.FindName("AreaEraserButton")).IsChecked = true; await Settle();
        Check("eraser bar switches mode", window.CurrentTool == ToolKind.AreaEraser && ((ToggleButton)window.FindName("EraserToolButton")).IsChecked == true);
        undo = Session.UndoCount;
        var ids = Annotations.Select(a => a.Id).ToHashSet();
        await Drag(new(250, 60), new(250, 250));
        Check("area eraser splits each stroke in its swept band", Annotations.Count == before.Length + 3 && Session.UndoCount == undo + 1 &&
            Session.UndoLabel == "undo.areaEraser" && Annotations.OfType<StrokeAnnotation>().Count(s => s.IsOutlined) == 6,
            $"{Annotations.Count} annotations, outlined {Annotations.OfType<StrokeAnnotation>().Count(s => s.IsOutlined)}");
        Check("swept band is empty, the rest stays", !Painted(new(250, 100)) && !Painted(new(259, 170)) && Painted(new(239, 100)) && Painted(new(261, 230)) &&
            Painted(new(250, 290)), "locked stroke at y=290 kept");
        var hole = WindowColor(new(250, 100));
        var kept = WindowColor(new(200, 100));
        Check("page background shows through the cut", hole.R > 240 && hole.G > 240 && hole.B > 240 && kept.R > 180 && kept.G < 110, $"cut {hole}, kept {kept}");
        Check("pieces stay at the original stacking position", Annotations.Take(6).All(a => a is StrokeAnnotation { IsOutlined: true }) &&
            Annotations[6].Id == locked.Id, string.Join(",", Annotations.Select(a => a.Kind)));
        Snapshot(logic ? "d08-logic-erased.png" : "d08-erased.png");
        var erased = Annotations.ToArray();
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("one Undo restores all three strokes", Annotations.SequenceEqual(before));
        ApplicationCommands.Redo.Execute(null, window); await Settle();
        Check("Redo repeats the erase", Annotations.SequenceEqual(erased));

        // Only the top edge of the thick stroke (y 90..110): the eraser reaches y = 92.
        window.SetEraserSize(10);
        Check("eraser size is stored in points", window.AreaEraserDiameter == 10 && ((TextBox)window.FindName("EraserSizeBox")).Text == "10");
        int count = Annotations.Count;
        await Drag(new(120, 87), new(150, 87), new(180, 87), new(220, 87));
        Check("edge-only erase keeps the rest of a thick stroke", Annotations.Count == count && !Painted(new(170, 91)) && Painted(new(170, 93)) &&
            Painted(new(170, 109)) && Painted(new(105, 91)), $"{Annotations.Count} annotations");
        var edge = WindowColor(new(170, 90.6));
        var below = WindowColor(new(170, 96));
        Check("erased edge shows the page", edge.R > 235 && edge.G > 235 && below.R > 180 && below.G < 110, $"edge {edge}, below {below}");

        // Live preview without touching the document, then Esc while the button is still down.
        window.SetEraserSize(20);
        undo = Session.UndoCount;
        var snapshot = Annotations.ToArray();
        await Press(new(330, 170));
        await MoveTo(new(345, 170)); await MoveTo(new(360, 170)); await Settle();
        var live = WindowColor(new(345, 170));
        Check("live preview while rubbing, document unchanged", live.B > 240 && live.R > 240 && Session.UndoCount == undo && Annotations.SequenceEqual(snapshot),
            $"pixel {live}");
        await Key(VkEscape); await Settle();
        var restored = WindowColor(new(345, 170));
        await Release(new(360, 170));
        Check("Esc discards the rub", Session.UndoCount == undo && Annotations.SequenceEqual(snapshot) && restored.B > 180 && restored.R < 120, $"pixel {restored}");

        // Zoom changes the circle on screen, not the erased size in points.
        View.SetZoom(2); View.ScrollToPagePoint(0, new(150, 170)); await Settle();
        var leftThin = Annotations.Single(a => a.Kind == AnnotationKind.Ink && AnnotationGeometry.Hit(a, new(150, 170), 0));
        ids = Annotations.Select(a => a.Id).ToHashSet();
        await Drag(new(150, 170), new(150, 170));
        var split = Pieces(leftThin.Id, ids).Select(a => AnnotationGeometry.Bounds([a])).OrderBy(b => b.X).ToArray();
        double gap = split.Length == 2 ? split[1].X - (split[0].X + split[0].Width) : double.NaN;
        Check("20pt circle at 200% removes 20pt", split.Length == 2 && Math.Abs(gap - 2 * Math.Sqrt(100 - 4)) < .3, $"gap {gap:0.###}pt");
        View.SetZoom(1); View.SetVerticalOffset(0); View.SetHorizontalOffset(0); await Settle();

        // Rotated and scaled stroke.
        var center = turned.Transform.Transform(new(90, 0));
        ids = Annotations.Select(a => a.Id).ToHashSet();
        await Drag(center, center);
        var turnedPieces = Pieces(turned.Id, ids);
        Check("rotated, scaled stroke is cut where the circle is", turnedPieces.Length == 2 && !Painted(center) &&
            Painted(turned.Transform.Transform(new(90 - 16, 0))) && turnedPieces.All(a => a.Transform == turned.Transform),
            $"{turnedPieces.Length} pieces");

        // ----- lasso -----
        PressToolButton("LassoToolButton"); await Settle();
        Check("lasso tool button", window.CurrentTool == ToolKind.Lasso);
        var thinPieces = Annotations.Where(a => a is InkStroke { Color: var c } && c == Blue).Select(a => a.Id).ToHashSet();
        undo = Session.UndoCount;
        string unchanged = Metadata(Session.Current);
        // An open path: left, bottom and right sides only; the top is closed automatically.
        await Drag(new(80, 150), new(80, 170), new(80, 192), new(200, 192), new(300, 192), new(420, 192), new(420, 170), new(420, 150));
        Check("open lasso closes and selects the enclosed pieces", window.Selection.SelectedIds.ToHashSet().SetEquals(thinPieces) && thinPieces.Count == 3,
            $"{window.Selection.SelectedIds.Count} selected of {thinPieces.Count}");
        Check("lasso changes no document state", Session.UndoCount == undo && Metadata(Session.Current) == unchanged);
        Check("a lasso selection switches to the selection tool", window.CurrentTool == ToolKind.Select &&
            ((ToggleButton)window.FindName("SelectToolButton")).IsChecked == true && window.Selection.SelectedIds.Count == 3);
        undo = Session.UndoCount;
        var beforeMove = Annotations.Where(a => thinPieces.Contains(a.Id)).ToDictionary(a => a.Id);
        await Drag(new(120, 170), new(120, 180), new(120, 190)); // on a selected piece, with the selection tool
        var moved = Annotations.Where(a => thinPieces.Contains(a.Id)).ToArray();
        Check("the lassoed group moves together right away", Session.UndoCount == undo + 1 && moved.All(a =>
            Math.Abs(a.Transform.OffsetY - beforeMove[a.Id].Transform.OffsetY - 20) < 1.5), $"undo {Session.UndoLabel}");
        ApplicationCommands.Undo.Execute(null, window); await Settle();

        window.SetTool(ToolKind.Lasso); await Settle();
        Check("returning to the lasso keeps the selection", window.Selection.SelectedIds.Count == 3);
        await Drag(new(80, 270), new(80, 310), new(420, 310), new(420, 270));
        Check("locked stroke is never lassoed", window.Selection.SelectedIds.Count == 0 && window.CurrentTool == ToolKind.Lasso);
        await Drag(new(80, 320), new(80, 360), new(250, 360), new(250, 320));
        Check("a lasso over part of a line selects it", window.Selection.SelectedIds.SequenceEqual([line.Id]) && window.CurrentTool == ToolKind.Select);
        window.SetTool(ToolKind.Lasso); await Settle();
        var thickRight = Annotations.Single(a => a is InkStroke && AnnotationGeometry.Hit(a, new(350, 100), 0)).Id;
        await Drag(new(330, 80), new(330, 104), new(370, 104), new(370, 80)); // only the upper half of a short part
        Check("a lasso over part of a thick stroke selects it", window.Selection.SelectedIds.SequenceEqual([thickRight]));
        window.SetTool(ToolKind.Lasso); await Settle();
        await Drag(new(150, 100), new(151, 101));
        var clicked = window.Selection.SelectedIds.SingleOrDefault();
        Check("short drag is a click that picks the stroke", clicked != Guid.Empty && Annotations.Single(a => a.Id == clicked) is InkStroke { Color: var cc } && cc == Red &&
            window.CurrentTool == ToolKind.Select);
        window.SetTool(ToolKind.Lasso); await Settle();
        shift = true;
        if (!logic) keybd_event(VkShift, 0, 0, 0);
        try { await Drag(new(440, 80), new(440, 140), new(580, 140), new(580, 80)); }
        finally { if (!logic) keybd_event(VkShift, 0, 2, 0); shift = false; }
        Check("Shift+lasso adds the text box", window.Selection.SelectedIds.ToHashSet().SetEquals([clicked, text.Id]), $"{window.Selection.SelectedIds.Count}");
        count = Annotations.Count; undo = Session.UndoCount;
        await Key(VkDelete); await Settle();
        Check("Delete removes the lassoed annotations", Annotations.Count == count - 2 && Session.UndoCount == undo + 1);
        ApplicationCommands.Undo.Execute(null, window); await Settle();
        Check("delete undo", Annotations.Count == count);

        // ----- quick menu and toolbar -----
        var dip = View.PageTransform(0).PageToDip.Transform(new(500, 480));
        var menu = window.OpenAnnotationMenu(new(dip.X, dip.Y))!;
        var items = menu.Items.OfType<MenuItem>().Where(i => i.Header is string).ToDictionary(i => (string)i.Header);
        Check("quick menu offers lasso and both erasers", items["올가미"].IsEnabled && items["획 지우개"].IsEnabled && items["영역 지우개"].IsEnabled &&
            items["선택"].IsEnabled && !items.ContainsKey("글자 선택 하이라이트")); // one highlighter since D14g
        items["획 지우개"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false; await Settle();
        Check("quick menu switches to the stroke eraser and syncs the bar", window.CurrentTool == ToolKind.StrokeEraser &&
            ((ToggleButton)window.FindName("EraserToolButton")).IsChecked == true && ((ToggleButton)window.FindName("StrokeEraserButton")).IsChecked == true);
        PressToolButton("PenToolButton"); await Settle();
        PressToolButton("EraserToolButton"); await Settle();
        Check("toolbar eraser returns to the last eraser", window.CurrentTool == ToolKind.StrokeEraser);
        Check("menu and tool switches leave the document unchanged", Annotations.Count == count);

        // Pen still draws over pieces, and a stroke drawn now is erased by the stroke eraser in one move.
        window.SetTool(ToolKind.Pen); await Settle();
        await Drag(new(450, 300), new(500, 310), new(560, 300));
        var drawn = Annotations[^1];
        Check("pen draws over the page", drawn is InkStroke { IsOutlined: false });
        window.SetTool(ToolKind.StrokeEraser); await Settle();
        await Drag(new(500, 280), new(500, 330));
        Check("freshly drawn pen stroke is stroke-erased", Annotations.All(a => a.Id != drawn.Id));
        ApplicationCommands.Undo.Execute(null, window); await Settle();

        string path = Path.Combine(folder, "editable.pdf");
        Check("save", await window.SaveToAsync(path) && !Session.IsDirty);
        File.WriteAllText(Path.Combine(folder, "expected.json"), Metadata(Session.Current));
        window.SetTool(ToolKind.Lasso); window.Selection.SetSelection(thinPieces); View.SetVerticalOffset(0); await Settle();
        Snapshot(logic ? "d08-logic.png" : "d08.png");
        var bytes = File.ReadAllBytes(path);
        using var engine = new PdfNotes.Pdf.Engine.PdfiumDocument(bytes);
        PdfNotes.App.Prototype.ProbeFactory.SavePng(engine.Render(0, 1.5), Path.Combine(folder, "editable-page.png"));
    }

    private static async Task Reopen()
    {
        Check("open", await window.OpenFileAsync(Path.Combine(folder, "editable.pdf")));
        Check("restart exact metadata (schema v3 pieces)", Metadata(Session.Current) == File.ReadAllText(Path.Combine(folder, "expected.json")));
        var pieces = Annotations.OfType<StrokeAnnotation>().Where(s => s.IsOutlined).ToArray();
        Check("pieces reopen editable", pieces.Length >= 8 && messages.Count == 0, $"{pieces.Length} pieces, messages {messages.Count}");
        var piece = pieces.First(p => p.Color == Red && AnnotationGeometry.Bounds([p]).Y < 120);
        window.SetTool(ToolKind.Select); window.Selection.Select(piece.Id); await Settle();
        window.Selection.Nudge(0, 1, large: true);
        Check("reopened piece moves", Annotations.Single(a => a.Id == piece.Id).Transform.OffsetY > piece.Transform.OffsetY + 1);
        window.SetTool(ToolKind.AreaEraser); window.SetEraserSize(30); await Settle();
        int count = Annotations.Count;
        var moved = (StrokeAnnotation)Annotations.Single(a => a.Id == piece.Id);
        var target = AnnotationGeometry.Bounds([moved]);
        var at = new Point2(target.X + target.Width / 2, target.Y + target.Height / 2);
        var dip = View.PageTransform(0).PageToDip.Transform(at);
        var tool = View.Tool!;
        tool.PointerDown(Ptr(new Point(dip.X, dip.Y), ModifierKeys.None)); tool.PointerUp(Ptr(new Point(dip.X, dip.Y), ModifierKeys.None)); await Settle();
        Check("reopened piece is erased again", Annotations.Count == count + 1 && !Painted(at));
        string path = Path.Combine(folder, "reedited.pdf");
        Check("re-edit and save", await window.SaveToAsync(path));
        var bytes = File.ReadAllBytes(path);
        var read = EditablePdfReader.Read(bytes, PdfPageGeometryReader.Read(bytes));
        Check("re-edit round trip", read.RestoredCount == Annotations.Count && read.ReadOnlyCount == 0 && Metadata(read.Model) == Metadata(Session.Current),
            $"restored {read.RestoredCount}, read-only {read.ReadOnlyCount}");
        using var engine = new PdfNotes.Pdf.Engine.PdfiumDocument(bytes);
        PdfNotes.App.Prototype.ProbeFactory.SavePng(engine.Render(0, 1.5), Path.Combine(folder, "reedited-page.png"));
    }

    // ----- input -----

    private static void PressToolButton(string name)
    {
        var button = (ToggleButton)window.FindName(name);
        button.IsChecked = true;
    }

    private static Point Dip(Point2 page)
    {
        var p = View.PageTransform(0).PageToDip.Transform(page);
        return new(p.X, p.Y);
    }

    private static async Task Drag(params Point2[] points)
    {
        await Press(points[0]);
        foreach (var p in points.Skip(1)) await MoveTo(p);
        await Task.Delay(40);
        await Release(points[^1]);
        await Settle();
    }

    private static bool shift;
    private static ModifierKeys Modifiers => shift ? ModifierKeys.Shift : ModifierKeys.None;

    private static async Task Press(Point2 page)
    {
        if (logic) { View.Tool!.PointerDown(Ptr(Dip(page), Modifiers)); return; }
        Cursor(Dip(page)); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(50);
    }

    private static async Task MoveTo(Point2 page)
    {
        if (logic) { if (View.Tool!.IsActive) View.Tool.PointerMove(Ptr(Dip(page), Modifiers)); return; }
        Cursor(Dip(page)); await Task.Delay(20);
    }

    private static async Task Release(Point2 page)
    {
        if (logic) { if (View.Tool!.IsActive) View.Tool.PointerUp(Ptr(Dip(page), Modifiers)); return; }
        mouse_event(4, 0, 0, 0, 0); await Task.Delay(30);
    }

    private static async Task Hover(Point2 page)
    {
        if (logic) { (View.Tool as IHoverTool)?.PointerHover(Dip(page)); await Settle(); return; }
        Cursor(Dip(new(page.X - 5, page.Y))); await Task.Delay(40);
        Cursor(Dip(page)); await Settle();
    }

    private static async Task Key(byte key)
    {
        if (logic)
        {
            if (key == VkEscape) View.CancelTool();
            else if (key == VkDelete) Session.Execute(DocumentEdits.DeleteAnnotations(Session.Current, window.Selection.SelectedIds)); // no OS key in logic mode
            await Settle(); return;
        }
        View.Focus();
        keybd_event(key, 0, 0, 0); await Task.Delay(30); keybd_event(key, 0, 2, 0); await Task.Delay(30);
    }

    private static void Cursor(Point dip)
    {
        var p = View.PointToScreen(dip);
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    private static Task Settle() => Task.Delay(150);

    // ----- pixels -----

    private static RenderTargetBitmap Capture()
    {
        double dpi = View.DpiScale;
        var bitmap = new RenderTargetBitmap((int)Math.Round(window.ActualWidth * dpi), (int)Math.Round(window.ActualHeight * dpi),
            96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        return bitmap;
    }

    private static Color WindowColor(Point2 page) => Pixel(Capture(), Dip(page));

    private static Color Pixel(RenderTargetBitmap bitmap, Point dip)
    {
        var inWindow = View.TranslatePoint(dip, window);
        double dpi = View.DpiScale;
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)Math.Floor(inWindow.X * dpi), (int)Math.Floor(inWindow.Y * dpi), 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    /// <summary>Darkest red channel on a short horizontal scan across the expected circle edge (radius in points).</summary>
    private static int RingDarkness(Point2 center, double radiusPt)
    {
        var bitmap = Capture();
        var c = Dip(center);
        double r = radiusPt * View.PageTransform(0).DipsPerPagePoint;
        int darkest = 255;
        for (double dx = -3; dx <= 3; dx += .5) darkest = Math.Min(darkest, Pixel(bitmap, new(c.X + r + dx, c.Y)).R);
        return darkest;
    }

    private static void Snapshot(string name)
    {
        var bitmap = Capture();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
