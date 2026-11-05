using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Desktop.Views;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// A7 regression, common UI (WPF Probes d04, d09, d11e, d13, d13h): zoom and page keys, the Esc ladder, cancelling a
/// stroke, the zoom menu, PDF text (highlighter and copy), recent files, links, shapes and polygons.
/// </summary>
public sealed class UiRegressionTests
{
    private static async Task<MainWindow> Opened(int pages = 3, string? pdf = null)
    {
        string folder = Headless.TempFolder();
        if (pdf is null) { pdf = Path.Combine(folder, "doc.pdf"); ProbeSamplePdf.Create(pdf, pages); }
        var window = new MainWindow(new AppSettings(), _ => { }, folder) { Width = 1200, Height = 1000 };
        window.PromptOverride = (_, _) => Answer.No;
        window.MessageSink = _ => { };
        window.EditorClipboard.InMemory = true;
        window.Show();
        Assert.True(await window.OpenFileAsync(pdf));
        window.DocumentCanvas.SetZoom(1);
        window.DocumentCanvas.GoToPage(0);
        window.DocumentCanvas.Focus();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static IReadOnlyList<Annotation> Annotations(MainWindow w, int page = 0) => w.Document!.Session.Current.Pages[page].Annotations;

    private static Point At(MainWindow w, Point2 page, int index = 0)
    {
        var dip = w.DocumentCanvas.PageTransform(index).PageToDip.Transform(page);
        return w.DocumentCanvas.TranslatePoint(new Point(dip.X, dip.Y), w)!.Value;
    }

    private static void Drag(MainWindow w, Point2 a, Point2 b, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        w.MouseMove(At(w, a), modifiers);
        w.MouseDown(At(w, a), MouseButton.Left, modifiers);
        for (int i = 1; i <= 10; i++) w.MouseMove(At(w, new(a.X + (b.X - a.X) * i / 10, a.Y + (b.Y - a.Y) * i / 10)), modifiers);
        w.MouseUp(At(w, b), MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A point inside a glyph near its start or end (as the WPF Probe d09).</summary>
    private static Point2 Edge(TextGlyph g, bool end)
    {
        double f = end ? .8 : .2;
        return new((g.Box.UpperLeft.X + g.Box.LowerLeft.X) / 2 + (g.Box.UpperRight.X - g.Box.UpperLeft.X) * f, g.Center.Y);
    }

    private static void DragAlong(MainWindow w, params Point2[] points)
    {
        w.MouseMove(At(w, points[0]));
        w.MouseDown(At(w, points[0]), MouseButton.Left);
        for (int k = 1; k < points.Length; k++)
            for (int i = 1; i <= 5; i++)
                w.MouseMove(At(w, new(points[k - 1].X + (points[k].X - points[k - 1].X) * i / 5, points[k - 1].Y + (points[k].Y - points[k - 1].Y) * i / 5)));
        w.MouseUp(At(w, points[^1]), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(MainWindow w, Point2 p, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        w.MouseMove(At(w, p), modifiers);
        w.MouseDown(At(w, p), MouseButton.Left, modifiers);
        w.MouseUp(At(w, p), MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(MainWindow w, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        w.KeyPress(key, modifiers, PhysicalKey.None, null);
        w.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>d13h: Ctrl+± zoom within 10–800 %, PageDown, Ctrl+End and Ctrl+Home; none of it changes the document.</summary>
    [Fact]
    public void ZoomAndPageKeys() => Headless.Run(async () =>
    {
        var w = await Opened();
        var canvas = w.DocumentCanvas;
        double start = canvas.Zoom;
        Press(w, Key.OemPlus, RawInputModifiers.Control);
        Assert.True(canvas.Zoom > start);
        Press(w, Key.OemMinus, RawInputModifiers.Control);
        Press(w, Key.Subtract, RawInputModifiers.Control);
        Assert.True(canvas.Zoom < start);
        for (int i = 0; i < 40; i++) Press(w, Key.OemMinus, RawInputModifiers.Control);
        Assert.Equal(0.1, canvas.Zoom, 3);
        for (int i = 0; i < 60; i++) Press(w, Key.Add, RawInputModifiers.Control);
        Assert.Equal(8, canvas.Zoom, 3);
        canvas.SetZoom(1);
        canvas.GoToPage(0);
        Press(w, Key.PageDown);
        Assert.Equal(1, canvas.CurrentPageIndex);
        Press(w, Key.End, RawInputModifiers.Control);
        Assert.Equal(2, canvas.CurrentPageIndex);
        Press(w, Key.Home, RawInputModifiers.Control);
        Assert.Equal(0, canvas.CurrentPageIndex);
        Assert.False(w.Document!.Session.IsDirty);
        w.Close();
    });

    /// <summary>d13h, d04: Esc from a drawing tool goes to the selection tool; Esc or another button cancels a stroke in
    /// progress (and the right button opens no menu then); a click makes a dot.</summary>
    [Fact]
    public void EscAndOtherButtonsCancelAStroke() => Headless.Run(async () =>
    {
        var w = await Opened();
        foreach (var tool in new[] { ToolKind.Pen, ToolKind.Shape, ToolKind.Lasso })
        {
            w.SetTool(tool);
            Press(w, Key.Escape);
            Assert.Equal(ToolKind.Select, w.ViewModel.Tools.Current);
        }
        w.SetTool(ToolKind.Pen);
        w.MouseMove(At(w, new(100, 400)));
        w.MouseDown(At(w, new(100, 400)), MouseButton.Left);
        w.MouseMove(At(w, new(160, 420)));
        Press(w, Key.Escape);
        w.MouseUp(At(w, new(160, 420)), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Annotations(w));
        Assert.Equal(ToolKind.Pen, w.ViewModel.Tools.Current); // Esc cancelled the stroke only

        w.MouseDown(At(w, new(100, 450)), MouseButton.Left);
        w.MouseMove(At(w, new(180, 470)));
        w.MouseDown(At(w, new(180, 470)), MouseButton.Right);
        w.MouseUp(At(w, new(180, 470)), MouseButton.Right);
        w.MouseUp(At(w, new(180, 470)), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Annotations(w));
        Assert.False(w.LastCanvasMenu?.IsOpen == true);

        Click(w, new(300, 500));
        Assert.IsType<InkStroke>(Assert.Single(Annotations(w)));
        w.Close();
    });

    /// <summary>d13: the status bar's zoom menu has the steps with the current one checked and the two fits.</summary>
    [Fact]
    public void TheZoomMenuChecksTheCurrentZoomAndFits() => Headless.Run(async () =>
    {
        var w = await Opened();
        var menu = w.OpenZoomMenu();
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Single(items, i => i.IsChecked && (string)i.Header! == "100%");
        double before = w.DocumentCanvas.Zoom;
        var fit = items.Single(i => (string)i.Header! == Strings.Get("view.fitPage"));
        fit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(before, w.DocumentCanvas.Zoom);
        Assert.Equal(w.ViewModel.Navigation.ZoomText, w.FindControl<TextBlock>("ZoomText")!.Text);
        Assert.False(w.Document!.Session.IsDirty);
        w.Close();
    });

    /// <summary>
    /// d09: the highlighter started on PDF text marks the characters (one quad per line, two lines give two); the selection
    /// tool selects PDF text and Ctrl+C copies it.
    /// </summary>
    [Fact]
    public void PdfTextIsHighlightedAndCopied() => Headless.Run(async () =>
    {
        var w = await Opened(1);
        var glyphs = w.ViewModel.PageGlyphs(0);
        string text = string.Concat(glyphs.Select(g => g.Text));
        int original = text.IndexOf("Original", StringComparison.Ordinal), pdf = text.IndexOf("PDF", StringComparison.Ordinal), english = text.IndexOf("English", StringComparison.Ordinal);
        Assert.True(original >= 0 && english > original, text);
        w.SetTool(ToolKind.FreeHighlight);
        DragAlong(w, Edge(glyphs[original], false), Edge(glyphs[original + 3], true), Edge(glyphs[original + 7], true));
        var one = Assert.IsType<PdfTextHighlight>(Assert.Single(Annotations(w)));
        Assert.Single(one.Quads);
        Assert.Equal("Original", one.SourceTextHint);
        DragAlong(w, Edge(glyphs[pdf], false), Edge(glyphs[english], false), Edge(glyphs[english + 6], true));
        var two = Assert.IsType<PdfTextHighlight>(Annotations(w)[^1]);
        Assert.Equal(2, two.Quads.Length);

        w.Undo();
        w.Undo();
        Assert.Empty(Annotations(w));
        w.SetTool(ToolKind.Select);
        DragAlong(w, Edge(glyphs[english], false), Edge(glyphs[english + 6], true));
        Assert.True(w.Selection.HasTextSelection);
        Assert.Equal("English", w.Selection.SelectedText);
        Press(w, Key.C, RawInputModifiers.Control);
        Assert.Equal(w.Selection.SelectedText, w.EditorClipboard.GetText());
        Assert.Empty(Annotations(w)); // selecting text changes nothing
        w.Close();
    });

    /// <summary>d13h: opened files go to the recent list; a recent file that is gone is named in the status line and taken off the list.</summary>
    [Fact]
    public void ARecentFileThatIsGoneIsRemoved() => Headless.Run(async () =>
    {
        var w = await Opened(1);
        string path = w.Document!.FilePath!;
        Assert.Equal(path, w.RecentFiles[0]);
        Assert.True(await w.CreateNoteAsync(NewPageSpec.Default));
        File.Delete(path);
        var menu = w.OpenRecentMenu();
        var item = menu.Items.OfType<MenuItem>().First(i => (string)i.Header! == Path.GetFileName(path));
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Headless.Pump(() => !w.RecentFiles.Contains(path));
        Assert.Equal(Strings.Format("recent.missing", ("name", Path.GetFileName(path))), w.ViewModel.Status); // in the status line, no dialog
        Assert.Null(w.Document!.FilePath);
        w.Close();
    });

    /// <summary>d13h: a link is followed by a click with the selection tool; with the pen a click draws and Ctrl+click follows.</summary>
    [Fact]
    public void LinksFollowTheSelectionToolAndCtrlClick() => Headless.Run(async () =>
    {
        string pdf = Path.Combine(Headless.TempFolder(), "linked.pdf");
        ProbeSamplePdf.CreateLinked(pdf, 5);
        var w = await Opened(pdf: pdf);
        var link = w.PageLinks(0).First(l => l.TargetPage is > 0);
        var center = new Point2((link.Area.UpperLeft.X + link.Area.LowerRight.X) / 2, (link.Area.UpperLeft.Y + link.Area.LowerRight.Y) / 2);
        w.SetTool(ToolKind.Select);
        Click(w, center);
        Headless.Pump(() => w.DocumentCanvas.CurrentPageIndex == link.TargetPage);

        w.DocumentCanvas.GoToPage(0);
        Dispatcher.UIThread.RunJobs();
        w.SetTool(ToolKind.Pen);
        Click(w, center);
        Assert.Equal(0, w.DocumentCanvas.CurrentPageIndex);
        Assert.Single(Annotations(w)); // a dot
        Click(w, center, RawInputModifiers.Control);
        Headless.Pump(() => w.DocumentCanvas.CurrentPageIndex == link.TargetPage);
        Assert.Single(Annotations(w));
        Assert.False(MainWindow.IsOpenableUri("javascript:alert(1)"));
        w.Close();
    });

    /// <summary>d11e: Shift draws a circle; a polygon takes clicks and ends with Enter; Delete removes a selected shape.</summary>
    [Fact]
    public void ShapesWithShiftAndPolygons() => Headless.Run(async () =>
    {
        var w = await Opened(1);
        w.ViewModel.Settings.ShapeKind = ShapeKind.Ellipse;
        w.SetTool(ToolKind.Shape);
        Drag(w, new(100, 350), new(260, 420), RawInputModifiers.Shift);
        var circle = Assert.IsType<ShapeAnnotation>(Assert.Single(Annotations(w)));
        Assert.Equal(ShapeKind.Ellipse, circle.ShapeKind);
        Assert.Equal(circle.Box.Width, circle.Box.Height, 1);

        w.ViewModel.Settings.ShapeKind = ShapeKind.Polygon;
        w.SetTool(ToolKind.Shape);
        foreach (var p in new Point2[] { new(320, 350), new(420, 360), new(400, 460), new(330, 440) }) Click(w, p);
        Press(w, Key.Enter);
        var polygon = Assert.IsType<ShapeAnnotation>(Annotations(w)[^1]);
        Assert.Equal(ShapeKind.Polygon, polygon.ShapeKind);
        Assert.Equal(4, polygon.Points.Length);

        w.SetTool(ToolKind.Select);
        Click(w, new(180, 350 + circle.Box.Height / 2));
        Assert.Equal([circle.Id], w.Selection.SelectedIds);
        Press(w, Key.Delete);
        Assert.DoesNotContain(Annotations(w), a => a.Id == circle.Id);
        w.Close();
    });
}
