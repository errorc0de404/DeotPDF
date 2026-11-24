using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Tools;
using PdfNotes.ViewModels;

namespace PdfNotes.UI.Controls;

/// <summary>One cell of the 표 grid: lit while it is inside the size under the pointer.</summary>
public sealed class TableSizeCell(int row, int column) : ObservableObject
{
    private bool isLit;

    public int Row => row;
    public int Column => column;
    public bool IsLit { get => isLit; set => SetProperty(ref isLit, value); }
}

/// <summary>
/// The 표 button's grid (1.4 plan 1.3): pointing at a cell lights the cells from the top-left corner to it and shows
/// `rows × columns`; a click or a tap runs <see cref="Command"/> with that <see cref="TableSize"/> and raises <see cref="Picked"/>.
/// </summary>
public sealed partial class TableSizePicker : UserControl
{
    public static readonly StyledProperty<ICommand?> CommandProperty = AvaloniaProperty.Register<TableSizePicker, ICommand?>(nameof(Command));

    /// <summary>A cell (16) with its margin (1 on each side): the grid's pitch in DIPs.</summary>
    private const double Pitch = 18;

    public TableSizePicker()
    {
        InitializeComponent();
        Cells = [.. Enumerable.Range(0, TableAnnotation.MaxPick * TableAnnotation.MaxPick).Select(i => new TableSizeCell(i / TableAnnotation.MaxPick, i % TableAnnotation.MaxPick))];
        CellGrid.ItemsSource = Cells;
        CellGrid.PointerMoved += (_, e) => HoverAt(e.GetPosition(CellGrid));
        CellGrid.PointerPressed += (_, e) => HoverAt(e.GetPosition(CellGrid)); // a finger has no hover
        CellGrid.PointerExited += (_, _) => Hover(0, 0);
        CellGrid.PointerReleased += (_, e) =>
        {
            if (SizeAt(e.GetPosition(CellGrid)) is { } size) Pick(size.Rows, size.Columns);
        };
    }

    /// <summary>Runs with the picked size (the tool bar's table arming).</summary>
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    /// <summary>A size was picked: the popup closes.</summary>
    public event EventHandler? Picked;

    public IReadOnlyList<TableSizeCell> Cells { get; }

    /// <summary>The size under the pointer as shown, `3 × 4`, or empty.</summary>
    public string SizeText => SizeLabel.Text ?? "";

    private static TableSize? SizeAt(Point p)
    {
        int column = (int)Math.Floor(p.X / Pitch), row = (int)Math.Floor(p.Y / Pitch);
        return (uint)row < TableAnnotation.MaxPick && (uint)column < TableAnnotation.MaxPick ? new(row + 1, column + 1) : null;
    }

    private void HoverAt(Point p)
    {
        var size = SizeAt(p);
        Hover(size?.Rows ?? 0, size?.Columns ?? 0);
    }

    /// <summary>Lights the cells of a <paramref name="rows"/> × <paramref name="columns"/> table (0: none).</summary>
    internal void Hover(int rows, int columns)
    {
        foreach (var cell in Cells) cell.IsLit = cell.Row < rows && cell.Column < columns;
        SizeLabel.Text = rows > 0 && columns > 0 ? $"{rows} × {columns}" : "";
    }

    /// <summary>Picks <paramref name="rows"/> × <paramref name="columns"/>: the command runs, then <see cref="Picked"/>.</summary>
    internal void Pick(int rows, int columns)
    {
        var size = new TableSize(rows, columns);
        Hover(0, 0); // the grid opens empty next time
        if (Command?.CanExecute(size) == true) Command.Execute(size);
        Picked?.Invoke(this, EventArgs.Empty);
    }
}
