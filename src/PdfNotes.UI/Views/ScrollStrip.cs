using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Metadata;
using Avalonia.Styling;
using PdfNotes.UI.Themes;

namespace PdfNotes.UI.Views;

/// <summary>
/// A row that never wraps (2026-11-08): when the content is wider than the room, it scrolls sideways — ‹ › at the ends
/// (each shown only when there is more that way), a finger or pen drag, or the mouse wheel.
/// </summary>
public sealed class ScrollStrip : Panel
{
    public static readonly StyledProperty<Control?> ChildProperty = AvaloniaProperty.Register<ScrollStrip, Control?>(nameof(Child));

    private readonly ScrollViewer scroller;
    private readonly Button back;
    private readonly Button forward;

    public ScrollStrip()
    {
        scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        back = Arrow("ChevronLeft", HorizontalAlignment.Left, -1);
        forward = Arrow("ChevronRight", HorizontalAlignment.Right, 1);
        scroller.ScrollChanged += (_, _) => UpdateArrows();
        scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty) UpdateArrows();
        };
        // The wheel goes sideways (there is nothing to scroll up or down).
        scroller.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            double delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            if (delta == 0 || !CanScroll) return;
            ScrollTo(scroller.Offset.X - delta * 48);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Children.Add(scroller);
        Children.Add(back);
        Children.Add(forward);
        UpdateArrows();
    }

    [Content]
    public Control? Child { get => GetValue(ChildProperty); set => SetValue(ChildProperty, value); }

    /// <summary>The ‹ and › (tests).</summary>
    public Button BackButton => back;

    public Button ForwardButton => forward;

    public double Offset => scroller.Offset.X;

    private bool CanScroll => scroller.Extent.Width > scroller.Viewport.Width + 0.5;

    private double MaxOffset => Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);

    public void ScrollToStart() => ScrollTo(0);

    public void ScrollTo(double x) => scroller.Offset = new Vector(Math.Clamp(x, 0, MaxOffset), 0);

    /// <summary>One press of ‹ or ›: most of the visible width, so a group at the edge stays in sight.</summary>
    public void Step(int direction) => ScrollTo(scroller.Offset.X + direction * Math.Max(40, scroller.Viewport.Width * 0.7));

    private Button Arrow(string glyph, HorizontalAlignment side, int direction)
    {
        var button = new Button
        {
            Content = new AppIcon { Glyph = glyph, Size = 14 },
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Focusable = false,
            IsVisible = false,
        };
        button.Click += (_, _) => Step(direction);
        return button;
    }

    private void UpdateArrows()
    {
        back.IsVisible = CanScroll && scroller.Offset.X > 0.5;
        forward.IsVisible = CanScroll && scroller.Offset.X < MaxOffset - 0.5;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChildProperty) scroller.Content = change.NewValue;
        else if (change.Property == NameProperty && Name is { } name)
        {
            back.Name = name + "Back";
            forward.Name = name + "Forward";
        }
    }

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        // The arrows sit over the row's ends, so they need the bar's color behind them.
        if (this.TryFindResource("ToolButton", out var theme) && theme is ControlTheme t)
        {
            back.Theme = t;
            forward.Theme = t;
        }
        foreach (var arrow in new[] { back, forward })
        {
            arrow.Margin = default;
            arrow.Bind(Button.BackgroundProperty, arrow.GetResourceObservable("Bar.Background"));
        }
    }
}
