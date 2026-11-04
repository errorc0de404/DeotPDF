using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PdfNotes.Core.Commands;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

public partial class MainWindow
{
    /// <summary>The visible ribbon tab (<see cref="RibbonViewModel"/>). Changing it never changes the current tool or the keyboard focus.</summary>
    public RibbonTab CurrentRibbonTab => viewModel.Ribbon.Current;

    public void ShowRibbonTab(RibbonTab tab) => viewModel.Ribbon.Current = tab;

    private ToggleButton RibbonTabButton(RibbonTab tab) => tab switch
    {
        RibbonTab.File => FileTabButton,
        RibbonTab.Page => PageTabButton,
        RibbonTab.View => ViewTabButton,
        _ => HomeTabButton,
    };

    private void InitializeRibbon()
    {
        viewModel.Ribbon.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RibbonViewModel.Current)) ShowCurrentTab(); };
        ShowCurrentTab();
        PageList.SelectionChanged += (_, _) => viewModel.Pages.CommandsChanged();
        Activated += (_, _) => viewModel.Pages.CommandsChanged(); // another window may have copied pages meanwhile (spec 11.4, D14g)
        viewModel.Pages.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(PagesViewModel.CanPaste) or nameof(PagesViewModel.CanDelete)) UpdatePageCommands(); };
    }

    /// <summary>Automatic tab switch (spec 3.2, <see cref="RibbonViewModel.Auto"/>).</summary>
    private void AutoRibbonTab(RibbonTab tab) => viewModel.Ribbon.Auto(tab);

    private void RibbonTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name } && Enum.TryParse<RibbonTab>(name, out var tab)) viewModel.Ribbon.Current = tab;
    }

    private void ShowCurrentTab()
    {
        var tab = viewModel.Ribbon.Current;
        RibbonTabButton(tab).IsChecked = true;
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
        PastePageButton.IsEnabled = viewModel.Pages.CanPaste;
        DeletePageButton.IsEnabled = viewModel.Pages.CanDelete;
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
