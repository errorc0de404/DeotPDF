using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;
using PdfNotes.Text;
using PdfNotes.UI.Views;

namespace PdfNotes.Desktop.Tests;

/// <summary>1.4 auto width Task 3 in the window: a click or a paste makes an auto width box, a drag or the width handle a fixed one (plan 1.1).</summary>
public sealed class TextAutoWidthFrameTests
{
    private const string Long = "줄바꿈 없이 이어지는 긴 문단은 페이지 오른쪽 끝까지 늘어난 다음 거기서 접혀야 한다. The quick brown fox jumps over the lazy dog.";

    private static MainWindow Window()
    {
        var window = new MainWindow(TestWindows.App(new AppSettings(), Headless.TempFolder()), TestWindows.Dialogs) { Width = 1200, Height = 1000 };
        window.Script.PromptOverride = (_, _) => Answer.No;
        window.Script.MessageSink = _ => { };
        window.View.EditorClipboard.InMemory = true;
        window.Show();
        window.View.DocumentCanvas.SetZoom(1);
        window.View.DocumentCanvas.GoToPage(0);
        Settle(window);
        return window;
    }

    private static void Settle(MainWindow w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static double PageWidth(MainWindow w) => w.View.Document!.Session.Current.Pages[0].Setup.WidthPt;

    private static TextBoxAnnotation Box(MainWindow w, Guid id) => (TextBoxAnnotation)w.View.Document!.Session.Current.FindAnnotation(id)!.Annotation;

    private static Point2 Viewport(MainWindow w, Point2 page) => w.View.DocumentCanvas.PageTransform(0).PageToDip.Transform(page);

    private static Point2 PagePoint(MainWindow w, Point2 viewport) => w.View.DocumentCanvas.PageTransform(0).DipToPage.Transform(viewport);

    /// <summary>A box made by a text tool click at <paramref name="at"/> with <paramref name="text"/> typed, editing finished.</summary>
    private static TextBoxAnnotation Clicked(MainWindow w, Point2 at, string text)
    {
        w.View.SetTool(ToolKind.Text);
        Settle(w);
        w.ClickAt(at);
        Assert.True(w.View.IsEditingText);
        w.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
        var id = w.View.EditingText!.Id;
        w.View.FinishTextEditing();
        return Box(w, id);
    }

    /// <summary>A box made by a text tool drag from <paramref name="from"/> to <paramref name="to"/> with <paramref name="text"/> typed, editing finished.</summary>
    private static TextBoxAnnotation Dragged(MainWindow w, Point2 from, Point2 to, string text)
    {
        w.View.SetTool(ToolKind.Text);
        Settle(w);
        w.DragThrough(MouseButton.Left, RawInputModifiers.None, Gestures.Steps(from, to, 6));
        Assert.True(w.View.IsEditingText);
        Assert.False(w.View.EditingText!.AutoWidth);
        w.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
        var id = w.View.EditingText!.Id;
        w.View.FinishTextEditing();
        return Box(w, id);
    }

    private static MenuItem AutoWidthItem(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Single(i => i.Header as string == Strings.Get("textBox.autoWidth"));

    private static void Press(MenuItem item)
    {
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static Point2 Middle(MainWindow w, TextBoxAnnotation box)
    {
        var b = AnnotationGeometry.LocalBounds(box, TextLayout.Bounds);
        return Viewport(w, box.Transform.Transform(new(b.X + b.Width / 2, b.Y + b.Height / 2)));
    }

    [Fact]
    public void ClickMakesAnAutoBoxThatGrowsAsYouType() => Headless.Run(() =>
    {
        var w = Window();
        w.View.SetTool(ToolKind.Text);
        Settle(w);
        w.ClickAt(new(100, 400));
        Assert.True(w.View.EditingText!.AutoWidth);
        w.KeyTextInput("가");
        Dispatcher.UIThread.RunJobs();
        double one = w.View.EditingText!.WidthPt;
        w.KeyTextInput("나다라마");
        Settle(w);
        var editing = w.View.EditingText!;
        Assert.True(editing.WidthPt > one, $"{one} -> {editing.WidthPt}");
        Assert.True(editing.WidthPt < 180, $"{editing.WidthPt}");
        // The editor on screen is as wide as the box.
        var editor = w.View.ActiveTextEditor!;
        Assert.InRange(editor.Bounds.Width - editing.WidthPt * editor.Zoom, -1, 1); // layout rounds to whole pixels
        w.View.FinishTextEditing();
        var box = Box(w, editing.Id);
        Assert.True(box.AutoWidth);
        Assert.Equal(editing.WidthPt, box.WidthPt);
        Assert.Equal(TextAutoWidth.NaturalWidth(box), box.WidthPt, 6);
        w.Close();
    });

    [Fact]
    public void DragMakesAFixedBox() => Headless.Run(() =>
    {
        var w = Window();
        var box = Dragged(w, new(100, 400), new(300, 400), Long);
        Assert.False(box.AutoWidth);
        Assert.Equal(200, box.WidthPt, 6);
        Assert.True(TextLayout.Measure(box).Lines.Length > 1);
        w.Close();
    });

    [Fact]
    public void CanvasPasteMakesAnAutoBoxCappedAtThePageEdge() => Headless.Run(() =>
    {
        var w = Window();
        double pageWidth = PageWidth(w);
        Assert.True(pageWidth > 560, $"page {pageWidth}");
        Assert.False(w.View.IsEditingText);
        w.View.EditorClipboard.SetText(Long);
        Assert.True(w.View.PasteSelection(Viewport(w, new(500, 400))));
        var id = w.View.EditingText!.Id;
        w.View.FinishTextEditing();
        var box = Box(w, id);
        Assert.True(box.AutoWidth);
        Assert.Equal(Long, box.Text);
        // The cap: from the anchor (the box's left edge) to the page edge (about 95 here).
        Assert.Equal(pageWidth - box.Transform.OffsetX, box.WidthPt, 6);
        Assert.True(TextLayout.Measure(box).Lines.Length > 1);
        w.Close();
    });

    [Fact]
    public void WidthHandleMakesItFixed() => Headless.Run(() =>
    {
        var w = Window();
        var box = Clicked(w, new(100, 400), "가나다라");
        Assert.True(box.AutoWidth);
        w.View.SetTool(ToolKind.Select);
        w.View.Selection.Select(box.Id);
        Settle(w);
        var handle = PagePoint(w, w.View.Selection.HandlePoints(w.View.DocumentCanvas.PageTransform(0))[1]);
        w.DragThrough(MouseButton.Left, RawInputModifiers.None, Gestures.Steps(handle, new(handle.X + 60, handle.Y), 6));
        var resized = Box(w, box.Id);
        Assert.False(resized.AutoWidth);
        Assert.Equal(box.WidthPt + 60, resized.WidthPt, 3);
        // Typing then keeps the dragged width.
        w.View.BeginTextEdit(box.Id);
        w.View.ActiveTextEditor!.Model!.Type(Long);
        w.View.FinishTextEditing();
        Assert.Equal(resized.WidthPt, Box(w, box.Id).WidthPt);
        w.Close();
    });

    [Fact]
    public void ContextMenuTogglesAutoWidth() => Headless.Run(() =>
    {
        var w = Window();
        var session = w.View.Document!.Session;
        var box = Dragged(w, new(100, 400), new(300, 400), "가나다");
        Assert.Equal(200, box.WidthPt, 6);

        // On: back to auto width, fit at once; one Undo returns to the fixed width.
        var item = AutoWidthItem(w.View.OpenAnnotationMenu(Middle(w, box))!);
        Assert.True(item.IsEnabled);
        Assert.Equal(MenuItemToggleType.CheckBox, item.ToggleType);
        Assert.False(item.IsChecked);
        int steps = session.UndoCount;
        Press(item);
        var auto = Box(w, box.Id);
        Assert.True(auto.AutoWidth);
        Assert.Equal(TextAutoWidth.NaturalWidth(auto), auto.WidthPt, 6);
        Assert.True(auto.WidthPt < 100);
        Assert.Equal(steps + 1, session.UndoCount);
        Assert.Equal("undo.autoWidth", session.UndoLabel);
        session.Undo();
        Assert.False(Box(w, box.Id).AutoWidth);
        Assert.Equal(200, Box(w, box.Id).WidthPt, 6);
        session.Redo();

        // Off: fixed at the width it has now; one Undo returns to auto width.
        item = AutoWidthItem(w.View.OpenAnnotationMenu(Middle(w, auto))!);
        Assert.True(item.IsChecked);
        Press(item);
        var fixedBox = Box(w, box.Id);
        Assert.False(fixedBox.AutoWidth);
        Assert.Equal(auto.WidthPt, fixedBox.WidthPt);
        session.Undo();
        Assert.True(Box(w, box.Id).AutoWidth);

        // A locked box: the entry is there but off.
        w.View.ViewModel.Selection.SetLocked([box.Id], true);
        item = AutoWidthItem(w.View.OpenAnnotationMenu(Middle(w, Box(w, box.Id)))!);
        Assert.False(item.IsEnabled);
        w.Close();
    });

    [Fact]
    public void MovingDoesNotRefit() => Headless.Run(() =>
    {
        var w = Window();
        double pageWidth = PageWidth(w);
        var box = Clicked(w, new(100, 400), "가나다라마바사아자");
        w.View.SetTool(ToolKind.Select);
        w.View.Selection.Select(box.Id);
        Settle(w);
        var inside = new Point2(box.Transform.OffsetX + 10, box.Transform.OffsetY + 8);
        double dx = pageWidth - 60 - box.Transform.OffsetX;
        w.DragThrough(MouseButton.Left, RawInputModifiers.None, Gestures.Steps(inside, new(inside.X + dx, inside.Y), 8));
        var moved = Box(w, box.Id);
        Assert.Equal(box.Transform.OffsetX + dx, moved.Transform.OffsetX, 3);
        Assert.Equal(box.WidthPt, moved.WidthPt); // past the page edge, not wrapped again
        Assert.True(moved.Transform.OffsetX + moved.WidthPt > pageWidth);

        // The next edit fits it: up to the page edge.
        w.View.BeginTextEdit(box.Id);
        w.View.ActiveTextEditor!.Model!.Type("차");
        w.View.FinishTextEditing();
        var edited = Box(w, box.Id);
        Assert.Equal(pageWidth - edited.Transform.OffsetX, edited.WidthPt, 6);
        Assert.Equal(moved.Transform, edited.Transform);
        w.Close();
    });

    [Fact]
    public void CenterAlignedAutoBoxGrowsBothWays() => Headless.Run(() =>
    {
        var w = Window();
        w.View.SetTool(ToolKind.Text);
        Settle(w);
        w.ClickAt(new(300, 400));
        w.KeyTextInput("가나");
        Dispatcher.UIThread.RunJobs();
        w.View.ViewModel.TextStyle.SetAlignment(TextAlignment.Center);
        w.View.ActiveTextEditor!.Focus();
        Dispatcher.UIThread.RunJobs();
        var before = w.View.EditingText!;
        double center = before.Transform.OffsetX + before.WidthPt / 2;
        w.KeyTextInput("다라마바");
        Settle(w);
        var after = w.View.EditingText!;
        Assert.True(after.WidthPt > before.WidthPt);
        Assert.True(after.Transform.OffsetX < before.Transform.OffsetX);
        Assert.Equal(center, after.Transform.OffsetX + after.WidthPt / 2, 6);
        // The editor on screen follows the box's left edge.
        var at = Viewport(w, new(after.Transform.OffsetX, after.Transform.OffsetY));
        Assert.Equal(at.X, Avalonia.Controls.Canvas.GetLeft(w.View.ActiveTextEditor!), 3);
        w.Close();
    });

    [Fact]
    public void OldBoxOpensFixed() => Headless.Run(async () =>
    {
        var w = Window();
        var box = Dragged(w, new(100, 400), new(300, 400), "가나다");
        string path = Path.Combine(Headless.TempFolder(), "fixed.pdf");
        Assert.True(await w.View.SaveToAsync(path)); // no auto width box: written as v8
        w.Close();

        var again = Window();
        Assert.True(await again.View.OpenFileAsync(path));
        var opened = Box(again, box.Id);
        Assert.False(opened.AutoWidth);
        Assert.Equal(200, opened.WidthPt, 6);
        again.View.BeginTextEdit(box.Id);
        again.View.ActiveTextEditor!.Model!.Type(Long);
        again.View.FinishTextEditing();
        Assert.Equal(opened.WidthPt, Box(again, box.Id).WidthPt);
        Assert.False(Box(again, box.Id).AutoWidth);
        again.Close();
    });

    /// <summary>The edit frame widens with the composition, not only after it is committed (user report 2026-11-25).</summary>
    [Fact]
    public void EditFrameFollowsTheComposition() => Headless.Run(() =>
    {
        var w = Window();
        w.View.SetTool(ToolKind.Text);
        Settle(w);
        w.ClickAt(new(100, 400));
        w.KeyTextInput("가");
        Settle(w);
        var model = w.View.ActiveTextEditor!.Model!;
        double before = model.DisplayBox.WidthPt;
        model.SetComposition("나다라마바사아");
        Settle(w);
        var shown = model.DisplayBox;
        Assert.True(shown.WidthPt > before + 40, $"{before} -> {shown.WidthPt}");
        // The frame's top edge just left of the widened box's right end.
        var corner = w.PageAt(shown.Transform.Transform(new(shown.WidthPt - 4, 0)));
        Assert.True(FrameLineNear(w, new(corner.X, corner.Y)), "no edit frame at the widened edge");
        w.Close();
    });

    /// <summary>A colored pixel (the frame line, not white paper or black text) within 3 pixels of <paramref name="at"/>.</summary>
    private static bool FrameLineNear(MainWindow w, Point2 at)
    {
        using var frame = w.CaptureRenderedFrame()!;
        using var copy = new Avalonia.Media.Imaging.WriteableBitmap(frame.PixelSize, frame.Dpi, Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var l = copy.Lock()) frame.CopyPixels(new Avalonia.PixelRect(frame.PixelSize), l.Address, l.RowBytes * l.Size.Height, l.RowBytes);
        using var locked = copy.Lock();
        var pixels = new byte[locked.RowBytes * locked.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(locked.Address, pixels, 0, pixels.Length);
        for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
            {
                int i = ((int)Math.Round(at.Y) + dy) * locked.RowBytes + ((int)Math.Round(at.X) + dx) * 4;
                if (Math.Abs(pixels[i] - pixels[i + 2]) > 8) return true;
            }
        return false;
    }

    /// <summary>Review Focus 5: the text bar's size on a selected (not edited) auto box fits it again, in the same Undo step.</summary>
    [Fact]
    public void BarFontChangeRefitsASelectedAutoBox() => Headless.Run(() =>
    {
        var w = Window();
        var session = w.View.Document!.Session;
        var box = Clicked(w, new(100, 400), "가나다 Wide");
        var fixedBox = Dragged(w, new(100, 600), new(300, 600), "가나다");
        w.View.SetTool(ToolKind.Select);
        w.View.Selection.SetSelection([box.Id, fixedBox.Id]);
        Settle(w);
        Assert.False(w.View.IsEditingText);
        int steps = session.UndoCount;
        w.View.ViewModel.TextStyle.SetSize("24");
        var bigger = Box(w, box.Id);
        Assert.Equal(24, bigger.Runs[0].SizePt);
        Assert.True(bigger.WidthPt > box.WidthPt, $"{box.WidthPt} -> {bigger.WidthPt}");
        Assert.Equal(TextAutoWidth.NaturalWidth(bigger), bigger.WidthPt, 6);
        Assert.Equal(200, Box(w, fixedBox.Id).WidthPt, 6); // a fixed box keeps its width
        Assert.Equal(steps + 1, session.UndoCount);
        session.Undo();
        Assert.Equal(box.WidthPt, Box(w, box.Id).WidthPt);
        Assert.Equal(box.Runs[0].SizePt, Box(w, box.Id).Runs[0].SizePt);
        w.View.Selection.Select(box.Id);
        w.View.ViewModel.TextStyle.SetFlag('B', true);
        Assert.True(Box(w, box.Id).Runs[0].Bold);
        Assert.True(Box(w, box.Id).WidthPt > box.WidthPt, $"{box.WidthPt} -> {Box(w, box.Id).WidthPt}");
        w.Close();
    });
}
