using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace PdfNotes.UI.Themes;

/// <summary>
/// An icon of the app (roadmap P4 A5): a Fluent UI System Icons path (<see cref="IconData"/>, drawn the same on Windows,
/// macOS and Linux) or one of the app's own outlines (<see cref="Outline"/>: the shape tools). It takes the text color
/// of its button (<c>Foreground</c> inherited), so the on, off and disabled states follow the button like a glyph would.
/// </summary>
public sealed class AppIcon : Control
{
    public static readonly StyledProperty<string?> GlyphProperty = AvaloniaProperty.Register<AppIcon, string?>(nameof(Glyph));
    public static readonly StyledProperty<string?> OutlineProperty = AvaloniaProperty.Register<AppIcon, string?>(nameof(Outline));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<AppIcon, double>(nameof(Size), 16);
    public static readonly StyledProperty<bool> MirroredProperty = AvaloniaProperty.Register<AppIcon, bool>(nameof(Mirrored));

    private static readonly Dictionary<string, Geometry> Cache = [];

    static AppIcon()
    {
        AffectsRender<AppIcon>(GlyphProperty, OutlineProperty, MirroredProperty, TextElement.ForegroundProperty);
        AffectsMeasure<AppIcon>(SizeProperty);
    }

    public AppIcon() => VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;

    /// <summary>A key of <see cref="IconData.Paths"/> (filled, 20 × 20 box).</summary>
    public string? Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    /// <summary>Path data drawn as a 1.1 DIP outline in a 16 × 16 box (the app's own icons, Icon.Stroke).</summary>
    public string? Outline { get => GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }

    /// <summary>The icon cell in DIPs (Icon.Size, 16; FontSize.IconSmall icons 12).</summary>
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    /// <summary>Drawn left-right mirrored (one rotate glyph serves 왼쪽으로 회전 this way).</summary>
    public bool Mirrored { get => GetValue(MirroredProperty); set => SetValue(MirroredProperty, value); }

    public static bool Exists(string glyph) => IconData.Paths.ContainsKey(glyph);

    private static Geometry Parse(string data)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(data, out var geometry)) Cache[data] = geometry = Geometry.Parse(data);
            return geometry;
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        var brush = TextElement.GetForeground(this) ?? Brushes.Black;
        double s = Size;
        using var mirror = context.PushTransform(Mirrored ? Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(s, 0) : Matrix.Identity);
        if (Glyph is { } glyph && IconData.Paths.TryGetValue(glyph, out var data))
        {
            // Fluent's 20 px icons keep a 2 px margin, so the inner 16 px are what fills this icon's cell (the visual size
            // and stroke weight of icon font glyphs, which fill their cell).
            using var scale = context.PushTransform(Matrix.CreateTranslation(-2, -2) * Matrix.CreateScale(s / 16, s / 16));
            context.DrawGeometry(brush, null, Parse(data));
        }
        else if (Outline is { } outline)
        {
            using var scale = context.PushTransform(Matrix.CreateScale(s / 16, s / 16));
            context.DrawGeometry(null, new Pen(brush, 1.1, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Parse(outline));
        }
    }

    /// <summary>The 표 button (1.4 plan 1.3): a frame with two row and two column lines.</summary>
    public const string TableOutline = "M1.5,2.5 L14.5,2.5 L14.5,13.5 L1.5,13.5 Z M1.5,6.17 L14.5,6.17 M1.5,9.83 L14.5,9.83 M5.83,2.5 L5.83,13.5 M10.17,2.5 L10.17,13.5";

    /// <summary>The outline of a shape group tool.</summary>
    public static string ShapeOutline(PdfNotes.Editor.Tools.ToolKind tool, PdfNotes.Core.Models.ShapeKind shape) => tool switch
    {
        PdfNotes.Editor.Tools.ToolKind.Line => "M2,14 L14,2",
        PdfNotes.Editor.Tools.ToolKind.Curve => "M1.5,14 C3.5,1.5 10,15.5 14.5,2",
        _ => shape switch
        {
            PdfNotes.Core.Models.ShapeKind.Ellipse => "M8,2.5 A6.5,5.5 0 1 1 7.99,2.5 Z",
            PdfNotes.Core.Models.ShapeKind.Triangle => "M8,2 L14.5,14 L1.5,14 Z",
            PdfNotes.Core.Models.ShapeKind.Polygon => "M2,5.5 L8.5,1.5 L14.5,6 L12,14 L3.5,13 Z",
            _ => "M1.5,3 L14.5,3 L14.5,13 L1.5,13 Z"
        }
    };
}
