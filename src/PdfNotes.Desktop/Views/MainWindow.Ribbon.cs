using Avalonia.Controls;
using Avalonia.Input;
using PdfNotes.Core.Commands;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Localization;
using PdfNotes.ViewModels;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// The ribbon (spec 3.2, D13b, roadmap P4 A5): the quick commands, the four tabs (<see cref="RibbonViewModel"/>) and the
/// commands of 파일, 페이지 and 보기. The 필기 tab's tool buttons are MainWindow.Tools.cs. Changing the tab never changes
/// the tool or the keyboard focus.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The visible ribbon tab.</summary>
    public RibbonTab CurrentRibbonTab => viewModel.Ribbon.Current;

    public void ShowRibbonTab(RibbonTab tab) => viewModel.Ribbon.Current = tab;

    private void InitializeRibbon()
    {
        foreach (var head in new[] { FileTabButton, HomeTabButton, PageTabButton, ViewTabButton })
            head.Click += (_, _) => { if (head.Tag is string name && Enum.TryParse<RibbonTab>(name, out var tab)) viewModel.Ribbon.Current = tab; };
        viewModel.Ribbon.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RibbonViewModel.Current)) ShowCurrentTab(); };
        ShowCurrentTab();

        // Quick commands and 파일
        QuickOpenButton.Click += (_, _) => _ = OpenWithDialogAsync();
        OpenButton.Click += (_, _) => _ = OpenWithDialogAsync();
        QuickSaveButton.Click += (_, _) => _ = SaveAsync(saveAs: false);
        SaveButton.Click += (_, _) => _ = SaveAsync(saveAs: false);
        SaveAsButton.Click += (_, _) => _ = SaveAsync(saveAs: true);
        UndoButton.Click += (_, _) => Undo();
        RedoButton.Click += (_, _) => Redo();
        NewButton.Click += (_, _) => _ = NewNoteAsync();
        RecentButton.Click += (_, _) => OpenRecentMenu();
        MergePdfsButton.Click += (_, _) => _ = MergePdfsAsync();
        ExportFlattenedButton.Click += (_, _) => _ = ExportPagesAsync(allPages: true, flatten: true); // the export window, preset (spec 11.10)
        ExportPagesFileButton.Click += (_, _) => _ = ExportPagesAsync(allPages: true);
        SettingsButton.Click += (_, _) => ShowSettings();
        SettingsRibbonButton.Click += (_, _) => ShowSettings();

        // 필기 (the tools are wired in MainWindow.Tools.cs)
        InsertImageButton.Click += (_, _) => _ = InsertImageFromFileAsync();
        ForeignEditButton.Click += (_, _) => _ = ConvertForeignAsync();

        // 페이지: the page list's context menu commands
        InsertPageButton.Click += (_, _) => AddBlankPage(true);
        InsertPageMenuButton.Click += (_, _) => OpenInsertMenu();
        CopyPageButton.Click += (_, _) => viewModel.Pages.Copy(PageCopyMode.WithAnnotations);
        CopyPageOnlyButton.Click += (_, _) => viewModel.Pages.Copy(PageCopyMode.PageOnly);
        PastePageButton.Click += (_, _) => viewModel.Pages.Paste();
        ExportPagesButton.Click += (_, _) => _ = ExportPagesAsync();
        DeletePageButton.Click += (_, _) => viewModel.Pages.DeleteSelected();
        ClearPageNotesButton.Click += (_, _) => ClearSelectedPageNotes();
        RotateLeftButton.Click += (_, _) => viewModel.Pages.RotateSelected(false);
        RotateRightButton.Click += (_, _) => viewModel.Pages.RotateSelected(true);
        ShrinkPageButton.Click += (_, _) => ShowPageTransform(false);
        ResizePageButton.Click += (_, _) => ShowPageTransform(true);
        PagePanelButton.Click += (_, _) => SetPagePanelVisible(PagePanelButton.IsChecked == true);
        viewModel.Pages.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(PagesViewModel.CanPaste) or nameof(PagesViewModel.CanDelete)) UpdatePageCommands(); };
        viewModel.Recent.PropertyChanged += (_, _) => UpdateCommands(); // the recent button is off when the list empties

        // 보기
        ZoomOutButton.Click += (_, _) => viewModel.Navigation.ZoomBy(1 / 1.25);
        ZoomInButton.Click += (_, _) => viewModel.Navigation.ZoomBy(1.25);
        ZoomBox.ItemsSource = ZoomSteps.MenuPercents.Where(p => p <= 800).Select(p => p + "%").ToArray();
        ZoomBox.SelectionChanged += (_, _) =>
        {
            if (ZoomBox.SelectedItem is string text) { viewModel.Navigation.SetZoomText(text); ZoomBox.SelectedIndex = -1; UpdateViewStatus(); }
        };
        ZoomBox.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            if (e.Key == Key.Enter) viewModel.Navigation.SetZoomText(ZoomBox.Text ?? "");
            Canvas.Focus();
            UpdateViewStatus();
            e.Handled = true;
        };
        ZoomBox.LostFocus += (_, _) => UpdateViewStatus();
        FitWidthButton.Click += (_, _) => viewModel.Navigation.FitWidth();
        FitPageButton.Click += (_, _) => viewModel.Navigation.FitPage();
        ContinuousButton.Click += (_, _) => { viewModel.Navigation.SetMode(ViewMode.Continuous); UpdateViewStatus(); };
        SinglePageButton.Click += (_, _) => { viewModel.Navigation.SetMode(ViewMode.SinglePage); UpdateViewStatus(); };
        HideNotesButton.Click += (_, _) => SetNotesHidden(HideNotesButton.IsChecked == true);
        SearchButton.Click += (_, _) => OpenSearch();
    }

    private void ShowCurrentTab()
    {
        var tab = viewModel.Ribbon.Current;
        FileTabButton.IsChecked = tab == RibbonTab.File;
        HomeTabButton.IsChecked = tab == RibbonTab.Home;
        PageTabButton.IsChecked = tab == RibbonTab.Page;
        ViewTabButton.IsChecked = tab == RibbonTab.View;
        FileTab.IsVisible = tab == RibbonTab.File;
        HomeTab.IsVisible = tab == RibbonTab.Home;
        PageTab.IsVisible = tab == RibbonTab.Page;
        ViewTab.IsVisible = tab == RibbonTab.View;
        if (tab == RibbonTab.Page) UpdatePageCommands();
    }

    /// <summary>Paste needs copied pages that fit this document; delete needs at least one page left.</summary>
    private void UpdatePageCommands()
    {
        if (Document is null || PastePageButton is null) return;
        PastePageButton.IsEnabled = viewModel.Pages.CanPaste;
        DeletePageButton.IsEnabled = viewModel.Pages.CanDelete;
        ForeignEditButton.IsEnabled = viewModel.Foreign.CanConvert;
    }

    /// <summary>The insert menu opened last (tests).</summary>
    public ContextMenu? LastInsertMenu { get; private set; }

    /// <summary>The ▾ of 삽입 (spec 11.5): a blank page, or pages of another PDF (11.9).</summary>
    public ContextMenu OpenInsertMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.Items.Add(Menus.Item(Strings.Get("page.blankMenu"), () => AddBlankPage(true)));
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
            var item = Menus.Item(Path.GetFileName(path), () => _ = viewModel.OpenRecentAsync(path));
            ToolTip.SetTip(item, path);
            item.ContextMenu = new ContextMenu { Items = { Menus.Item(Strings.Get("recent.remove"), () => { viewModel.Recent.Remove(path); UpdateCommands(); }) } };
            menu.Items.Add(item);
        }
        menu.Open(RecentButton);
        return LastRecentMenu = menu;
    }
}
