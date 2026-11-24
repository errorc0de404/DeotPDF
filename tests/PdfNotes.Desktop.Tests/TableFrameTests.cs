using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
using PdfNotes.UI.Controls;
using PdfNotes.UI.Views;
using PdfNotes.ViewModels;

namespace PdfNotes.Desktop.Tests;

/// <summary>1.4 plan 1.3 in the window: inserting from the 표 grid, typing in cells, the row/column menu, boundary drags, handles and the bar.</summary>
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

    // ----- Task 10: cells, rows and columns, boundaries, handles, the bar -----

    /// <summary>A table on page 0 (top-left at 100, 150 unless <paramref name="transform"/>), the selection tool chosen.</summary>
    private static TableAnnotation AddTable(MainWindow w, int rows, int columns, Affine? transform = null, bool locked = false, Func<TableAnnotation, TableAnnotation>? change = null)
    {
        var session = w.View.Document!.Session;
        var table = TableAnnotation.Create(Guid.NewGuid(), new(100, 150), rows, columns, TextEditing.Default);
        if (transform is { } m) table = table with { Transform = m };
        if (change is not null) table = change(table);
        table = table with { Locked = locked };
        session.Execute(DocumentEdits.AddAnnotation(session.Current, session.Current.Pages[0].Id, table));
        w.View.SetTool(ToolKind.Select);
        Dispatcher.UIThread.RunJobs();
        return table;
    }

    private static TableAnnotation Table(MainWindow w) => Annotations(w).OfType<TableAnnotation>().Single();

    private static TableCell Text(string text) => new(text, [TextEditing.Default with { Start = 0, Length = text.Length }], Core.Models.TextAlignment.Left, []);

    /// <summary>A table-local point on the page.</summary>
    private static Point2 OnPage(TableAnnotation t, double x, double y) => t.Transform.Transform(new Point2(x, y));

    private static Point2 CellCenter(TableAnnotation t, int row, int column)
    {
        var g = TableLayout.Measure(t);
        return OnPage(t, (g.ColumnX[column] + g.ColumnX[column + 1]) / 2, (g.RowY[row] + g.RowY[row + 1]) / 2);
    }

    private static Point2 Dip(MainWindow w, Point2 page) => w.View.DocumentCanvas.PageTransform(0).PageToDip.Transform(page);

    private static int Steps(MainWindow w) => w.View.Document!.Session.UndoCount;

    private static string? Label(MainWindow w) => w.View.Document!.Session.UndoLabel;

    private static MenuItem Item(ContextMenu menu, string key) => menu.Items.OfType<MenuItem>().Single(i => i.Header as string == Strings.Get(key));

    private static void Run(ContextMenu menu, string key)
    {
        Item(menu, key).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static readonly string[] MenuKeys = ["table.rowAbove", "table.rowBelow", "table.columnLeft", "table.columnRight", "table.deleteRow", "table.deleteColumn"];

    [Fact]
    public void DoubleClickTypesInACell() => Headless.Run(() =>
    {
        var w = Window();
        var table = AddTable(w, 2, 2);
        int steps = Steps(w);
        w.ClickAt(CellCenter(table, 1, 1), clicks: 2);
        Assert.True(w.View.IsEditingText);
        Assert.Equal((table.Id, 1, 1), w.View.EditingCell);
        w.KeyTextInput("가나");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("가나", Table(w).Cell(1, 1).Text); // in the document while typing (one step)
        w.Tap(Key.Escape);
        Assert.False(w.View.IsEditingText);
        Assert.Equal("가나", Table(w).Cell(1, 1).Text);
        Assert.Equal(steps + 1, Steps(w));
        Assert.Equal("undo.tableCell", Label(w));
        Assert.Equal([table.Id], w.View.Selection.SelectedIds); // the table stays selected
        w.View.Undo();
        Assert.Equal("", Table(w).Cell(1, 1).Text);
        Assert.Equal(steps, Steps(w));

        // An edit that changes nothing leaves no step.
        w.View.BeginTableCellEdit(table.Id, 0, 0);
        Assert.True(w.View.IsEditingText);
        w.Tap(Key.Escape);
        Assert.Equal(steps, Steps(w));
        Assert.True(Table(w).Cell(0, 0).Runs[0].Bold); // the empty header cell keeps its style
        w.Close();
    });

    [Fact]
    public void TabMovesToTheNextCellAndAddsARowAtTheEnd() => Headless.Run(() =>
    {
        var w = Window();
        var table = AddTable(w, 2, 2);
        int steps = Steps(w);
        w.View.BeginTableCellEdit(table.Id, 1, 0);
        w.KeyTextInput("a");
        w.Tap(Key.Tab);
        Assert.Equal((table.Id, 1, 1), w.View.EditingCell);
        w.KeyTextInput("b");
        w.Tap(Key.Tab); // the last cell: a new row, typing goes on in its first cell
        Assert.Equal((table.Id, 2, 0), w.View.EditingCell);
        Assert.Equal(3, Table(w).Rows);
        w.KeyTextInput("c");
        w.Tap(Key.Tab, RawInputModifiers.Shift); // back to the cell before
        Assert.Equal((table.Id, 1, 1), w.View.EditingCell);
        w.Tap(Key.Escape);
        var t = Table(w);
        Assert.Equal(["", "", "a", "b", "c", ""], t.Cells.Select(c => c.Text).ToArray());
        Assert.Equal(steps + 4, Steps(w)); // a, b, the row, c

        w.View.Undo();
        Assert.Equal("", Table(w).Cell(2, 0).Text);
        Assert.Equal(3, Table(w).Rows);
        w.View.Undo();
        Assert.Equal(2, Table(w).Rows);
        Assert.Equal("b", Table(w).Cell(1, 1).Text);
        w.View.Undo();
        Assert.Equal("", Table(w).Cell(1, 1).Text);
        Assert.Equal("a", Table(w).Cell(1, 0).Text);
        w.Close();
    });

    [Fact]
    public void ContextMenuInsertsAndDeletes() => Headless.Run(() =>
    {
        var w = Window();
        var table = AddTable(w, 1, 2, change: t => TableEdits.SetCell(TableEdits.SetCell(t, 0, 0, Text("A")), 0, 1, Text("B")));
        int steps = Steps(w);
        var menu = w.View.OpenAnnotationMenu(Dip(w, CellCenter(table, 0, 1)))!;
        foreach (var key in MenuKeys) Assert.NotNull(Item(menu, key));
        Assert.False(Item(menu, "table.deleteRow").IsEnabled); // one row left
        Assert.True(Item(menu, "table.deleteColumn").IsEnabled);
        Run(menu, "table.rowBelow");
        Assert.Equal(2, Table(w).Rows);
        Assert.Equal("B", Table(w).Cell(0, 1).Text);
        Assert.Equal("undo.tableRow", Label(w));
        Assert.Equal(steps + 1, Steps(w));

        Run(w.View.OpenAnnotationMenu(Dip(w, CellCenter(Table(w), 0, 0)))!, "table.columnLeft");
        Assert.Equal(["", "A", "B"], Enumerable.Range(0, 3).Select(c => Table(w).Cell(0, c).Text).ToArray());
        Assert.Equal("undo.tableColumn", Label(w));
        Run(w.View.OpenAnnotationMenu(Dip(w, CellCenter(Table(w), 0, 1)))!, "table.columnRight");
        Assert.Equal(["", "A", "", "B"], Enumerable.Range(0, 4).Select(c => Table(w).Cell(0, c).Text).ToArray());
        Run(w.View.OpenAnnotationMenu(Dip(w, CellCenter(Table(w), 0, 2)))!, "table.deleteColumn");
        Run(w.View.OpenAnnotationMenu(Dip(w, CellCenter(Table(w), 0, 0)))!, "table.deleteColumn");
        Assert.Equal(["A", "B"], Enumerable.Range(0, 2).Select(c => Table(w).Cell(0, c).Text).ToArray());
        Run(w.View.OpenAnnotationMenu(Dip(w, CellCenter(Table(w), 0, 0)))!, "table.rowAbove");
        Assert.Equal(3, Table(w).Rows);
        Assert.Equal("A", Table(w).Cell(1, 0).Text);
        menu = w.View.OpenAnnotationMenu(Dip(w, CellCenter(Table(w), 1, 0)))!;
        Assert.True(Item(menu, "table.deleteRow").IsEnabled);
        Run(menu, "table.deleteRow");
        Assert.Equal(2, Table(w).Rows);
        Assert.Equal("", Table(w).Cell(0, 0).Text);
        Assert.Equal("undo.tableRow", Label(w));
        Assert.Equal(steps + 7, Steps(w));
        Assert.Equal([table.Id], w.View.Selection.SelectedIds);

        w.View.Undo();
        Assert.Equal(3, Table(w).Rows);
        w.Close();
    });

    [Fact]
    public void DraggingABoundaryResizesTheColumn() => Headless.Run(() =>
    {
        var w = Window();
        var table = AddTable(w, 2, 2);
        w.View.Selection.Select(table.Id);
        int steps = Steps(w);
        // The hover shows a resize cursor along the column line's normal.
        var on = OnPage(table, 80, 30);
        Assert.Equal(PdfNotes.Editor.Input.CursorShape.Resize, w.View.Selection.CursorAt(Dip(w, on))?.Shape);
        w.DragThrough(MouseButton.Left, RawInputModifiers.None, Gestures.Steps(on, OnPage(table, 120, 30), 4));
        var t = Table(w);
        Assert.Equal(120, t.ColumnWidths[0], 1);
        Assert.Equal(80, t.ColumnWidths[1], 1);
        Assert.Equal(table.Transform, t.Transform); // resized, not moved
        Assert.Equal("undo.tableSize", Label(w));
        Assert.Equal(steps + 1, Steps(w));
        Assert.Equal([table.Id], w.View.Selection.SelectedIds);

        // The outer right line sizes the last column; a row line the row above it.
        w.DragThrough(MouseButton.Left, RawInputModifiers.None, Gestures.Steps(OnPage(t, 200, 30), OnPage(t, 240, 30), 4));
        Assert.Equal(120, Table(w).ColumnWidths[1], 1);
        w.DragThrough(MouseButton.Left, RawInputModifiers.None, Gestures.Steps(OnPage(t, 40, 24), OnPage(t, 40, 44), 4));
        Assert.Equal(44, Table(w).RowHeights[0], 1);
        Assert.Equal(steps + 3, Steps(w));
        w.Close();
    });

    [Fact]
    public void CornerHandleScalesCellsNotText() => Headless.Run(() =>
    {
        var w = Window();
        var table = AddTable(w, 2, 2, change: t => TableEdits.SetCell(t, 1, 0, Text("가")));
        w.View.Selection.Select(table.Id);
        int steps = Steps(w);
        var b = TableLayout.Measure(table).Bounds;
        var corner = OnPage(table, b.X + b.Width, b.Y + b.Height);
        w.DragThrough(MouseButton.Left, RawInputModifiers.None, Gestures.Steps(corner, new(corner.X + b.Width / 2, corner.Y + b.Height), 6));
        var t = Table(w);
        Assert.Equal(steps + 1, Steps(w));
        Assert.Equal(120, t.ColumnWidths[0], 0); // × 1.5 across, × 2 down
        Assert.Equal(48, t.RowHeights[1], 0);
        Assert.Equal(table.Cell(1, 0).Runs[0].SizePt, t.Cell(1, 0).Runs[0].SizePt); // the text keeps its size
        Assert.Equal(table.Transform.OffsetX, t.Transform.OffsetX, 1); // the opposite corner stays
        Assert.Equal(table.Transform.OffsetY, t.Transform.OffsetY, 1);

        // With another annotation selected too, the group scales evenly; the table's text still keeps its size.
        var session = w.View.Document!.Session;
        var shape = new ShapeAnnotation(Guid.NewGuid(), false, Affine.Identity, ShapeKind.Rectangle, new(400, 150, 60, 40), [], ShapeStyle.Default);
        session.Execute(DocumentEdits.AddAnnotation(session.Current, session.Current.Pages[0].Id, shape));
        w.View.Selection.SetSelection([t.Id, shape.Id]);
        Dispatcher.UIThread.RunJobs();
        var frame = w.View.Selection.SelectionFrame()!.Value;
        var groupCorner = frame.Frame.Transform(new Point2(frame.Bounds.X + frame.Bounds.Width, frame.Bounds.Y + frame.Bounds.Height));
        double widthBefore = Table(w).ColumnWidths[1];
        w.DragThrough(MouseButton.Left, RawInputModifiers.None, Gestures.Steps(groupCorner, new(groupCorner.X + frame.Bounds.Width * 0.5, groupCorner.Y + frame.Bounds.Height * 0.5), 6));
        Assert.True(Table(w).ColumnWidths[1] > widthBefore * 1.3);
        Assert.Equal(table.Cell(1, 0).Runs[0].SizePt, Table(w).Cell(1, 0).Runs[0].SizePt);
        w.Close();
    });

    [Fact]
    public void ARotatedTableStillEditsTheRightCell() => Headless.Run(() =>
    {
        var w = Window();
        var table = AddTable(w, 2, 2, transform: Affine.Rotation(Math.PI / 2).Then(Affine.Translation(300, 150)));
        w.ClickAt(CellCenter(table, 1, 0), clicks: 2);
        Assert.Equal((table.Id, 1, 0), w.View.EditingCell);
        w.KeyTextInput("r");
        w.Tap(Key.Escape);
        Assert.Equal("r", Table(w).Cell(1, 0).Text);

        // A boundary drag along the table's own x axis.
        var t = Table(w);
        w.View.Selection.Select(t.Id);
        w.DragThrough(MouseButton.Left, RawInputModifiers.None, Gestures.Steps(OnPage(t, 80, 30), OnPage(t, 110, 30), 4));
        Assert.Equal(110, Table(w).ColumnWidths[0], 1);
        Assert.Equal(t.Transform, Table(w).Transform);
        w.Close();
    });

    [Fact]
    public void LineWidthAndHeaderFromTheBar() => Headless.Run(() =>
    {
        var w = Window();
        var table = AddTable(w, 2, 2);
        w.View.Selection.Select(table.Id);
        Dispatcher.UIThread.RunJobs();
        var styles = w.View.ViewModel.ToolStyles;
        Assert.True(styles.StyleBarShown);
        Assert.True(styles.StyleBarEnabled);
        Assert.Equal(0.5, styles.WidthMinimum);
        Assert.Equal(4, styles.WidthMaximum);
        Assert.False(styles.TransparencyEnabled);
        var header = w.View.FindControl<ToggleButton>("TableHeaderButton")!;
        Assert.True(header.IsEffectivelyVisible);
        Assert.True(header.IsChecked);
        int steps = Steps(w);

        styles.SetWidth(2);
        Assert.Equal(2, Table(w).Style.LineWidthPt);
        styles.SetWidth(10);
        Assert.Equal(4, Table(w).Style.LineWidthPt);
        styles.SetWidth(0.1);
        Assert.Equal(0.5, Table(w).Style.LineWidthPt);
        var red = ColorPalette.Basic[2];
        styles.SetColor(red);
        Assert.Equal(red, Table(w).Style.LineColor);
        Assert.Equal(steps + 4, Steps(w));

        header.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        if (header.Command is { } command && command.CanExecute(header.CommandParameter)) command.Execute(header.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Table(w).Style.HeaderRow);
        Assert.False(Table(w).Cell(0, 0).Runs[0].Bold);
        Assert.Equal("undo.tableHeader", Label(w));
        Assert.False(header.IsChecked);
        w.View.Undo();
        Assert.True(Table(w).Style.HeaderRow);

        // While a cell is edited the text bar takes the bar's place.
        w.View.BeginTableCellEdit(table.Id, 1, 1);
        Dispatcher.UIThread.RunJobs();
        Assert.False(styles.StyleBarShown);
        Assert.True(w.View.ViewModel.TextStyle.Bar.Visible);
        w.Tap(Key.Escape);
        w.Close();
    });

    [Fact]
    public void LockedTableIsNotEdited() => Headless.Run(() =>
    {
        var w = Window();
        var table = AddTable(w, 2, 2, locked: true);
        int steps = Steps(w);
        w.ClickAt(CellCenter(table, 1, 1), clicks: 2);
        Assert.False(w.View.IsEditingText);
        w.View.BeginTableCellEdit(table.Id, 1, 1);
        Assert.False(w.View.IsEditingText);
        var menu = w.View.OpenAnnotationMenu(Dip(w, CellCenter(table, 1, 1)))!;
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), i => MenuKeys.Any(k => i.Header as string == Strings.Get(k)));
        menu.Close();
        w.DragThrough(MouseButton.Left, RawInputModifiers.None, Gestures.Steps(OnPage(table, 80, 30), OnPage(table, 120, 30), 4));
        Assert.Equal(table, Table(w));
        Assert.Equal(steps, Steps(w));
        w.Close();
    });

    /// <summary>
    /// Review fix: Tab while a composition is still open commits it and moves on, without a tab character. Not a real IME:
    /// the composition is set on the editor model the way the IME client sets it.
    /// </summary>
    [Fact]
    public void TabDuringASimulatedCompositionCommitsItAndMovesOn() => Headless.Run(() =>
    {
        var w = Window();
        var table = AddTable(w, 1, 2);
        w.View.BeginTableCellEdit(table.Id, 0, 1); // the last cell
        w.KeyTextInput("사");
        w.View.ActiveTextEditor!.Model!.SetComposition("과");
        Assert.True(w.View.ActiveTextEditor.Model.IsComposing);
        w.Tap(Key.Tab);
        Assert.Equal("사과", Table(w).Cell(0, 1).Text);
        Assert.Equal(2, Table(w).Rows); // the last cell's Tab still adds the row
        Assert.Equal((table.Id, 1, 0), w.View.EditingCell);
        w.Tap(Key.Escape);
        w.Close();
    });

    [Fact]
    public void TableFontAndSizeFromTheBar() => Headless.Run(() =>
    {
        var w = Window();
        var table = AddTable(w, 2, 2, change: t => TableEdits.SetCell(t, 1, 0, Text("가")));
        w.View.Selection.Select(table.Id);
        Dispatcher.UIThread.RunJobs();
        var styles = w.View.ViewModel.ToolStyles;
        var sizeBox = w.View.FindControl<TextBox>("TableSizeBox")!;
        Assert.True(w.View.FindControl<ComboBox>("TableFontBox")!.IsEffectivelyVisible);
        Assert.Equal(TextEditing.Default.SizePt.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture), sizeBox.Text);
        int steps = Steps(w);

        // The size box as typed: every cell, empty ones too, in one step; the rows grow with the text.
        sizeBox.Text = "20";
        styles.CommitTableSize();
        Assert.All(Table(w).Cells.SelectMany(c => c.Runs), r => Assert.Equal(20, r.SizePt));
        Assert.True(Table(w).Cell(0, 0).Runs[0].Bold); // the header keeps its bold
        Assert.Equal("undo.tableFormat", Label(w));
        Assert.Equal(steps + 1, Steps(w));
        var grid = TableLayout.Measure(Table(w));
        Assert.True(grid.RowY[2] - grid.RowY[1] > TableAnnotation.DefaultRowHeight);
        styles.SetTableSize(20); // nothing changes: no step
        Assert.Equal(steps + 1, Steps(w));

        string other = TextLayout.FontFamilies().First(f => f != TextEditing.Default.FontFamily && TextLayout.FontAvailable(f));
        styles.SetTableFont(other);
        Assert.All(Table(w).Cells.SelectMany(c => c.Runs), r => Assert.Equal(other, r.FontFamily));
        Assert.Equal(steps + 2, Steps(w));
        w.View.Undo();
        w.View.Undo();
        Assert.Equal(TextEditing.Default.SizePt, Table(w).Cell(1, 0).Runs[0].SizePt);
        w.Close();
    });

    [Fact]
    public void EditingACellLeavesTheTextBoxFillAndBorderAlone() => Headless.Run(() =>
    {
        var w = Window();
        var table = AddTable(w, 2, 2);
        var textStyle = w.View.ViewModel.TextStyle;
        var before = textStyle.NewBoxStyle;
        textStyle.SetColorTarget(TextColorTarget.Fill);
        w.View.BeginTableCellEdit(table.Id, 1, 1);
        Dispatcher.UIThread.RunJobs();
        Assert.False(textStyle.BoxTargetsShown);
        Assert.False(w.View.FindControl<RadioButton>("TextTargetFill")!.IsVisible);
        Assert.False(w.View.FindControl<RadioButton>("TextTargetBorder")!.IsVisible);
        Assert.True(textStyle.IsTargetText); // the palette colors the characters
        var red = ColorPalette.Basic[2];
        textStyle.SetColor(red);
        w.KeyTextInput("x");
        w.Tap(Key.Escape);
        Assert.Equal(before, textStyle.NewBoxStyle);
        Assert.Equal(red, Table(w).Cell(1, 1).Runs[0].Color);
        Assert.True(textStyle.BoxTargetsShown);
        w.Close();
    });
}
