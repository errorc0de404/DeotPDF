using Avalonia.Controls;
using Avalonia.Input;
using PdfNotes.Core.Commands;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Localization;
using PdfNotes.ViewModels;

namespace PdfNotes.UI.Views;

/// <summary>
/// The ribbon (spec 3.2, D13b, roadmap P4 A5): the quick commands, the four tabs (<see cref="RibbonViewModel"/>) and the
/// commands of 파일, 페이지 and 보기. The 필기 tab's tool buttons are MainView.Tools.cs. Changing the tab never changes
/// the tool or the keyboard focus.
/// </summary>
public sealed partial class MainView
{
    /// <summary>The visible ribbon tab.</summary>
    public RibbonTab CurrentRibbonTab => viewModel.Ribbon.Current;

    public void ShowRibbonTab(RibbonTab tab) => viewModel.Ribbon.Current = tab;

    private void InitializeRibbon()
    {
        // The tab heads and every tab's commands are bound to the view model (R4b–R4e); the zoom box takes a preset
        // from its list or Enter, Esc or leaving shows the zoom again.
        ZoomBox.SelectionChanged += (_, _) =>
        {
            if (ZoomBox.SelectedItem is string text) { viewModel.Navigation.CommitZoomInput(text); ZoomBox.SelectedIndex = -1; }
        };
        ZoomBox.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            if (e.Key == Key.Enter) viewModel.Navigation.CommitZoomInput();
            else viewModel.Navigation.ResetZoomInput();
            Canvas.Focus();
            e.Handled = true;
        };
        ZoomBox.LostFocus += (_, _) => viewModel.Navigation.ResetZoomInput();
    }

    private void InsertMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenInsertMenu();

    /// <summary>The insert menu opened last (tests).</summary>
    public ContextMenu? LastInsertMenu { get; private set; }

    /// <summary>The ▾ of 삽입 (spec 11.5): a blank page, or pages of another PDF (11.9).</summary>
    public ContextMenu OpenInsertMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.Items.Add(Menus.Item(Strings.Get("page.blankMenu"), () => pages.InsertBlankCommand.Execute()));
        menu.Items.Add(Menus.Item(Strings.Get("page.importMenu"), () => _ = ImportPdfPagesAsync()));
        menu.Open(InsertPageButton);
        return LastInsertMenu = menu;
    }

    /// <summary>The recent documents menu opened last (tests).</summary>
    public ContextMenu? LastRecentMenu { get; private set; }

    /// <summary>파일 › 최근 문서 ▾: file names (full path as tooltip), most recent first; each has 목록에서 제거 on its right-click (spec 24.1).</summary>
    public ContextMenu OpenRecentMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (string path in viewModel.Recent.Files.ToArray())
        {
            var item = Menus.Item(viewModel.Locations.NameOf(path), () => _ = viewModel.OpenRecentAsync(path));
            ToolTip.SetTip(item, path);
            item.ContextMenu = new ContextMenu { Items = { Menus.Item(Strings.Get("recent.remove"), () => viewModel.Recent.Remove(path)) } };
            menu.Items.Add(item);
        }
        menu.Open(RecentButton);
        return LastRecentMenu = menu;
    }
}
