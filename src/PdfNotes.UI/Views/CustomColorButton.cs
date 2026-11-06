using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Styling;
using PdfNotes.Core.Models;
using PdfNotes.UI.Themes;
using PdfNotes.Editor.Localization;

namespace PdfNotes.UI.Views;

/// <summary>
/// The + of a palette (spec 3.2): a popup with the color plane, hue strip, the code, the eyedropper and 추가. 추가 or
/// Enter runs <see cref="Command"/> with the color (it becomes a user color), Esc closes. The eyedropper is the
/// window's (<see cref="EyedropperRequested"/>).
/// </summary>
public sealed class CustomColorButton : Panel
{
    public static readonly StyledProperty<RgbColor> StartColorProperty = AvaloniaProperty.Register<CustomColorButton, RgbColor>(nameof(StartColor));
    public static readonly StyledProperty<bool> CanAddProperty = AvaloniaProperty.Register<CustomColorButton, bool>(nameof(CanAdd), true);
    public static readonly StyledProperty<ICommand?> CommandProperty = AvaloniaProperty.Register<CustomColorButton, ICommand?>(nameof(Command));

    private readonly ToggleButton button;
    private readonly Popup popup;

    public CustomColorButton()
    {
        button = new ToggleButton { Padding = new Thickness(6, 3), Content = new AppIcon { Glyph = "Add", Size = 14 } };
        ToolTip.SetTip(button, Strings.Get("color.custom"));
        Picker = new ColorPickerPanel("");
        var frame = new Border { BorderThickness = new Thickness(1), Child = Picker, Padding = new Thickness(10), CornerRadius = new CornerRadius(6) };
        frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("Surface.Background"));
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Border"));
        popup = new Popup { PlacementTarget = button, Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true, Child = frame };
        button.Click += (_, _) => popup.IsOpen = button.IsChecked == true;
        popup.Closed += (_, _) => button.IsChecked = false;
        popup.Opened += (_, _) => { Picker.Add.IsEnabled = CanAdd; Picker.ShowColor(StartColor); Picker.Code.Focus(); Picker.Code.SelectAll(); };
        Picker.Picked += color => { popup.IsOpen = false; Pick(color); };
        Picker.EyedropperRequested += () => { popup.IsOpen = false; EyedropperRequested?.Invoke(); };
        Picker.KeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; Done?.Invoke(); e.Handled = true; } };
        Children.Add(button);
        Children.Add(popup);
    }

    /// <summary>The color the window opens with.</summary>
    public RgbColor StartColor { get => GetValue(StartColorProperty); set => SetValue(StartColorProperty, value); }

    /// <summary>The palettes have room for one more user color.</summary>
    public bool CanAdd { get => GetValue(CanAddProperty); set => SetValue(CanAddProperty, value); }

    /// <summary>Takes the picked color.</summary>
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    /// <summary>The color window (tests).</summary>
    public ColorPickerPanel Picker { get; }

    /// <summary>The eyedropper was chosen: the window samples a pixel and calls <see cref="Pick"/>.</summary>
    public event Action? EyedropperRequested;

    /// <summary>A color was picked or the window closed with Esc: the keyboard can go back.</summary>
    public event Action? Done;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NameProperty && Name is { } name)
        {
            button.Name = name + "Button";
            popup.Name = name + "Popup";
        }
    }

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        if (this.TryFindResource("ToolButton", out var theme) && theme is ControlTheme t) button.Theme = t;
    }

    /// <summary>Applies a color as 추가 does.</summary>
    public void Pick(RgbColor color)
    {
        if (Command is { } command && command.CanExecute(color)) command.Execute(color);
        Done?.Invoke();
    }
}
