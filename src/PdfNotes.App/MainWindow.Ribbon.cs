using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PdfNotes.Core.Commands;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

/// <summary>Tabs of the top ribbon (spec 3.2 D13 implementation default). <see cref="Home"/> is the tab named 필기 (2026-10-14).</summary>
public enum RibbonTab { File, Home, Page, View }

public partial class MainWindow
{
    /// <summary>The visible ribbon tab. Changing it never changes the current tool or the keyboard focus.</summary>
    public RibbonTab CurrentRibbonTab { get; private set; } = RibbonTab.Home;

    public void ShowRibbonTab(RibbonTab tab) => RibbonTabButton(tab).IsChecked = true;

    private ToggleButton RibbonTabButton(RibbonTab tab) => tab switch
    {
        RibbonTab.File => FileTabButton,
        RibbonTab.Page => PageTabButton,
        RibbonTab.View => ViewTabButton,
        _ => HomeTabButton,
    };

    private void InitializeRibbon()
    {
        HomeTabButton.IsChecked = true;
        PageList.SelectionChanged += (_, _) => UpdatePageCommands();
        Activated += (_, _) => UpdatePageCommands(); // another window may have copied pages meanwhile (spec 11.4, D14g)
    }

    /// <summary>
    /// Automatic tab switch (spec 3.2, 2026-10-14): work on the page list shows 페이지, work on the canvas (selecting,
    /// drawing, typing, changing the tool) shows 필기. Only between those two — an open 파일 or 보기 tab stays — and only
    /// with the 화면 setting on. Showing a tab never changes the tool, the selection or the keyboard focus.
    /// </summary>
    private void AutoRibbonTab(RibbonTab tab)
    {
        if (holdRibbonTab || !settings.AutoSwitchTabs || CurrentRibbonTab is not (RibbonTab.Home or RibbonTab.Page) || CurrentRibbonTab == tab) return;
        ShowRibbonTab(tab);
    }

    private void RibbonTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name } || !Enum.TryParse<RibbonTab>(name, out var tab)) return;
        CurrentRibbonTab = tab;
        FileTab.Visibility = tab == RibbonTab.File ? Visibility.Visible : Visibility.Collapsed;
        HomeTab.Visibility = tab == RibbonTab.Home ? Visibility.Visible : Visibility.Collapsed;
        PageTab.Visibility = tab == RibbonTab.Page ? Visibility.Visible : Visibility.Collapsed;
        ViewTab.Visibility = tab == RibbonTab.View ? Visibility.Visible : Visibility.Collapsed;
        if (tab == RibbonTab.Page) UpdatePageCommands();
    }

    // ----- page tab: the same commands as the page panel's context menu (spec 11) -----

    /// <summary>Paste needs copied pages that fit this document; delete needs at least one page left.</summary>
    private void UpdatePageCommands()
    {
        if (document is null) return;
        PastePageButton.IsEnabled = CanPastePages;
        DeletePageButton.IsEnabled = SelectedPageIds.Length < document.Session.Current.Pages.Count;
    }

    private void InsertPageClick(object sender, RoutedEventArgs e) => AddBlankPage(true);

    /// <summary>The ▾ of the insert button (spec 11.5): a blank page, or pages of another PDF (11.9).</summary>
    private void InsertPageMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = InsertPageButton, Placement = PlacementMode.Bottom };
        var blank = new MenuItem { Header = Strings.Get("page.blankMenu") };
        blank.Click += (_, _) => AddBlankPage(true);
        menu.Items.Add(blank);
        var import = new MenuItem { Header = Strings.Get("page.importMenu") };
        import.Click += async (_, _) => await ImportPdfPagesAsync();
        menu.Items.Add(import);
        menu.IsOpen = true;
        LastInsertMenu = menu;
    }

    /// <summary>The insert menu opened last (validation harness).</summary>
    public ContextMenu? LastInsertMenu { get; private set; }
    private void CopyPageClick(object sender, RoutedEventArgs e) => CopySelectedPages(PageCopyMode.WithAnnotations);
    private void CopyPageOnlyClick(object sender, RoutedEventArgs e) => CopySelectedPages(PageCopyMode.PageOnly);
    private void PastePageClick(object sender, RoutedEventArgs e) => PastePages();
    private void DeletePageClick(object sender, RoutedEventArgs e) => DeleteSelectedPages();
    private void RotateLeftClick(object sender, RoutedEventArgs e) => RotateSelectedPages(false);
    private void RotateRightClick(object sender, RoutedEventArgs e) => RotateSelectedPages(true);
}
