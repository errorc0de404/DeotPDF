using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace PdfNotes.UI.Views;

/// <summary>
/// The app's windows inside the view (roadmap P5c): on Android there is one screen, so a dialog's content is shown on a
/// card over the app with its title; its OK and Cancel end it (<see cref="DialogView"/>), as do Esc and the system's
/// back gesture. Cards stack: a window opened from another (the merge window's range picker) goes on top.
/// </summary>
public sealed partial class MainView
{
    private readonly List<Action> openCards = [];

    /// <summary>A dialog is shown on a card (backups wait meanwhile, spec 18.2).</summary>
    public bool HasDialogCard => openCards.Count > 0;

    /// <summary>Shows <paramref name="dialog"/> on a card; true when it ends with OK.</summary>
    public Task<bool> ShowInViewAsync(DialogView dialog)
    {
        var result = new TaskCompletionSource<bool>();
        var title = new TextBlock { Text = dialog.Title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(20, 16, 20, 0) };
        title.Bind(TextBlock.FontSizeProperty, title.GetResourceObservable("FontSize.Title"));
        var body = new DockPanel { Children = { title, new ScrollViewer { Content = dialog, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto } } };
        DockPanel.SetDock(title, Dock.Top);
        var card = new Border
        {
            Child = body, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BoxShadow = BoxShadows.Parse("0 4 24 0 #40000000"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("Window.Background"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("Border"));
        if (dialog.PreferredSize is { } size) { card.Width = size.Width; card.Height = size.Height; }
        FitCard(card);
        void End(bool ok)
        {
            if (!openCards.Remove(Cancel)) return;
            dialog.Finished = null;
            DialogLayer.Children.Remove(card);
            DialogLayer.IsVisible = openCards.Count > 0;
            dialog.RaiseClosed();
            (dialog as IDisposable)?.Dispose();
            result.TrySetResult(ok);
        }
        void Cancel() { if (dialog.CanClose) End(false); }
        dialog.Finished = End;
        card.KeyDown += (_, e) => { if (e.Key == Key.Escape) { Cancel(); e.Handled = true; } };
        openCards.Add(Cancel);
        DialogLayer.Children.Add(card);
        DialogLayer.IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(dialog.RaiseOpened, Avalonia.Threading.DispatcherPriority.Loaded);
        return result.Task;
    }

    /// <summary>A card stays inside the view, with a margin; its content scrolls when it is larger.</summary>
    private void FitCard(Control card)
    {
        var area = DialogLayer.Bounds.Size is { Width: > 0 } s ? s : Bounds.Size;
        card.MaxWidth = Math.Max(320, area.Width - 48);
        card.MaxHeight = Math.Max(240, area.Height - 48);
    }

    private void InitializeDialogLayer() => DialogLayer.SizeChanged += (_, _) => { foreach (var card in DialogLayer.Children) FitCard(card); };

    /// <summary>Context menus open now, in the order they opened (any menu of the app, whoever made it).</summary>
    private static readonly List<ContextMenu> OpenMenus = Track();

    /// <summary>The last menu still open over this view (menus of other windows are theirs).</summary>
    private ContextMenu? MyOpenMenu()
    {
        var mine = TopLevel.GetTopLevel(this);
        for (int i = OpenMenus.Count - 1; i >= 0; i--)
            if (OpenMenus[i] is { IsOpen: true, PlacementTarget: { } target } menu && ReferenceEquals(TopLevel.GetTopLevel(target), mine)) return menu;
        return null;
    }

    private static List<ContextMenu> Track()
    {
        var open = new List<ContextMenu>();
        ContextMenu.IsOpenProperty.Changed.AddClassHandler<ContextMenu>((menu, _) =>
        {
            open.Remove(menu);
            if (menu.IsOpen) open.Add(menu);
        });
        return open;
    }

    /// <summary>The system's back gesture (Android) closes an open menu, else cancels the card on top.</summary>
    private void BackRequested(object? sender, RoutedEventArgs e)
    {
        // A menu open from a long press or a button: back closes it instead of leaving the app (P5d).
        if (MyOpenMenu() is { } menu)
        {
            menu.Close();
            e.Handled = true;
            return;
        }
        if (CloseTouchMenu()) { e.Handled = true; return; } // the 파일·페이지·보기 list
        if (openCards.Count == 0) return;
        openCards[^1]();
        e.Handled = true;
    }
}
