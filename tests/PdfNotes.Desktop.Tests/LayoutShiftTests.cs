using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using PdfNotes.Core.Geometry;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// 2026-11-12 user report (1.2.1): selecting a shape or a text box added a bar row, the page moved up and the selection
/// was dragged with it. The bars are one row now (ScrollStrip), so the canvas stays where it is whatever is selected.
/// </summary>
public sealed class LayoutShiftTests
{
    [Theory]
    [InlineData(1200, false)]
    [InlineData(900, false)]
    [InlineData(1200, true)]
    [InlineData(700, true)]
    public void SelectingAndEditingDoNotMoveTheCanvas(double width, bool touch) => Headless.Run(() =>
    {
        var w = new MainWindow(TestWindows.App(new AppSettings(), Headless.TempFolder()), TestWindows.Dialogs) { Width = width, Height = 900 };
        w.Script.PromptOverride = (_, b) => b == AskButtons.YesNo ? Answer.Yes : Answer.No;
        w.Script.MessageSink = _ => { };
        if (touch) w.View.UseTouchLayout();
        w.Show();
        double Top()
        {
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            return w.View.DocumentCanvas.TranslatePoint(new Point(0, 0), w)!.Value.Y;
        }
        double top = Top();
        w.View.SetTool(ToolKind.Shape);
        w.DragThrough(MouseButton.Left, RawInputModifiers.None, [new Point2(100, 100), new Point2(200, 160), new Point2(250, 200)]);
        Assert.Equal(top, Top());
        w.View.SetTool(ToolKind.Select);
        w.ClickAt(new Point2(175, 150));
        Assert.Single(w.View.Selection.SelectedIds);
        Assert.Equal(top, Top());
        w.View.SetTool(ToolKind.Text);
        w.ClickAt(new Point2(300, 400));
        Assert.Equal(top, Top()); // the text bar replaces the style bar
        w.Tap(Key.Escape);
        Assert.Equal(top, Top());
        w.Close();
    });
}
