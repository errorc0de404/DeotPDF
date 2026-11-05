using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PdfNotes.Core.Models;
using PdfNotes.Desktop.Themes;
using PdfNotes.Editor.Localization;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// The color entry behind a palette's + (spec 3.2, the WPF ColorPickerPanel): a saturation × brightness plane with a hue
/// strip, the color code, the eyedropper and 추가. 추가 or Enter in the code picks the color; the eyedropper asks the owner
/// to sample a pixel of the window. The plane shows annotation colors (HSV), not frame colors.
/// </summary>
public sealed class ColorPickerPanel : StackPanel
{
    public const double PlaneWidth = 168, PlaneHeight = 120, StripWidth = 16;
    private double hue, saturation, brightness;
    private bool syncing;
    private readonly Border plane = new() { Width = PlaneWidth, Height = PlaneHeight, Cursor = new Cursor(StandardCursorType.Cross), BorderThickness = new Thickness(1), ClipToBounds = true };
    private readonly Ellipse planeMark = new() { Width = 12, Height = 12, StrokeThickness = 2, IsHitTestVisible = false, Stroke = Brushes.White };
    private readonly Rectangle strip = new() { Width = StripWidth, Height = PlaneHeight, Cursor = new Cursor(StandardCursorType.Hand) };
    private readonly Rectangle stripMark = new() { Width = StripWidth + 4, Height = 4, StrokeThickness = 1.5, IsHitTestVisible = false };
    private readonly Border preview = new() { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };

    public Control Plane => plane;
    public Control Strip => strip;
    public TextBox Code { get; }
    public Button Eyedropper { get; }
    public Button Add { get; }

    public RgbColor SelectedColor => FromHsv(hue, saturation, brightness);

    public event Action<RgbColor>? Picked;
    public event Action? EyedropperRequested;

    public ColorPickerPanel(string name)
    {
        Name = name + "Picker";
        var marks = new Avalonia.Controls.Canvas { IsHitTestVisible = false };
        marks.Children.Add(planeMark);
        plane.Child = new Panel
        {
            Children =
            {
                // white → clear from the left (saturation), clear → black to the bottom (brightness)
                new Rectangle { Fill = Gradient(Colors.White, Color.FromArgb(0, 255, 255, 255), vertical: false) },
                new Rectangle { Fill = Gradient(Color.FromArgb(0, 0, 0, 0), Colors.Black, vertical: true) },
                marks
            }
        };
        plane.Bind(Border.BorderBrushProperty, plane.GetResourceObservable("Control.Border"));
        var hues = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative) };
        for (int i = 0; i <= 6; i++) hues.GradientStops.Add(new GradientStop(Media(FromHsv(i * 60, 1, 1)), i / 6.0));
        strip.Fill = hues;
        stripMark.Bind(Shape.StrokeProperty, stripMark.GetResourceObservable("Text.Primary"));
        var stripHost = new Avalonia.Controls.Canvas { Width = StripWidth, Height = PlaneHeight, Margin = new Thickness(8, 0, 0, 0), Children = { strip, stripMark } };
        Avalonia.Controls.Canvas.SetLeft(stripMark, -2);

        Track(plane, p => SetHsv(hue, Math.Clamp(p.X / PlaneWidth, 0, 1), 1 - Math.Clamp(p.Y / PlaneHeight, 0, 1)));
        Track(strip, p => SetHsv(Math.Clamp(p.Y / PlaneHeight, 0, 1) * 359.999, saturation, brightness));

        preview.Bind(Border.BorderBrushProperty, preview.GetResourceObservable("Control.Border"));
        Code = new TextBox { Name = name + "Box", Width = 80, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "bar" } };
        Code.Bind(TextBox.FontFamilyProperty, Code.GetResourceObservable("Font.Mono"));
        Code.TextChanged += (_, _) =>
        {
            if (syncing || !TryParse(Code.Text, out var typed)) return;
            ShowColor(typed, keepCode: true);
        };
        Code.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || !Add!.IsEnabled || !TryParse(Code.Text, out RgbColor _)) return;
            Picked?.Invoke(SelectedColor);
            e.Handled = true;
        };
        Eyedropper = new Button { Name = name + "Eyedropper", Margin = new Thickness(4, 0, 0, 0), Content = new AppIcon { Glyph = "Eyedropper" } };
        Eyedropper.Bind(ThemeProperty, Eyedropper.GetResourceObservable("ToolButton"));
        ToolTip.SetTip(Eyedropper, Strings.Get("color.eyedropper"));
        Eyedropper.Click += (_, _) => EyedropperRequested?.Invoke();
        Add = new Button { Name = name + "Add", Content = Strings.Get("color.add"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2) };
        Add.Click += (_, _) => Picked?.Invoke(SelectedColor);

        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { plane, stripHost } });
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children = { preview, Code, Eyedropper, Add } });
        ShowColor(RgbColor.Black);
    }

    private static LinearGradientBrush Gradient(Color from, Color to, bool vertical) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(vertical ? 0 : 1, vertical ? 1 : 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) }
    };

    /// <summary>Shows a color in the plane, the strip and the code (opening the picker shows the target's color).</summary>
    public void ShowColor(RgbColor color, bool keepCode = false)
    {
        var (h, s, v) = ToHsv(color);
        if (s == 0 || v == 0) h = hue; // gray: keep the hue the user had
        hue = h; saturation = s; brightness = v;
        Refresh(keepCode);
    }

    /// <summary>Sets the plane and strip as a click at these fractions would (tests).</summary>
    public void SetHsv(double h, double s, double v)
    {
        hue = h; saturation = s; brightness = v;
        Refresh(keepCode: false);
    }

    private void Refresh(bool keepCode)
    {
        plane.Background = new SolidColorBrush(Media(FromHsv(hue, 1, 1)));
        Avalonia.Controls.Canvas.SetLeft(planeMark, saturation * PlaneWidth - planeMark.Width / 2 - 1);
        Avalonia.Controls.Canvas.SetTop(planeMark, (1 - brightness) * PlaneHeight - planeMark.Height / 2 - 1);
        Avalonia.Controls.Canvas.SetTop(stripMark, hue / 360 * PlaneHeight - stripMark.Height / 2);
        preview.Background = new SolidColorBrush(Media(SelectedColor));
        if (keepCode) return;
        syncing = true;
        try { Code.Text = SelectedColor.ToHex(); }
        finally { syncing = false; }
    }

    /// <summary>Press and drag on the plane or the strip (the pointer is captured while the button is down).</summary>
    private static void Track(Control element, Action<Point> set)
    {
        element.PointerPressed += (_, e) => { e.Pointer.Capture(element); set(e.GetPosition(element)); e.Handled = true; };
        element.PointerMoved += (_, e) => { if (ReferenceEquals(e.Pointer.Captured, element)) set(e.GetPosition(element)); };
        element.PointerReleased += (_, e) => e.Pointer.Capture(null);
    }

    public static bool TryParse(string? text, out RgbColor color)
    {
        text = (text ?? "").Trim();
        return RgbColor.TryParse(text.StartsWith('#') ? text : "#" + text, out color);
    }

    private static Color Media(RgbColor c) => Color.FromRgb(c.R, c.G, c.B);

    /// <summary>Hue 0–360, saturation and brightness 0–1.</summary>
    public static RgbColor FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360 / 60;
        double c = v * s, x = c * (1 - Math.Abs(h % 2 - 1)), m = v - c;
        var (r, g, b) = (int)h switch { 0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x) };
        static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        return new(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    public static (double H, double S, double V) ToHsv(RgbColor color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }
}
