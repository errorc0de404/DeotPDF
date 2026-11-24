using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;
using PdfNotes.TextEditor;
using static PdfNotes.Tests.TextEditor.TextEditorModelTests;

namespace PdfNotes.Tests.TextEditor;

/// <summary>1.4 plan 1.3 칸 입력·Undo: a cell edited as a text box, written into its table as one document step.</summary>
public class TableCellEditSessionTests
{
    private static (DocumentSession Session, TableAnnotation Table) NewDocument()
    {
        var session = new DocumentSession(DocumentModel.Create([new(400, 500, 0, PageSource.Generated, PageBackground.Blank)]));
        var table = TableAnnotation.Create(Guid.NewGuid(), new(50, 60), 2, 2, TextEditing.Default);
        session.Execute(DocumentEdits.AddAnnotation(session.Current, session.Current.Pages[0].Id, table));
        session.SealCoalescing();
        return (session, table);
    }

    private static TableAnnotation Table(DocumentSession session, Guid id) => (TableAnnotation)session.Current.FindAnnotation(id)!.Annotation;

    private static (TextEditorModel Editor, TableCellEditSession Link) Begin(DocumentSession session, TableAnnotation table, int row, int column)
    {
        var editor = NewEditor(TableLayout.CellBox(table, row, column), TableLayout.CellStyle(table, row, column));
        return (editor, new TableCellEditSession(session, table.Id, row, column, editor));
    }

    [Fact]
    public void TypingWritesTheCellAsOneStep()
    {
        var (session, table) = NewDocument();
        int steps = session.UndoCount;
        var (editor, link) = Begin(session, table, 1, 0);
        editor.Type("a");
        editor.Type("b");
        Assert.Equal("ab", Table(session, table.Id).Cell(1, 0).Text); // while typing: the row can grow on screen
        editor.Key(EditorKey.Enter);
        editor.Type("c");
        link.Finish();
        Assert.Equal("ab\nc", Table(session, table.Id).Cell(1, 0).Text);
        Assert.Equal(steps + 1, session.UndoCount);
        Assert.Equal(TableCellEditSession.Label, session.UndoLabel);
        Assert.Null(ModelValidator.ValidateAnnotation(Table(session, table.Id), _ => false));
        session.Undo();
        Assert.Equal("", Table(session, table.Id).Cell(1, 0).Text);
    }

    [Fact]
    public void AnEditThatEndsAsItBeganLeavesNoStep()
    {
        var (session, table) = NewDocument();
        int steps = session.UndoCount;
        var (editor, link) = Begin(session, table, 0, 1);
        editor.Type("x");
        editor.Key(EditorKey.Backspace);
        link.Finish();
        Assert.Equal(steps, session.UndoCount);
        Assert.Equal(table.Cell(0, 1), Table(session, table.Id).Cell(0, 1), CellComparer.Instance);
        Assert.True(Table(session, table.Id).Cell(0, 1).Runs[0].Bold); // the header cell keeps its bold typing style
    }

    [Fact]
    public void FinishCommitsTheCompositionAndKeepsTheTypingStyleOfAnEmptiedCell()
    {
        var (session, table) = NewDocument();
        session.Execute(DocumentEdits.UpdateAnnotations(session.Current, [TableEdits.SetCell(table, 1, 1, new("old", [TextEditing.Default with { Start = 0, Length = 3 }], TextAlignment.Left, []))], "test"));
        session.SealCoalescing();
        var (editor, link) = Begin(session, Table(session, table.Id), 1, 1);
        editor.SelectAll();
        editor.Key(EditorKey.Delete);
        editor.Format(r => r with { SizePt = 20 });
        link.Finish();
        var cell = Table(session, table.Id).Cell(1, 1);
        Assert.Equal("", cell.Text);
        Assert.Equal(20, Assert.Single(cell.Runs).SizePt);

        var (again, link2) = Begin(session, Table(session, table.Id), 1, 1);
        again.SetComposition("가");
        link2.Finish();
        Assert.Equal("가", Table(session, table.Id).Cell(1, 1).Text);
    }

    [Fact]
    public void ATableRemovedMeanwhileIsLeftAlone()
    {
        var (session, table) = NewDocument();
        var (editor, link) = Begin(session, table, 0, 0);
        session.Execute(DocumentEdits.DeleteAnnotations(session.Current, [table.Id]));
        editor.Type("z");
        link.Finish();
        Assert.Null(session.Current.FindAnnotation(table.Id));
    }

    private sealed class CellComparer : IEqualityComparer<TableCell>
    {
        public static readonly CellComparer Instance = new();
        public bool Equals(TableCell? a, TableCell? b) => a is not null && b is not null && TableCellEditSession.Same(a, b);
        public int GetHashCode(TableCell c) => c.Text.GetHashCode();
    }
}
