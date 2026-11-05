using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PdfNotes.Editor.Localization;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// The frame of the app's dialogs (roadmap P4 A5): the theme's colors, the content, then the buttons at the bottom right
/// outside any scrolling, default button first (D13c). OK runs <see cref="Accept"/>; the window closes with true only when
/// it accepts. Esc cancels.
/// </summary>
public abstract class AppDialog : Window, IAcceptDialog
{
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };

    protected AppDialog(string title)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DialogLook.Apply(this);
        error.Bind(TextBlock.ForegroundProperty, error.GetResourceObservable("Text.Danger"));
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(false); e.Handled = true; } };
    }

    /// <summary>The default button (tests and Enter).</summary>
    public Button OkButton { get; } = new() { MinWidth = 84, Padding = new Thickness(10, 4), IsDefault = true };

    public Button CancelButton { get; } = new() { MinWidth = 84, Padding = new Thickness(10, 4), Content = Strings.Get("common.cancel") };

    /// <summary>Puts the body and the buttons into the window.</summary>
    protected void Build(Control body, string okText)
    {
        OkButton.Content = okText;
        OkButton.Click += (_, _) => { if (Accept()) Close(true); };
        CancelButton.Click += (_, _) => Close(false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 16, 0, 0), Children = { OkButton, CancelButton } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(error, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(20), Children = { buttons, error, body } };
    }

    /// <summary>
    /// A resizable window of a preferred size that fits the screen it opens on (the WPF ScreenArea.Fit): never larger than
    /// the work area, never smaller than the minimum.
    /// </summary>
    protected void Fit(double width, double height, double minWidth, double minHeight) => DialogLook.Fit(this, width, height, minWidth, minHeight);

    public abstract bool Accept();

    /// <summary>Shows why the input was not accepted (the window stays open).</summary>
    protected bool Fail(string message)
    {
        error.Text = message;
        error.IsVisible = true;
        return false;
    }

    protected static string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    protected static bool TryParse(string? text, out double value) =>
        double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    protected static TextBlock Label(string text, double bottom = 10) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, bottom) };

    protected static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable("Text.Muted"));
        block.Bind(TextBlock.FontSizeProperty, block.GetResourceObservable("FontSize.Caption"));
        return block;
    }

    /// <summary>A grid of label + field rows.</summary>
    protected static Grid Form(params (Control? Label, Control Field)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var (label, field) = rows[i];
            if (label is not null) { Grid.SetRow(label, i); grid.Children.Add(label); }
            Grid.SetRow(field, i);
            Grid.SetColumn(field, label is null ? 0 : 1);
            if (label is null) Grid.SetColumnSpan(field, 2);
            grid.Children.Add(field);
        }
        return grid;
    }
}
