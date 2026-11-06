using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Desktop.Views;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// Roadmap P4 A3–A4 under Avalonia.Headless: the canvas tools with mouse input through the window (the Editor tools and
/// the P1 view model, as in the WPF app), keys, save and reopen, pen pressure and text boxes with the P2 editor.
/// </summary>
public sealed class EditingTests
{
    private static MainWindow Window()
    {
        var settings = new AppSettings();
        var window = new MainWindow(settings, _ => { }, Headless.TempFolder(), TestWindows.Dialogs) { Width = 1200, Height = 1000 }; // the ribbon, the bars and the page list take room (A5)
        window.Script.PromptOverride = (_, _) => Answer.No;
        window.Script.MessageSink = _ => { };
        window.Show();
        window.DocumentCanvas.SetZoom(1);
        window.DocumentCanvas.GoToPage(0);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static IReadOnlyList<Annotation> Annotations(MainWindow w) => w.Document!.Session.Current.Pages[0].Annotations;

    /// <summary>The window point of a canonical point on page 0.</summary>
    private static Point At(MainWindow w, Point2 page)
    {
        var dip = w.DocumentCanvas.PageTransform(0).PageToDip.Transform(page);
        return w.DocumentCanvas.TranslatePoint(new Point(dip.X, dip.Y), w)!.Value;
    }

    private static void Drag(MainWindow w, MouseButton button, params Point2[] points)
    {
        w.MouseMove(At(w, points[0]));
        w.MouseDown(At(w, points[0]), button);
        foreach (var p in points.Skip(1)) w.MouseMove(At(w, p));
        w.MouseUp(At(w, points[^1]), button);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static void Click(MainWindow w, Point2 p, int clicks = 1)
    {
        for (int i = 0; i < clicks; i++)
        {
            w.MouseDown(At(w, p), MouseButton.Left);
            w.MouseUp(At(w, p), MouseButton.Left);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static Point2[] Line(Point2 a, Point2 b, int n = 10) =>
        [.. Enumerable.Range(0, n).Select(i => new Point2(a.X + (b.X - a.X) * i / (n - 1), a.Y + (b.Y - a.Y) * i / (n - 1)))];

    private static void Key(MainWindow w, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        w.KeyPress(key, modifiers, PhysicalKey.None, null);
        w.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void ThePenDrawsAndUndoRedoKeysWork() => Headless.Run(() =>
    {
        var w = Window();
        w.SetTool(ToolKind.Pen);
        Drag(w, MouseButton.Left, Line(new(100, 100), new(300, 160)));
        var ink = Assert.IsType<InkStroke>(Assert.Single(Annotations(w)));
        Assert.True(ink.Points.Length >= 2);
        Assert.False(ink.HasPressure); // a mouse has no pressure
        Assert.Equal(w.ViewModel.ToolStyles.Pen.Color, ink.Color);
        Key(w, Avalonia.Input.Key.Z, RawInputModifiers.Control);
        Assert.Empty(Annotations(w));
        Key(w, Avalonia.Input.Key.Y, RawInputModifiers.Control);
        Assert.Single(Annotations(w));
        w.Close();
    });

    [Fact]
    public void ErasersSelectionLassoLinesAndShapes() => Headless.Run(() =>
    {
        var w = Window();
        w.SetTool(ToolKind.Pen);
        Drag(w, MouseButton.Left, Line(new(100, 100), new(300, 100)));
        Drag(w, MouseButton.Left, Line(new(100, 200), new(300, 200)));
        Assert.Equal(2, Annotations(w).Count);

        // The middle button erases with the area eraser while the pen stays the tool (spec 7.5).
        Drag(w, MouseButton.Middle, Line(new(200, 80), new(200, 120)));
        Assert.Equal(3, Annotations(w).Count); // the first stroke cut in two
        Assert.Equal(ToolKind.Pen, w.CurrentTool);

        w.SetTool(ToolKind.StrokeEraser);
        Drag(w, MouseButton.Left, Line(new(150, 190), new(150, 210), 4));
        Assert.Equal(2, Annotations(w).Count);

        // Select a piece and move it.
        w.SetTool(ToolKind.Select);
        var piece = (InkStroke)Annotations(w)[0];
        var on = piece.Transform.Transform(piece.Outline[0][0]);
        var inside = AnnotationGeometry.LocalBounds(piece);
        var center = new Point2(inside.X + inside.Width / 2, inside.Y + inside.Height / 2);
        Click(w, center);
        Assert.Equal([piece.Id], w.Selection.SelectedIds);
        Drag(w, MouseButton.Left, Line(center, new(center.X + 40, center.Y + 60), 6));
        var moved = Annotations(w).Single(a => a.Id == piece.Id);
        Assert.NotEqual(piece.Transform, moved.Transform);
        Key(w, Avalonia.Input.Key.Escape);
        Assert.Empty(w.Selection.SelectedIds);
        Key(w, Avalonia.Input.Key.Delete);
        Assert.Equal(2, Annotations(w).Count);

        // The lasso selects what it encloses and hands over to the selection tool.
        w.SetTool(ToolKind.Lasso);
        Drag(w, MouseButton.Left, new(60, 60), new(360, 60), new(360, 400), new(60, 400), new(60, 60));
        Assert.Equal(ToolKind.Select, w.CurrentTool);
        Assert.NotEmpty(w.Selection.SelectedIds);
        Key(w, Avalonia.Input.Key.Delete);
        Assert.Empty(Annotations(w));

        w.SetTool(ToolKind.Line);
        Drag(w, MouseButton.Left, Line(new(100, 500), new(300, 560), 6));
        Assert.IsType<LineAnnotation>(Assert.Single(Annotations(w)));
        w.SetTool(ToolKind.Shape);
        Drag(w, MouseButton.Left, Line(new(320, 500), new(420, 600), 6));
        Assert.Contains(Annotations(w), a => a is ShapeAnnotation);
        w.Close();
    });

    [Fact]
    public void APenWithPressureAndItsEraserEnd() => Headless.Run(() =>
    {
        var w = Window();
        w.SetTool(ToolKind.Pen);
        var canvas = w.DocumentCanvas;
        CanvasPointer Pen(Point2 page, double pressure, bool eraser = false)
        {
            var dip = canvas.PageTransform(0).PageToDip.Transform(page);
            return new(dip, InputModifiers.None, Device: PointerDevice.Pen, Pressure: pressure, PointerId: 7, Eraser: eraser);
        }
        var path = Line(new(100, 300), new(400, 340), 12);
        canvas.ProbePen(Pen(path[0], .1), 0);
        for (int i = 1; i < path.Length; i++) canvas.ProbePen(Pen(path[i], .1 + .8 * i / 11), 1);
        canvas.ProbePen(Pen(path[^1], 0), 2);
        var stroke = Assert.IsType<InkStroke>(Assert.Single(Annotations(w)));
        Assert.True(stroke.HasPressure && stroke.Pressures[^1] > stroke.Pressures[0] + .5);
        w.ViewModel.Settings.Eraser = ToolKind.StrokeEraser;
        var across = Line(new(250, 280), new(255, 360), 6);
        canvas.ProbePen(Pen(across[0], .5, eraser: true), 0);
        foreach (var p in across.Skip(1)) canvas.ProbePen(Pen(p, .5, eraser: true), 1);
        canvas.ProbePen(Pen(across[^1], .5, eraser: true), 2);
        Assert.Empty(Annotations(w));
        Assert.Equal(ToolKind.Pen, w.CurrentTool);
        w.Close();
    });

    [Fact]
    public void SaveAndReopenKeepTheDrawing() => Headless.Run(async () =>
    {
        var w = Window();
        w.SetTool(ToolKind.Pen);
        Drag(w, MouseButton.Left, Line(new(100, 100), new(300, 160)));
        w.SetTool(ToolKind.Shape);
        Drag(w, MouseButton.Left, Line(new(320, 500), new(420, 600), 6));
        string path = Path.Combine(Headless.TempFolder(), "saved.pdf");
        Assert.True(await w.SaveToAsync(path));
        var kinds = Annotations(w).Select(a => a.Kind).ToArray();
        w.Close();

        var again = Window();
        Assert.True(await again.OpenFileAsync(path));
        Assert.Equal(kinds, Annotations(again).Select(a => a.Kind).ToArray());
        again.Close();
    });

    [Fact]
    public void ATextBoxIsTypedFinishedAndUndoneInOneStep() => Headless.Run(() =>
    {
        var w = Window();
        w.SetTool(ToolKind.Text);
        int steps = w.Document!.Session.UndoCount;
        Click(w, new(100, 400));
        Assert.True(w.IsEditingText);
        Assert.NotNull(w.ActiveTextEditor);
        w.KeyTextInput("abc 한글");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        // Written into the document while typing (other views see it), not drawn by the canvas meanwhile.
        Assert.Equal("abc 한글", Assert.IsType<TextBoxAnnotation>(Assert.Single(Annotations(w))).Text);
        Key(w, Avalonia.Input.Key.Escape);
        Assert.False(w.IsEditingText);
        Assert.Equal(steps + 1, w.Document.Session.UndoCount);
        w.Undo();
        Assert.Empty(Annotations(w));

        // A box left blank leaves nothing, not even an Undo step.
        Click(w, new(100, 500));
        Assert.True(w.IsEditingText);
        Key(w, Avalonia.Input.Key.Escape);
        Assert.Empty(Annotations(w));
        Assert.Equal(steps, w.Document.Session.UndoCount);
        w.Close();
    });

    [Fact]
    public void AnExistingBoxIsEditedAndAnImeCompositionIsCommittedOnFinish() => Headless.Run(() =>
    {
        var w = Window();
        w.SetTool(ToolKind.Text);
        Click(w, new(100, 400));
        w.KeyTextInput("첫 글");
        Key(w, Avalonia.Input.Key.Escape);
        var box = Assert.IsType<TextBoxAnnotation>(Assert.Single(Annotations(w)));
        int steps = w.Document!.Session.UndoCount;

        // The text tool on the box selects it; a second press on it edits.
        var inside = new Point2(box.Transform.OffsetX + 10, box.Transform.OffsetY + 8);
        Click(w, inside);
        Assert.Equal(ToolKind.Select, w.CurrentTool);
        Assert.Equal([box.Id], w.Selection.SelectedIds);
        int releases = 0; bool? lastChanged = null;
        w.Selection.Released += (_, changed) => { releases++; lastChanged = changed; };
        Click(w, inside);
        Assert.True(w.IsEditingText, $"releases {releases}, changed {lastChanged}, return {w.ViewModel.Tools.Return}, selected {w.Selection.SelectedIds.Count}");
        var model = w.EditingText is not null ? w.ActiveTextEditor!.Model! : null;
        model!.Select(box.Text.Length, box.Text.Length);
        // An IME composition is not in the document until it is committed; finishing commits it (spec 6.1).
        model.SetComposition("하");
        Assert.Equal("첫 글", ((TextBoxAnnotation)Annotations(w)[0]).Text);
        w.FinishTextEditing();
        Assert.Equal("첫 글하", ((TextBoxAnnotation)Annotations(w)[0]).Text);
        Assert.Equal(steps + 1, w.Document.Session.UndoCount);
        w.Undo();
        Assert.Equal("첫 글", ((TextBoxAnnotation)Annotations(w)[0]).Text);
        Assert.Equal([box.Id], w.Selection.SelectedIds); // the Undo shows what it changed (spec 13.3)
        Key(w, Avalonia.Input.Key.Escape);
        Assert.Empty(w.Selection.SelectedIds);

        // With the selection tool chosen directly a typed key does not edit the box (spec 5.4: only a visit from the text tool).
        w.SetTool(ToolKind.Select);
        Thread.Sleep(700); // a new click, not the second half of a double click
        Click(w, inside);
        Assert.Equal([box.Id], w.Selection.SelectedIds);
        w.KeyTextInput("?");
        Assert.False(w.IsEditingText);
        // Selected from the text tool, a typed key starts editing it with that key.
        Key(w, Avalonia.Input.Key.Escape);
        w.SetTool(ToolKind.Text);
        Thread.Sleep(700);
        Click(w, inside); // the text tool selects it (and the canvas has the keyboard)
        Assert.Equal([box.Id], w.Selection.SelectedIds);
        Assert.False(w.IsEditingText);
        w.KeyTextInput("!");
        Assert.True(w.IsEditingText);
        Key(w, Avalonia.Input.Key.Escape);
        Assert.EndsWith("!", ((TextBoxAnnotation)Annotations(w)[0]).Text);
        w.Close();
    });
}
