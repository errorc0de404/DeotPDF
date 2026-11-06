using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;
using PdfNotes.ViewModels;

namespace PdfNotes.Desktop.Themes;

/// <summary>
/// The icon of a pen favorite (spec 24.7): a colored dot sized by the width (pen), a wide translucent bar (highlighter), a
/// thin line (underline/strikeout), or a dashed ring (empty).
/// </summary>
public sealed class FavoriteMark : Grid
{
    public static readonly StyledProperty<AppSettings.FavoriteSettings?> FavoriteProperty =
        AvaloniaProperty.Register<FavoriteMark, AppSettings.FavoriteSettings?>(nameof(Favorite));

    public FavoriteMark()
    {
        Width = 16;
        Height = 16;
        Update();
    }

    public AppSettings.FavoriteSettings? Favorite { get => GetValue(FavoriteProperty); set => SetValue(FavoriteProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FavoriteProperty) Update();
    }

    private void Update()
    {
        Children.Clear();
        if (Favorite is not { } slot)
        {
            var ring = new Ellipse { Width = 12, Height = 12, StrokeThickness = 1, StrokeDashArray = [2, 2] };
            ring.Bind(Shape.StrokeProperty, ring.GetResourceObservable("Text.Muted"));
            Children.Add(ring);
            return;
        }
        var style = slot.Style.ToStyle(StrokeStyle.DefaultPen, FavoritesViewModel.Kind(slot));
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * (1 - style.TransparencyPercent / 100)), style.Color.R, style.Color.G, style.Color.B));
        double dot = Math.Clamp(4 + style.WidthPt * 1.5, 5, 14);
        Shape mark = slot.Tool != ToolKind.FreeHighlight
            ? new Ellipse { Width = dot, Height = dot, Fill = brush, StrokeThickness = .5 }
            : slot.MarkKind is null ? new Rectangle { Width = 16, Height = 8, RadiusX = 2, RadiusY = 2, Fill = brush }
            : new Rectangle { Width = 16, Height = 3, Fill = brush, VerticalAlignment = slot.MarkKind == TextMarkupKind.Underline ? VerticalAlignment.Bottom : VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, slot.MarkKind == TextMarkupKind.Underline ? 2 : 0) };
        mark.Bind(Shape.StrokeProperty, mark.GetResourceObservable("Divider")); // a white dot stays visible
        Children.Add(mark);
    }
}
