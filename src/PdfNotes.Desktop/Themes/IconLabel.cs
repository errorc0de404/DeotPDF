using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace PdfNotes.Desktop.Themes;

/// <summary>The content of a ribbon button: an icon and a word (the WPF `[Icon glyph][TextBlock]` pair). Either may be left out.</summary>
public sealed class IconLabel : StackPanel
{
    public static readonly StyledProperty<string?> GlyphProperty = AvaloniaProperty.Register<IconLabel, string?>(nameof(Glyph));
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<IconLabel, string?>(nameof(Text));

    private readonly AppIcon icon = new();
    private readonly TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center };

    public IconLabel()
    {
        Orientation = Orientation.Horizontal;
        Children.Add(icon);
        Children.Add(label);
        Update();
    }

    public string? Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>The icon (its outline for drawn icons such as the shape tool's).</summary>
    public AppIcon IconPart => icon;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GlyphProperty || change.Property == TextProperty) Update();
    }

    private void Update()
    {
        icon.Glyph = Glyph;
        label.Text = Text;
        bool hasIcon = Glyph is not null || icon.Outline is not null;
        icon.IsVisible = hasIcon;
        label.IsVisible = !string.IsNullOrEmpty(Text);
        // Space.IconGap (0,0,6,0) between the two, none when the icon stands alone.
        icon.Margin = label.IsVisible ? new Thickness(0, 0, 6, 0) : default;
    }

    /// <summary>Shows a drawn outline instead of a glyph (the shape button follows the shape tool).</summary>
    public void SetOutline(string data)
    {
        icon.Glyph = null;
        icon.Outline = data;
        Update();
    }
}
