using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PdfNotes.UI.Views;

/// <summary>The thin lines from sticky note icons to cards dragged away from them (spec 24.9). Drawn under the cards; never saved.</summary>
public sealed class NoteLinks : Control
{
    private static readonly Pen Halo = new(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), 3);
    private static readonly Pen Line = new(new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)), 1);

    /// <summary>Icon edge to the nearest point of its card, in this control's coordinates.</summary>
    public IReadOnlyList<(Point From, Point To)> Segments { get; private set; } = [];

    public void Show(IReadOnlyList<(Point From, Point To)> segments)
    {
        if (Segments.Count == 0 && segments.Count == 0) return;
        Segments = segments;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        foreach (var (from, to) in Segments) context.DrawLine(Halo, from, to);
        foreach (var (from, to) in Segments) context.DrawLine(Line, from, to);
    }
}
