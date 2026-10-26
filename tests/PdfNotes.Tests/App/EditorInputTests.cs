using System.Windows.Input;
using PdfNotes.App.Tools;
using PdfNotes.Core.Geometry;

namespace PdfNotes.Tests.App;

/// <summary>D14c: the WPF shell converts its input to the editor's input types and maps the tools' cursor requests back.</summary>
public class EditorInputTests
{
    [Theory]
    [InlineData(ModifierKeys.None, InputModifiers.None)]
    [InlineData(ModifierKeys.Alt, InputModifiers.Alt)]
    [InlineData(ModifierKeys.Control, InputModifiers.Control)]
    [InlineData(ModifierKeys.Shift, InputModifiers.Shift)]
    [InlineData(ModifierKeys.Windows, InputModifiers.Windows)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, InputModifiers.Control | InputModifiers.Shift)]
    public void ModifiersCastOneToOne(ModifierKeys wpf, InputModifiers editor) => Assert.Equal(editor, (InputModifiers)wpf);

    [Fact]
    public void ToolsLiveInTheEditor()
    {
        foreach (var type in new[] { typeof(ICanvasTool), typeof(SelectTool), typeof(StrokeTool), typeof(EraserTool), typeof(LassoTool), typeof(LineTool),
                     typeof(ShapeTool), typeof(TextTool), typeof(NoteTool), typeof(HighlighterTool), typeof(TextHighlightTool) })
            Assert.Equal("PdfNotes.Editor", type.Assembly.GetName().Name);
    }

    [Fact]
    public void CursorRequestsMapToTheSameWpfCursors()
    {
        Assert.Null(SelectionCursors.For(null));
        Assert.Same(Cursors.Arrow, SelectionCursors.For(ToolCursor.Arrow));
        Assert.Same(Cursors.Pen, SelectionCursors.For(ToolCursor.Pen));
        Assert.Same(Cursors.Cross, SelectionCursors.For(ToolCursor.Cross));
        Assert.Same(Cursors.IBeam, SelectionCursors.For(ToolCursor.IBeam));
        Assert.Same(Cursors.Hand, SelectionCursors.For(ToolCursor.Hand));
        Assert.Same(SelectionCursors.Move, SelectionCursors.For(ToolCursor.Move));
        Assert.Same(Cursors.SizeAll, SelectionCursors.For(ToolCursor.SizeAll));
        Assert.Same(SelectionCursors.RotateFor(1.5), SelectionCursors.For(ToolCursor.Rotate(1.5)));
        Assert.Same(SelectionCursors.Resize(new Point2(1, 1)), SelectionCursors.For(ToolCursor.Resize(new Point2(1, 1))));
    }
}
