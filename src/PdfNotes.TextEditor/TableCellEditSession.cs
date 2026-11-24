using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;

namespace PdfNotes.TextEditor;

/// <summary>
/// A table cell edited as a text box (1.4 plan 1.3 칸 입력·Undo): the editor edits the cell box (TableLayout.CellBox) and
/// every change is written into the table at once (<see cref="TableEdits.SetCell"/>), so the row grows with the text on
/// screen, but the whole edit — entering the cell until leaving it — is one document Undo step. An edit that ends as it
/// began leaves no step. Box-only properties (width, fill, border) are not part of a cell and are not written.
/// </summary>
public sealed class TableCellEditSession : ITextEditSession
{
    public const string Label = "undo.tableCell";

    private readonly DocumentSession session;
    private readonly TextEditorModel model;
    private readonly string key;
    private readonly TableCell? original;
    private readonly int startCount;
    private bool finished;

    public TableCellEditSession(DocumentSession session, Guid tableId, int row, int column, TextEditorModel model)
    {
        this.session = session; this.model = model;
        TableId = tableId; Row = row; Column = column;
        key = $"cell:{tableId}:{row}:{column}";
        original = Find() is { } table ? table.Cell(row, column) : null;
        session.SealCoalescing();
        startCount = session.UndoCount;
        model.Edited += OnEdited;
    }

    public TextEditorModel Model => model;
    public Guid TableId { get; }
    public int Row { get; }
    public int Column { get; }

    private TableAnnotation? Find() =>
        session.Current.FindAnnotation(TableId)?.Annotation is TableAnnotation { Locked: false } t && Row < t.Rows && Column < t.Columns ? t : null;

    private void OnEdited(object? sender, TextEditedEventArgs e)
    {
        if (!finished) Write(e.Box);
    }

    /// <summary>Writes the cell into this edit's one step, unless the table is gone or the cell is already so.</summary>
    private void Write(TextBoxAnnotation box)
    {
        if (Find() is not { } table) return;
        var cell = TableLayout.FromBox(box, model.TypingStyle with { Start = 0, Length = 0 });
        if (Same(table.Cell(Row, Column), cell)) return;
        var after = TableEdits.SetCell(table, Row, Column, cell);
        if (ModelValidator.ValidateAnnotation(after, session.Current.Resources.ContainsKey) is not null) return;
        session.Execute(DocumentEdits.UpdateAnnotations(session.Current, [after], Label, key));
    }

    /// <summary>Ends editing: the composition is completed and written; an edit that changed nothing in the end is taken back.</summary>
    public void Finish()
    {
        if (finished) return;
        model.CompleteInput();
        Write(model.Box);
        finished = true;
        model.Edited -= OnEdited;
        // A save in the middle ends the coalescing and splits the edit; every step it made is then taken back.
        if (original is not null && Find() is { } now && Same(now.Cell(Row, Column), original))
            while (session.UndoCount > startCount && session.UndoLabel == Label) session.Undo();
        session.SealCoalescing();
    }

    /// <summary>
    /// The table as the canvas draws it while the cell is edited: the editor draws the cell's text, so the cell is left
    /// empty here, with its row kept as tall as the text makes it.
    /// </summary>
    public TableAnnotation WithoutEditedText(TableAnnotation table)
    {
        if (table.Id != TableId || Row >= table.Rows || Column >= table.Columns) return table;
        var grid = TableLayout.Measure(table);
        var empty = TableCell.Empty(TableLayout.CellStyle(table, Row, Column));
        return TableEdits.SetCell(table, Row, Column, empty) with { RowHeights = table.RowHeights.SetItem(Row, grid.RowY[Row + 1] - grid.RowY[Row]) };
    }

    /// <summary>The cell's rectangle in table-local points (column width × drawn row height).</summary>
    public Rect2? CellRect(TableAnnotation table)
    {
        if (table.Id != TableId || Row >= table.Rows || Column >= table.Columns) return null;
        var grid = TableLayout.Measure(table);
        return new(grid.ColumnX[Column], grid.RowY[Row], grid.ColumnX[Column + 1] - grid.ColumnX[Column], grid.RowY[Row + 1] - grid.RowY[Row]);
    }

    public static bool Same(TableCell a, TableCell b) => ReferenceEquals(a, b) ||
        a.Text == b.Text && a.Alignment == b.Alignment && a.Runs.SequenceEqual(b.Runs) && a.Highlights.SequenceEqual(b.Highlights);
}
