using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;
using PdfNotes.Text;
using PdfNotes.UI.Controls;
using PdfNotes.UI.Views;

namespace PdfNotes.Desktop.Tests;

/// <summary>1.4 plan 1.3 넣기 in the window: the 표 button's size grid, the armed table, a click on the page.</summary>
public sealed class TableFrameTests
{
    private static MainWindow Window(AppSettings? settings = null)
    {
        var window = new MainWindow(TestWindows.App(settings ?? new AppSettings(), Headless.TempFolder()), TestWindows.Dialogs) { Width = 1200, Height = 1000 };
        window.Script.PromptOverride = (_, _) => Answer.No;
        window.Script.MessageSink = _ => { };
        window.Show();
        window.View.DocumentCanvas.SetZoom(1);
        window.View.DocumentCanvas.GoToPage(0);
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static IReadOnlyList<Annotation> Annotations(MainWindow w) => w.View.Document!.Session.Current.Pages[0].Annotations;

    /// <summary>Opens the 표 button's grid and returns it.</summary>
    private static TableSizePicker OpenPicker(MainWindow w)
    {
        var button = w.View.FindControl<Button>("TableButton")!;
        button.Flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        Assert.True(button.Flyout.IsOpen);
        return w.View.FindControl<TableSizePicker>("TablePicker")!;
    }

    [Fact]
    public void PickingThreeByFourAndClickingPlacesATable() => Headless.Run(() =>
    {
        var settings = new AppSettings();
        settings.DefaultText = settings.DefaultText with { SizePt = 14 };
        var w = Window(settings);
        var picker = OpenPicker(w);
        picker.Hover(3, 4);
        Assert.Equal("3 × 4", picker.SizeText);
        Assert.Equal(12, picker.Cells.Count(c => c.IsLit));
        picker.Pick(3, 4);
        Assert.False(w.View.FindControl<Button>("TableButton")!.Flyout!.IsOpen);
        Assert.Equal(ToolKind.Table, w.View.CurrentTool);

        int steps = w.View.Document!.Session.UndoCount;
        w.ClickAt(new(100, 150));
        var table = Assert.IsType<TableAnnotation>(Assert.Single(Annotations(w)));
        Assert.Equal((3, 4), (table.Rows, table.Columns));
        Assert.Equal(Affine.Translation(100, 150), table.Transform);
        Assert.Equal(14, table.Cell(1, 1).Runs[0].SizePt); // the text box default style
        Assert.Equal([table.Id], w.View.Selection.SelectedIds);
        Assert.Equal(ToolKind.Select, w.View.CurrentTool);
        Assert.Equal(steps + 1, w.View.Document.Session.UndoCount);

        // One at a time: the next click selects or pans, it does not insert again.
        w.ClickAt(new(400, 600));
        Assert.Single(Annotations(w));

        w.View.Document.Session.Undo();
        Assert.Empty(Annotations(w));
        w.Close();
    });

    [Fact]
    public void ATableNearTheEdgeIsPulledInside() => Headless.Run(() =>
    {
        var w = Window();
        var setup = w.View.Document!.Session.Current.Pages[0].Setup;
        w.View.DocumentCanvas.SetZoom(0.5); // the whole page on the screen
        w.View.DocumentCanvas.GoToPage(0);
        Dispatcher.UIThread.RunJobs();
        OpenPicker(w).Pick(3, 4);
        w.ClickAt(new(setup.WidthPt - 10, setup.HeightPt - 10));
        var table = Assert.IsType<TableAnnotation>(Assert.Single(Annotations(w)));
        var bounds = TableLayout.Measure(table).Bounds;
        Assert.Equal(setup.WidthPt, table.Transform.OffsetX + bounds.X + bounds.Width, 3);
        Assert.Equal(setup.HeightPt, table.Transform.OffsetY + bounds.Y + bounds.Height, 3);
        w.Close();
    });

    [Fact]
    public void EscCancelsTheArmedTable() => Headless.Run(() =>
    {
        var w = Window();
        OpenPicker(w).Pick(2, 2);
        Assert.Equal(ToolKind.Table, w.View.CurrentTool);
        w.View.DocumentCanvas.Focus();
        w.Tap(Key.Escape);
        Assert.Equal(ToolKind.Select, w.View.CurrentTool);
        Assert.Null(w.View.ViewModel.Tools.Table);
        w.ClickAt(new(100, 150));
        Assert.Empty(Annotations(w));

        // Another tool cancels it too.
        OpenPicker(w).Pick(2, 2);
        w.View.SetTool(ToolKind.Pen);
        Assert.Null(w.View.ViewModel.Tools.Table);
        w.View.SetTool(ToolKind.Table);
        Assert.Equal(ToolKind.Select, w.View.CurrentTool);
        w.Close();
    });
}
