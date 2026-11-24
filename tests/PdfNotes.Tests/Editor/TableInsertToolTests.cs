using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Session;
using PdfNotes.Editor.Tools;
using PdfNotes.Text;

namespace PdfNotes.Tests.Editor;

/// <summary>1.4 plan 1.3 넣기: the armed table insert, its placement on the page and how Esc or another tool cancels it.</summary>
public sealed class TableInsertToolTests
{
    /// <summary>A canvas whose every point is <see cref="At"/> on page 0.</summary>
    private sealed class PageCanvas : ICanvasView
    {
        public Point2 At { get; set; }
        public PageAnchor? HitTestPage(Point2 viewportPoint) => new(0, At);
        public ViewTransform PageTransform(int index) => throw new NotSupportedException();
        public double DpiScale => 1;
        public Point2 Offset => default;
        public void SetHorizontalOffset(double value) { }
        public void SetVerticalOffset(double value) { }
        public void SetPreview(int pageIndex, ICanvasPreview? preview, bool mark = false) { }
        public void RefreshPreview() { }
        public void RefreshOverlays() { }
        public void RefreshOverlay(int pageIndex) { }
    }

    private static readonly TextRun Style = TextEditing.Default with { SizePt = 14 };

    private static Page Blank(double width = 595, double height = 842) =>
        Page.Create(new PageSetup(width, height, 0, PageSource.Generated, PageBackground.Blank));

    [Fact]
    public void ArmingATableMakesTheTableToolCurrentAndAnotherToolCancelsIt()
    {
        var tools = new ToolSwitch(new AppSettings(), () => { });
        Assert.Equal(ToolKind.Table, tools.ArmTable(new(3, 4), notesHidden: false));
        Assert.Equal(new TableSize(3, 4), tools.Table);
        Assert.Equal(EscapeStep.SelectTool, tools.Escape(operationActive: false, hasSelection: false));

        tools.Choose(ToolKind.Pen, notesHidden: false);
        Assert.Null(tools.Table);

        // Hidden notes allow only the selection tool (spec 24.10); out-of-range sizes are kept inside the grid.
        Assert.Equal(ToolKind.Select, tools.ArmTable(new(2, 2), notesHidden: true));
        Assert.Null(tools.Table);
        tools.ArmTable(new(0, 99), notesHidden: false);
        Assert.Equal(new TableSize(1, TableAnnotation.MaxPick), tools.Table);

        // The table tool without a size is the selection tool.
        tools.Choose(ToolKind.Select, notesHidden: false);
        Assert.Equal(ToolKind.Select, tools.Choose(ToolKind.Table, notesHidden: false));
    }

    [Fact]
    public void TheClickIsTheTopLeftCornerAndAnEdgePullsTheTableInside()
    {
        var page = Blank();
        var table = TableInsertTool.Place(page, new(100, 150), new(3, 4), Style);
        Assert.Equal((3, 4), (table.Rows, table.Columns));
        Assert.Equal(Affine.Translation(100, 150), table.Transform);
        Assert.Equal(14, table.Cell(1, 0).Runs[0].SizePt);
        Assert.True(table.Cell(0, 0).Runs[0].Bold); // the header row

        var edge = TableInsertTool.Place(page, new(590, 830), new(3, 4), Style);
        var bounds = TableLayout.Measure(edge).Bounds;
        Assert.Equal(595, edge.Transform.OffsetX + bounds.X + bounds.Width, 6);
        Assert.Equal(842, edge.Transform.OffsetY + bounds.Y + bounds.Height, 6);

        // Wider than the page: at the page's left edge (the outer line inside), the click still sets the top.
        var wide = TableInsertTool.Place(page, new(300, 200), new(2, 10), Style);
        Assert.Equal(-TableLayout.Measure(wide).Bounds.X, wide.Transform.OffsetX, 6);
        Assert.Equal(200, wide.Transform.OffsetY, 6);

        var small = Blank(200, 100);
        var big = TableInsertTool.Place(small, new(50, 50), new(10, 10), Style);
        var b = TableLayout.Measure(big).Bounds;
        Assert.Equal((-b.X, -b.Y), (big.Transform.OffsetX, big.Transform.OffsetY));
    }

    [Fact]
    public void AClickPlacesOneTableSelectsItAndGoesToTheSelectionTool()
    {
        using var doc = OpenDocument.CreateNote(NewPageSpec.Default);
        var tools = new ToolSwitch(new AppSettings(), () => { });
        var canvas = new PageCanvas { At = new(80, 90) };
        var chosen = new List<ToolKind>();
        var set = new ToolSet(canvas, () => doc.Session, tools,
            new(_ => [], () => false, () => StrokeStyle.DefaultPen, () => false, () => StrokeStyle.DefaultHighlighter, () => false,
                () => TextHighlightStyle.Default, () => (TextMarkupKind.Underline, StrokeStyle.DefaultMarkup), () => StrokeStyle.DefaultPen, () => LineOptions.Default,
                () => ShapeKind.Rectangle, () => ShapeStyle.Default, () => 4, () => 12, TextStyle: () => Style),
            new((_, _, _) => { }, _ => false, (_, _) => { }, _ => false),
            kind => { chosen.Add(kind); tools.Choose(kind, false); }, () => { }, action => action());
        Assert.Same(set.Table, set.For(ToolKind.Table));
        Assert.True(TouchRules.TapReachesTool(set.Table)); // a tap places it on a tablet

        // Not armed: a press does nothing.
        var press = new CanvasPointer(new(10, 10), InputModifiers.None);
        Assert.False(set.Table.PointerDown(press));

        tools.ArmTable(new(3, 4), false);
        int steps = doc.Session.UndoCount;
        Assert.True(set.Table.PointerDown(press));
        set.Table.PointerUp(press);
        var table = Assert.IsType<TableAnnotation>(Assert.Single(doc.Session.Current.Pages[0].Annotations));
        Assert.Equal(Affine.Translation(80, 90), table.Transform);
        Assert.Equal(steps + 1, doc.Session.UndoCount);
        Assert.Equal("undo.addTable", doc.Session.UndoLabel);
        Assert.Equal([ToolKind.Select], chosen);
        Assert.Equal([table.Id], set.Select.SelectedIds);
        Assert.Null(tools.Return); // one table at a time: the table tool does not come back
        Assert.Null(tools.Table);
    }
}
