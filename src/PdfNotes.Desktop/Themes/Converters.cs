using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using PdfNotes.Core.Models;
using PdfNotes.ViewModels;

namespace PdfNotes.Desktop.Themes;

/// <summary>Value converters the window's bindings use (roadmap P4.5 R4d).</summary>
public static class Converters
{
    /// <summary>An annotation color (<see cref="RgbColor"/>) as a brush: a palette swatch.</summary>
    public static readonly IValueConverter ColorBrush = new FuncValueConverter<RgbColor, IBrush>(c => new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)));

    /// <summary>The shape button's drawn icon (<see cref="ShapeIcon"/>).</summary>
    public static readonly IValueConverter ShapeOutline = new FuncValueConverter<ShapeIcon?, string?>(i => i is null ? null : AppIcon.ShapeOutline(i.Tool, i.Shape));
}
