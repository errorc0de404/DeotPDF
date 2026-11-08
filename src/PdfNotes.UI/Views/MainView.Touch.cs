using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using PdfNotes.ViewModels;

namespace PdfNotes.UI.Views;

/// <summary>
/// The touch layout (2026-11-08, Android tablets): the top of the screen is two rows instead of four — one row of
/// [save undo redo | the 필기 tools as icons | 파일 페이지 보기 settings], then the style bar. 파일, 페이지 and 보기 drop
/// down from their buttons as a list (the ribbon's own buttons, one under another); the status bar is a small card at the
/// bottom right. The looks are Themes/TouchLayout.axaml, the sizes Themes/Touch.axaml.
/// </summary>
public sealed partial class MainView
{
    /// <summary>The touch layout is on.</summary>
    public bool IsTouchLayout { get; private set; }

    /// <summary>The 파일·페이지·보기 list is open (tests).</summary>
    public bool IsTouchMenuOpen => TouchMenu.IsOpen;

    /// <summary>Turns the touch layout on (the Android app, before the view is shown). There is no way back.</summary>
    public void UseTouchLayout()
    {
        if (IsTouchLayout) return;
        IsTouchLayout = true;
        Styles.Add(new StyleInclude(new Uri("avares://PdfNotes.UI/")) { Source = new Uri("avares://PdfNotes.UI/Themes/TouchLayout.axaml") });

        // 필기 stays in the top row (Ribbon.HomeShown); the other tabs become the lists of the drop-down.
        foreach (var tab in new[] { FileTab, PageTab, ViewTab })
        {
            RibbonTabs.Children.Remove(tab);
            TouchPanelTabs.Children.Add(tab);
            StackDown(tab);
        }
        var ribbon = viewModel.Ribbon;
        ribbon.PanelsFloat = true;
        ribbon.Current = RibbonTab.Home;
        ribbon.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RibbonViewModel.Current)) ShowTouchMenu(ribbon.Current);
        };

        // A touch on the view but not on the tab buttons closes the list. A popup's touches (the list itself, a ▾ menu
        // from it) are routed through the view too, but they are not on it.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (ribbon.Current == RibbonTab.Home || e.Source is not Visual v || !this.IsVisualAncestorOf(v) || IsInside(v, TouchRight)) return;
            ribbon.Current = RibbonTab.Home;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // A command closes the list; the ones pressed again and again (rotate, zoom) and the ▾ menus keep it.
        Visual[] keep = [RecentButton, InsertPageMenuButton, RotateLeftButton, RotateRightButton, ZoomOutButton, ZoomInButton];
        TouchPanelTabs.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Visual v && keep.Any(k => IsInside(v, k))) return;
            ribbon.Current = RibbonTab.Home;
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // The status bar becomes a card over the document.
        ((Panel)StatusBar.Parent!).Children.Remove(StatusBar);
        DocumentHost.Children.Insert(DocumentHost.Children.IndexOf(LoadingOverlay), StatusBar);
    }

    private static bool IsInside(Visual v, Visual container) => ReferenceEquals(v, container) || v.GetVisualAncestors().Contains(container);

    /// <summary>A ribbon tab as a list: its groups one under another and their buttons too, except the rows that belong together.</summary>
    private void StackDown(StackPanel tab)
    {
        tab.Orientation = Orientation.Vertical;
        // ↺ ↻ stay side by side, under the other page commands.
        if (tab == PageTab && RotateLeftButton.Parent is StackPanel organize)
        {
            organize.Children.Remove(RotateLeftButton);
            organize.Children.Remove(RotateRightButton);
            organize.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { RotateLeftButton, RotateRightButton } });
        }
        foreach (var group in tab.Children.OfType<HeaderedContentControl>())
            if (group.Content is StackPanel row && !row.Children.Contains(ZoomOutButton) && !row.Children.Contains(InsertPageButton))
                row.Orientation = Orientation.Vertical; // − zoom + and 삽입 ▾ stay one row
        if (tab == ViewTab) ZoomBox.Width = 120; // room for "100%" with the touch padding
    }

    private void ShowTouchMenu(RibbonTab tab)
    {
        Control? button = tab switch { RibbonTab.File => TouchFileButton, RibbonTab.Page => TouchPageButton, RibbonTab.View => TouchViewButton, _ => null };
        TouchMenu.IsOpen = false;
        if (button is null) return;
        TouchMenu.PlacementTarget = button;
        TouchMenuScroll.MaxHeight = Math.Max(200, Bounds.Height - RibbonHost.Bounds.Height - 40);
        TouchMenuScroll.Offset = default;
        TouchMenu.IsOpen = true;
    }

    /// <summary>Back (Android) closes the list first.</summary>
    private bool CloseTouchMenu()
    {
        if (!IsTouchLayout || viewModel.Ribbon.Current == RibbonTab.Home) return false;
        viewModel.Ribbon.Current = RibbonTab.Home;
        return true;
    }
}
