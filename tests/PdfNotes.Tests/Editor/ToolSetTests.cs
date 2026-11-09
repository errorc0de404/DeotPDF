using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Session;
using PdfNotes.Editor.Tools;

namespace PdfNotes.Tests.Editor;

/// <summary>Review 2026-11-08 D4: the canvas tools of a window and how they hand over, without a window.</summary>
public sealed class ToolSetTests
{
    private sealed class FakeCanvas : ICanvasView
    {
        public PageAnchor? HitTestPage(Point2 viewportPoint) => null;
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

    private static ToolSet Make(DocumentSession session, ToolSwitch tools, Action returned, List<string>? order = null) =>
        new(new FakeCanvas(), () => session, tools,
            new(_ => [], () => false, () => StrokeStyle.DefaultPen, () => false, () => StrokeStyle.DefaultHighlighter, () => false,
                () => TextHighlightStyle.Default, () => null, () => StrokeStyle.DefaultPen, () => LineOptions.Default,
                () => ShapeKind.Rectangle, () => ShapeStyle.Default, () => 4, () => 12),
            new((_, _, _) => { }, _ => false, (_, _) => { }, _ => false),
            _ => { }, () => { order?.Add("return"); returned(); }, action => action(), _ => order?.Add("window"));

    [Fact]
    public void ToolSetReturnsToTheSelectionAfterAOneShotTool()
    {
        using var doc = OpenDocument.CreateNote(NewPageSpec.Default);
        var tools = new ToolSwitch(new AppSettings(), () => { });
        int returned = 0;
        var order = new List<string>();
        var set = Make(doc.Session, tools, () => returned++, order);
        Assert.Same(set.Line, set.For(ToolKind.Line));
        Assert.Same(set.StrokeEraser, set.Eraser(ToolKind.StrokeEraser));
        Assert.Same(set.AreaEraser, set.Eraser(ToolKind.AreaEraser));

        set.ReturnAfter(ToolKind.Line); // the line tool selected what it drew
        var press = new CanvasPointer(new(10, 10), InputModifiers.None);
        Assert.True(set.Select.PointerDown(press)); // on nothing: the page pans
        set.Select.PointerUp(press);
        Assert.Equal(1, returned); // the selection is empty: the line tool may come back
        Assert.Equal(["window", "return"], order); // a released text box starts editing first
    }
}
