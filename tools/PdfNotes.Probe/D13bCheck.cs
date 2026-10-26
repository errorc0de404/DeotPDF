using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.App;
using PdfNotes.App.Canvas;
using PdfNotes.App.Themes;
using PdfNotes.App.Tools;
using PdfNotes.App.Views;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Prototype;
using System.Runtime.InteropServices;

namespace PdfNotes.Probe;

/// <summary>
/// D13b user test list 1–5 (24.16) with OS mouse/keyboard input: text box fill/border memory typed into the text bar,
/// ribbon tabs clicked while a tool is active and shortcuts on every tab, page tab commands with their dialogs, view tab
/// zoom/fit/view mode/search and the simplified status bar, and a 720 DIP window where the wrapped buttons still work.
/// </summary>
internal static class D13bCheck
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static readonly List<object> checks = [];
    private static readonly List<string> messages = [];
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => (DocumentView)window.FindName("View");
    private static int page;

    public static int Run(string output, string part)
    {
        folder = output;
        Directory.CreateDirectory(folder);
        Environment.SetEnvironmentVariable("PDFNOTES_SETTINGS_DIR", Path.Combine(folder, "settings"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                await Open();
                await TextBoxMemory();
                await Tabs();
                await PageTab();
                await ViewTab();
                await Narrow();
                await Display();
                await ColorPicker();
                await ZoomMenu();
                await HighlightAuto();
                await AutoTabs();
                await PanelHandle();
                await ImportPages();
                result = 0;
            }
            catch (Exception e) { checks.Add(new { Name = "harness", Passed = false, Detail = e.ToString() }); Console.WriteLine(e); }
            finally
            {
                File.WriteAllText(Path.Combine(folder, part + "-result.json"), JsonSerializer.Serialize(new { Passed = result == 0, Input = "os-mouse-keyboard", Checks = checks, Messages = messages },
                    new JsonSerializerOptions { WriteIndented = true }));
                if (window is not null) { window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); }
                app.Shutdown();
            }
        };
        app.Run();
        return result;
    }

    private static MainWindow NewWindow()
    {
        var w = new MainWindow { Width = 1200, Height = 860, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        w.MessageSink = messages.Add;
        w.PromptOverride = (text, _) => { messages.Add("prompt: " + text); return MessageBoxResult.No; };
        w.SaveDialogOverride = name => Path.Combine(folder, "saved-" + Path.GetFileName(name));
        return w;
    }

    private static async Task Open()
    {
        window = NewWindow();
        window.Show(); await Settle(); await Foreground();
        string pdf = Path.Combine(folder, "source.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        Check("open sample PDF", await window.OpenFileAsync(pdf));
        await Settle(); await ShowPage(0);
        Check("start tab is Home", window.CurrentRibbonTab == RibbonTab.Home && ((FrameworkElement)window.FindName("HomeTab")).IsVisible);
    }

    // ----- 1. text box fill/border memory through the text bar palette (D13c 보완: target, swatches, + color code, 제거) -----

    private static async Task TextBoxMemory()
    {
        await ClickElement(Named("TextToolButton"));
        Check("text tool from the Home tab", window.CurrentTool == ToolKind.Text, window.CurrentTool.ToString());
        await ClickPage(new(90, 120));
        Check("a click with the text tool starts a new box", window.EditingText is not null);
        await TypeText("첫 상자");
        var textPanel = (Panel)window.FindName("TextStylePanel");
        T InBar<T>(string name) where T : FrameworkElement => Descendants(textPanel).OfType<T>().Single(e => e.Name == name);
        Button Swatch(RgbColor color) => Descendants(textPanel).OfType<Button>().First(b => b.Style == (Style)window.FindResource("Swatch") && Equals(b.DataContext, color));
        RgbColor cream = new(0xFF, 0xF2, 0xA8), red = new(0xE5, 0x39, 0x35);
        var alpha = InBar<TextBox>("TextAlphaBox");
        await ClickElement(InBar<RadioButton>("TextTargetFill"));
        Check("채우기 target clicked", window.TextColorTarget == TextColorTarget.Fill && alpha.IsEnabled);
        await ClickElement(InBar<ToggleButton>("TextCustomColorButton"));
        var codeBox = Descendants(Descendants(textPanel).OfType<Popup>().Single(p => p.PlacementTarget == InBar<ToggleButton>("TextCustomColorButton")).Child).OfType<TextBox>().Single();
        Check("+ opens the color code entry", codeBox.IsKeyboardFocused, codeBox.Text);
        await TypeText("#FFF2A8");
        await RawKey(0x0D);
        Check("a typed color code fills the box, opaque", window.EditingText?.BoxStyle is { BackgroundOpacity: 1 } fill && fill.BackgroundColor == cream, window.EditingText?.BoxStyle.ToString() ?? "no box");
        Check("the typed color becomes a user color", window.RecentColors.FirstOrDefault() == cream);
        await ClickElement(InBar<RadioButton>("TextTargetBorder"));
        await ClickElement(Swatch(red));
        await TypeField(alpha, "0");
        var expected = new TextBoxStyle(cream, 1, true, red, 1);
        Check("palette fill/border apply to the edited box", window.EditingText?.BoxStyle == expected, window.EditingText?.BoxStyle.ToString() ?? "no box");
        Snapshot("t1-first-box.png");
        await ClickPage(new(90, 330));
        if (window.EditingText is null) await ClickPage(new(90, 330)); // a click may only end the previous edit
        Check("the next new box starts with the same fill and border", window.EditingText?.BoxStyle == expected, window.EditingText?.BoxStyle.ToString() ?? "no box");
        await TypeText("둘째");

        // Right-click a user color → 제거 (app setting; boxes keep the color).
        await RightClick(Swatch(cream));
        var remove = (MenuItem)Swatch(cream).ContextMenu!.Items[0];
        Check("right-click opens 제거 on a user color", Swatch(cream).ContextMenu!.IsOpen && (string)remove.Header == "제거");
        await ClickScreen(remove);
        Check("제거 removes the user color from the palettes", !window.RecentColors.Contains(cream) &&
            !Descendants(textPanel).OfType<Button>().Any(b => Equals(b.DataContext, cream)), string.Join(",", window.RecentColors.Select(c => c.ToHex())));
        Check("the boxes keep that fill", window.EditingText?.BoxStyle == expected);
        await Key(0x1B);
        var boxes = Session.Current.Pages[0].Annotations.OfType<TextBoxAnnotation>().ToArray();
        Check("both boxes are kept with that style", boxes.Length == 2 && boxes.All(b => b.BoxStyle == expected), $"{boxes.Length} boxes");
        Snapshot("t1-two-boxes.png");

        // Restart: a new app window reads the saved setting.
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        window.Close(); await Settle();
        window = NewWindow();
        window.Show(); await Settle(); await Foreground();
        Check("after a restart a new box still starts with it", window.NewTextBoxStyle == expected, window.NewTextBoxStyle.ToString());
        Check("after a restart the removed color stays removed", !window.RecentColors.Contains(cream));
        Check("reopen the sample for the next tests", await window.OpenFileAsync(Path.Combine(folder, "source.pdf")));
        await Settle(); await ShowPage(0);
    }

    // ----- 2. tabs keep the tool; shortcuts work on every tab -----

    private static async Task Tabs()
    {
        await ClickElement(Named("PenToolButton"));
        Check("pen tool", window.CurrentTool == ToolKind.Pen);
        foreach (var tab in new[] { RibbonTab.File, RibbonTab.Page, RibbonTab.View, RibbonTab.Home })
        {
            var focusBefore = System.Windows.Input.Keyboard.FocusedElement;
            await ClickElement(Named(tab + "TabButton"));
            // Drawing on the page tab shows 필기 (spec 3.2 automatic tab switch, 2026-10-14); the other tabs stay.
            var after = tab == RibbonTab.Page ? RibbonTab.Home : tab;
            var content = (FrameworkElement)window.FindName(tab + "Tab");
            bool onlyThis = Enum.GetValues<RibbonTab>().All(t => ((FrameworkElement)window.FindName(t + "Tab")).IsVisible == (t == tab));
            Check($"{tab} tab shows its own content", window.CurrentRibbonTab == tab && content.IsVisible && onlyThis);
            Snapshot($"t2-tab-{tab.ToString().ToLowerInvariant()}.png");
            Check($"{tab} tab keeps the pen", window.CurrentTool == ToolKind.Pen && PenChecked(), window.CurrentTool.ToString());
            Check($"{tab} tab click does not take the keyboard focus", ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, focusBefore),
                $"{System.Windows.Input.Keyboard.FocusedElement?.GetType().Name}");

            int strokes = Strokes();
            await Drag(new(100, 200), new(140, 210), new(180, 230), new(220, 220));
            Check($"{tab}: drawing with the pen works", Strokes() == strokes + 1, $"{Strokes()}");
            Check($"{tab}: after drawing the tab is {after}", window.CurrentRibbonTab == after, window.CurrentRibbonTab.ToString());
            await Key(0x5A, control: true); // Ctrl+Z
            Check($"{tab}: Ctrl+Z undoes it", Strokes() == strokes);
            await Key(0x59, control: true); // Ctrl+Y
            Check($"{tab}: Ctrl+Y redoes it", Strokes() == strokes + 1);
            await Key(0x46, control: true); // Ctrl+F
            Check($"{tab}: Ctrl+F opens the search bar", window.IsSearchOpen);
            await Key(0x1B); // Esc in the search box
            if (window.IsSearchOpen) window.CloseSearch();
            await Settle();
            View.Focus();
            await Key(0x53, control: true); // Ctrl+S
            Check($"{tab}: Ctrl+S saves", !Session.IsDirty, window.Document.FilePath ?? "");
            Check($"{tab}: still the {after} tab and the pen", window.CurrentRibbonTab == after && window.CurrentTool == ToolKind.Pen);
        }
    }

    // ----- 3. page tab -----

    private static async Task PageTab()
    {
        await ClickElement(Named("PageTabButton"));
        int count = Session.Current.Pages.Count;
        await ShowPage(0);
        await ClickThumb(0);

        // 삽입 (spec 11.5, 2026-10-13): only insert after the current page; the dialog starts with the page's size and
        // background, and Enter creates the page. ▾ has 빈 페이지… and PDF에서 불러오기… (disabled until D13g).
        Check("page tab has no add-at-end button", !Descendants(Named("PageTab")).OfType<Button>().Any(b => (string)b.ToolTip == "빈 페이지 추가 (맨 뒤)"));
        var second = Session.Current.Pages[1].Id;
        _ = ClickElement(Named("InsertPageButton"));
        var insert = await Modal<NewNoteDialog>();
        Check("삽입 opens the blank page dialog with the page's size and background", insert.Title.Contains("삽입") && Field<CheckBox>(insert, "SameSizeBox").IsChecked == true &&
            Field<RadioButton>(insert, "BlankButton").IsChecked == true, insert.Title);
        await DialogKey(insert, 0x0D);
        Check("Enter inserts a blank page after the current one", Session.Current.Pages.Count == count + 1 && Session.Current.Pages[2].Id == second &&
            Session.Current.Pages[1].Annotations.Count == 0 && Session.Current.Pages[1].Setup.WidthPt == Session.Current.Pages[0].Setup.WidthPt, $"{Session.Current.Pages.Count}");
        await ClickThumb(0);
        await ClickElement(Named("InsertPageMenuButton"));
        var menu = window.LastInsertMenu!;
        var items = menu.Items.OfType<MenuItem>().ToArray();
        Check("▾ lists 빈 페이지… and PDF에서 불러오기… (D13g)", menu.IsOpen && items.Select(i => (string)i.Header).SequenceEqual(["빈 페이지…", "PDF에서 불러오기…"]) &&
            items[0].IsEnabled && items[1].IsEnabled);
        _ = ClickScreen(items[0]);
        var cancelled = await Modal<NewNoteDialog>();
        await ClickElement(Descendants(cancelled).OfType<Button>().Single(b => b.IsCancel));
        Check("cancel in the dialog adds nothing", Session.Current.Pages.Count == count + 1);

        // Copy → paste (paste disabled before a copy).
        System.Windows.Clipboard.Clear(); // another probe's window may have left pages there (pastable since D14g)
        window.ShowRibbonTab(RibbonTab.Home); window.ShowRibbonTab(RibbonTab.Page); // the page tab reads the clipboard when shown
        await ClickThumb(0);
        var paste = (Button)window.FindName("PastePageButton");
        Check("paste is disabled before a copy", !paste.IsEnabled);
        await ClickElement(PageButton("복사 (페이지+주석)"));
        Check("paste is enabled after a copy", paste.IsEnabled);
        int before = Session.Current.Pages.Count;
        await ClickElement(paste);
        Check("paste adds the copied page after the current one", Session.Current.Pages.Count == before + 1 &&
            Session.Current.Pages[1].Annotations.Count == Session.Current.Pages[0].Annotations.Count, $"{Session.Current.Pages.Count}");
        await ClickElement(PageButton("페이지만 복사"));
        await ClickElement(paste);
        Check("page-only copy pastes without annotations", Session.Current.Pages.Count == before + 2 && Session.Current.Pages[2].Annotations.Count == 0);

        string toolTrace = "";
        void Trace(string step) => toolTrace += $"{step}:{window.CurrentTool} ";
        Trace("start");
        // Delete asks first; No keeps the page, Yes deletes it.
        string? asked = null;
        window.PromptOverride = (text, _) => { asked = text; return MessageBoxResult.No; };
        before = Session.Current.Pages.Count;
        await ClickElement((Button)window.FindName("DeletePageButton"));
        Check("delete asks for confirmation; No keeps the page", asked is not null && Session.Current.Pages.Count == before, asked ?? "no prompt");
        window.PromptOverride = (text, _) => { asked = text; return MessageBoxResult.Yes; };
        await ClickElement((Button)window.FindName("DeletePageButton"));
        Check("Yes deletes the selected page", Session.Current.Pages.Count == before - 1);
        Trace("delete");
        window.PromptOverride = (text, _) => { messages.Add("prompt: " + text); return MessageBoxResult.No; };

        // Rotate.
        await ClickThumb(0);
        int rotation = Session.Current.Pages[0].Setup.Rotation;
        await ClickElement(PageButton("오른쪽으로 회전"));
        Check("rotate right", Session.Current.Pages[0].Setup.Rotation == (rotation + 90) % 360, $"{Session.Current.Pages[0].Setup.Rotation}");
        await ClickElement(PageButton("왼쪽으로 회전"));
        Check("rotate left", Session.Current.Pages[0].Setup.Rotation == rotation);
        Trace("rotate");

        // Shrink (apply) and resize (cancel).
        int undo = Session.UndoCount;
        var setup = Session.Current.Pages[0].Setup;
        _ = ClickElement((Button)window.FindName("ShrinkPageButton"));
        var shrink = await Modal<PageTransformDialog>();
        Snapshot("t3-shrink-dialog-owner.png");
        await ClickElement(Descendants(shrink).OfType<Button>().Single(b => b.IsDefault));
        await Settle();
        Trace("shrink");
        Check("shrink applies as one undo step", Session.UndoCount == undo + 1 && Session.Current.Pages[0].Setup != setup, $"undo +{Session.UndoCount - undo}");
        await Key(0x5A, control: true);
        Check("Ctrl+Z restores the page", Session.Current.Pages[0].Setup == setup);
        Trace("undo");
        undo = Session.UndoCount;
        var resizeButton = (Button)window.FindName("ResizePageButton");
        Check("resize is enabled after the Undo", resizeButton.IsEnabled, $"tab {window.CurrentRibbonTab}, pages selected {window.SelectedPageIds.Length}");
        _ = ClickElement(resizeButton);
        var resize = await Modal<PageTransformDialog>();
        await ClickElement(Descendants(resize).OfType<Button>().Single(b => b.IsCancel));
        Trace("resize");
        Check("resize dialog opens and cancel changes nothing", Session.UndoCount == undo && Session.Current.Pages[0].Setup == setup);

        // Page list toggle.
        var panel = (FrameworkElement)window.FindName("PagePanel");
        await ClickElement(Named("PagePanelButton"));
        Check("page list hides", !panel.IsVisible);
        Snapshot("t3-page-list-hidden.png");
        await ClickElement(Named("PagePanelButton"));
        Check("page list shows again", panel.IsVisible);
        Check("page tab keeps the pen", window.CurrentTool == ToolKind.Pen, toolTrace);
    }

    // ----- 4. view tab and status bar -----

    private static async Task ViewTab()
    {
        await ClickElement(Named("ViewTabButton"));
        await ShowPage(0);
        double zoom = View.Zoom;
        await ClickElement(ViewButton("확대"));
        Check("zoom in", Math.Abs(View.Zoom - zoom * 1.25) < 0.01, $"{zoom:0.###} → {View.Zoom:0.###}");
        await ClickElement(ViewButton("축소"));
        Check("zoom out", Math.Abs(View.Zoom - zoom) < 0.01, $"{View.Zoom:0.###}");
        var box = (ComboBox)window.FindName("ZoomBox");
        await ClickElement(box);
        await Key(0x41, control: true); // Ctrl+A
        await TypeText("150");
        await Key(0x0D);
        Check("typed zoom 150 applies", Math.Abs(View.Zoom - 1.5) < 0.01, $"{View.Zoom:0.###}, box '{box.Text}'");
        var zoomText = (TextBlock)window.FindName("ZoomText");
        Check("status bar shows the zoom percent", zoomText.Text == "150%", zoomText.Text);

        await ClickElement(ViewButton("너비 맞춤"));
        double width = View.Zoom;
        var t = View.PageTransform(View.CurrentPageIndex);
        Check("fit width: the page spans the view width", t.PageWidthDip > View.ViewportWidth * .85 && t.PageWidthDip <= View.ViewportWidth + 1,
            $"page {t.PageWidthDip:0} / view {View.ViewportWidth:0}");
        await ClickElement(ViewButton("페이지 맞춤"));
        t = View.PageTransform(View.CurrentPageIndex);
        Check("fit page: the whole page fits the view height", View.Zoom < width && t.PageHeightDip <= View.ViewportHeight + 1,
            $"zoom {View.Zoom:0.###}, page {t.PageHeightDip:0} / view {View.ViewportHeight:0}");
        Check("status bar follows fit", zoomText.Text == $"{Math.Round(View.Zoom * 100)}%", zoomText.Text);

        await ClickElement(Named("SinglePageButton"));
        Check("single page mode", View.Mode == ViewMode.SinglePage && ((ToggleButton)window.FindName("SinglePageButton")).IsChecked == true &&
            ((ToggleButton)window.FindName("ContinuousButton")).IsChecked != true);
        Snapshot("t4-single-page.png");
        await ClickElement(Named("ContinuousButton"));
        Check("continuous mode", View.Mode == ViewMode.Continuous && ((ToggleButton)window.FindName("ContinuousButton")).IsChecked == true);

        await ClickElement(ViewButton("검색 (Ctrl+F)"));
        Check("search button opens the search bar", window.IsSearchOpen);
        window.CloseSearch(); await Settle();

        // Status bar: previous/page/next, a short message and the zoom percent only.
        var bar = (DockPanel)VisualTreeHelper.GetParent(Named("ZoomButton"));
        // The zoom percent is a button since 2026-10-14 (spec 4.4 zoom menu, item 8).
        var buttons = Descendants(bar).OfType<ButtonBase>().Where(b => b.IsVisible && b.Name != "ZoomButton").ToArray();
        var combos = Descendants(bar).OfType<ComboBox>().Count();
        Check("status bar has only page navigation buttons", buttons.Length == 2 && combos == 0 &&
            buttons.All(b => (string)b.ToolTip is "이전 페이지" or "다음 페이지"), string.Join(", ", buttons.Select(b => b.ToolTip)));
        await ClickElement(buttons.Single(b => (string)b.ToolTip == "다음 페이지"));
        Check("status bar next page", View.CurrentPageIndex == 1 && ((TextBox)window.FindName("PageBox")).Text == "2", $"{View.CurrentPageIndex}");
        await ClickElement(buttons.Single(b => (string)b.ToolTip == "이전 페이지"));
        Check("status bar previous page", View.CurrentPageIndex == 0);
        Snapshot("t4-view-tab.png");
    }

    // ----- 5. narrow window: wraps, nothing clipped, wrapped buttons still work -----

    private static async Task Narrow()
    {
        window.Width = 720; await Settle(); await Settle();
        foreach (var tab in Enum.GetValues<RibbonTab>())
        {
            await ClickElement(Named(tab + "TabButton"));
            var panel = (WrapPanel)window.FindName(tab + "Tab");
            var items = panel.Children.OfType<FrameworkElement>().Where(c => c.IsVisible).ToList();
            int rows = items.Select(c => Math.Round(LayoutInformation.GetLayoutSlot(c).Top)).Distinct().Count();
            double right = items.Max(c => c.TranslatePoint(new Point(c.ActualWidth, 0), panel).X);
            Check($"720: {tab} tab not clipped", right <= panel.ActualWidth + .5, $"{rows} rows, right {right:0.#} / {panel.ActualWidth:0.#}");
            Snapshot($"t5-720-{tab.ToString().ToLowerInvariant()}.png");
        }
        var gear = Named("SettingsButton");
        double client = ((FrameworkElement)window.Content).ActualWidth;
        Check("720: settings gear visible inside the window", gear.TranslatePoint(new Point(gear.ActualWidth, 0), window).X <= client + .5);

        // The last (wrapped) buttons of the page and view tabs still respond to a real click.
        await ClickElement(Named("PageTabButton"));
        await ClickElement(Named("PagePanelButton"));
        Check("720: wrapped page list button works", !((FrameworkElement)window.FindName("PagePanel")).IsVisible);
        await ClickElement(Named("PagePanelButton"));
        await ClickElement(Named("ViewTabButton"));
        await ClickElement(ViewButton("검색 (Ctrl+F)"));
        Check("720: wrapped search button works", window.IsSearchOpen);
        window.CloseSearch(); await Settle();

        // Settings bars of the pen and the text tool wrap instead of clipping.
        await ClickElement(Named("HomeTabButton"));
        foreach (var tool in new[] { "PenToolButton", "TextToolButton", "EraserToolButton" })
        {
            await ClickElement(Named(tool));
            if (tool == "TextToolButton") await ClickPage(new(90, 480)); // the text bar is shown for a box being edited
            var bar = (Panel)new[] { "StyleBar", "EraserBorder", "TextStyleBorder" }.Select(n => (Border)window.FindName(n)).Single(b => b.IsVisible).Child;
            var visible = Descendants(bar).OfType<FrameworkElement>().Where(c => c.IsVisible && c is Control or TextBlock).ToArray();
            double barRight = visible.Max(c => c.TranslatePoint(new Point(c.ActualWidth, 0), bar).X);
            Check($"720: {tool} settings bar not clipped", barRight <= bar.ActualWidth + .5, $"right {barRight:0.#} / {bar.ActualWidth:0.#}");
            Snapshot($"t5-720-bar-{tool.Replace("ToolButton", "").ToLowerInvariant()}.png");
            if (tool == "TextToolButton") await Key(0x1B);
        }
        window.Width = 1200; await Settle();
    }

    // ----- 6. settings 화면 (D13d): choices show at once, 취소 restores them, 확인 stores them -----

    private static async Task Display()
    {
        Check("the app started in the Windows app theme", Theme.Choice == ThemeChoice.System && Theme.Mode == Theme.SystemTheme(), Theme.Mode.ToString());
        var start = Theme.Mode;
        var (other, otherChoice) = start == AppTheme.Dark ? ("ThemeLight", ThemeChoice.Light) : ("ThemeDark", ThemeChoice.Dark);
        var otherMode = start == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        async Task<SettingsDialog> OpenDisplay()
        {
            _ = ClickElement(Named("SettingsButton"));
            var dialog = await Modal<SettingsDialog>();
            await ClickElement((FrameworkElement)Field<ListBox>(dialog, "CategoryList").ItemContainerGenerator.ContainerFromIndex(2)); // 화면 (호환 is gone, D14g)
            return dialog;
        }

        var dialog = await OpenDisplay();
        await ClickElement(Field<RadioButton>(dialog, other));
        Check("a theme click shows at once (window and title bar)", Theme.Mode == otherMode && Theme.IsTitleBarDark(window) != (otherMode == AppTheme.Light),
            $"{Theme.Mode}, title bar dark {Theme.IsTitleBarDark(window)}");
        await ClickElement(Field<CheckBox>(dialog, "PageNumbersBox"));
        Check("page numbers off at once", !window.PageNumbersShown);
        Snapshot("t6-preview.png");
        await ClickElement(Descendants(dialog).OfType<Button>().Single(b => b.IsCancel));
        await Settle();
        Check("취소 restores the theme and the page numbers", Theme.Mode == start && window.PageNumbersShown && window.DisplaySettings == DisplayValues.Default);

        dialog = await OpenDisplay();
        await ClickElement(Field<RadioButton>(dialog, other));
        await ClickElement(Field<RadioButton>(dialog, "AccentOrange"));
        await ClickElement(Field<CheckBox>(dialog, "PageNumbersBox"));
        await ClickElement(Field<Button>(dialog, "OkButton"));
        await Settle();
        Check("확인 stores them", window.DisplaySettings == new DisplayValues(otherChoice, AccentColor.Orange, false) && Theme.Mode == otherMode &&
            Theme.Accent == AccentColor.Orange && !window.PageNumbersShown, window.DisplaySettings.ToString());
        Snapshot("t6-stored.png");
        window.SetDisplay(DisplayValues.Default);
        await Settle();
    }

    // ----- 7. + picker: color plane, hue strip, eyedropper (2026-10-13 user request) -----

    private static async Task ColorPicker()
    {
        await ClickElement(Named("HomeTabButton"));
        await ClickElement(Named("PenToolButton"));
        await ShowPage(0);
        var bar = (Panel)((Border)window.FindName("StyleBar")).Child;
        var plus = Descendants(bar).OfType<ToggleButton>().Single(b => b.Name == "CustomColorButton");
        var picker = window.CustomColorPickers["CustomColor"];
        await ClickElement(plus);
        Check("+ opens the color plane", picker.IsVisible && picker.Code.IsKeyboardFocused);
        // Hue strip at 2/3 (blue), then the plane's top-right corner (full saturation and brightness).
        await ClickScreen(picker.Strip, new Point(ColorPickerPanel.StripWidth / 2, ColorPickerPanel.PlaneHeight * 2 / 3));
        await ClickScreen(picker.Plane, new Point(ColorPickerPanel.PlaneWidth - 2, 2));
        var chosen = picker.SelectedColor;
        Check("strip and plane clicks choose blue", chosen.B > 240 && chosen.R < 12 && chosen.G < 12, chosen.ToHex());
        Snapshot("t7-picker.png");
        await ClickScreen(picker.Add);
        Check("추가 applies it to the pen", window.PenStyle.Color == chosen && !picker.IsVisible && window.RecentColors.First() == chosen, window.PenStyle.Color.ToHex());

        // Eyedropper: Esc cancels; a click on a filled rectangle takes its fill color, nothing else happens.
        RgbColor green = new(0x2E, 0x8B, 0x57);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, new ShapeAnnotation(Guid.NewGuid(), false, Affine.Identity,
            ShapeKind.Rectangle, new(340, 160, 120, 80), [], ShapeStyle.Default with { FillColor = green, FillOpacity = 1 })));
        await Settle();
        await ClickElement(plus);
        await ClickScreen(picker.Eyedropper);
        Check("스포이드 waits for a click", window.IsEyedropperActive && !picker.IsVisible);
        await Key(0x1B);
        Check("Esc cancels the eyedropper", !window.IsEyedropperActive && window.PenStyle.Color == chosen);
        await ClickElement(plus);
        await ClickScreen(picker.Eyedropper);
        int strokes = Strokes();
        await ClickPage(new(400, 200));
        Check("the eyedropper takes the clicked color", window.PenStyle.Color == green && !window.IsEyedropperActive, window.PenStyle.Color.ToHex());
        Check("the eyedropper click draws nothing", Strokes() == strokes && window.CurrentTool == ToolKind.Pen);
    }

    // ----- 8. status bar zoom menu (spec 4.4, 2026-10-14) -----

    private static async Task ZoomMenu()
    {
        await ShowPage(0);
        int undo = Session.UndoCount;
        var zoomText = (TextBlock)window.FindName("ZoomText");
        async Task<MenuItem[]> Open()
        {
            await ClickElement(Named("ZoomButton"));
            var menu = window.LastZoomMenu;
            Check("a click on the zoom percent opens the menu", menu is { IsOpen: true });
            return menu!.Items.OfType<MenuItem>().ToArray();
        }
        var items = await Open();
        Check("menu: the zoom steps 25–800% (D14g) then the two fits", items.Select(i => (string)i.Header).SequenceEqual(["25%", "50%", "75%", "100%", "125%", "150%", "200%", "300%", "400%", "800%", "너비 맞춤", "페이지 맞춤"]) &&
            window.LastZoomMenu!.Items.OfType<Separator>().Count() == 1, string.Join(" ", items.Select(i => i.Header)));
        Check("the current zoom (100%) is checked", items.Where(i => i.IsChecked).Select(i => (string)i.Header).SequenceEqual(["100%"]));
        Snapshot("t8-zoom-menu.png");
        await ClickScreen(items[5]);
        Check("150% chosen", Math.Abs(View.Zoom - 1.5) < 1e-6 && zoomText.Text == "150%", zoomText.Text);
        items = await Open();
        Check("now 150% is checked", items.Where(i => i.IsChecked).Select(i => (string)i.Header).SequenceEqual(["150%"]));
        await ClickScreen(items[10]);
        var t = View.PageTransform(View.CurrentPageIndex);
        Check("너비 맞춤 from the menu", t.PageWidthDip > View.ViewportWidth * .85 && t.PageWidthDip <= View.ViewportWidth + 1 && zoomText.Text == $"{Math.Round(View.Zoom * 100)}%",
            $"page {t.PageWidthDip:0} / view {View.ViewportWidth:0}, {zoomText.Text}");
        items = await Open();
        await ClickScreen(items[11]);
        t = View.PageTransform(View.CurrentPageIndex);
        Check("페이지 맞춤 from the menu", t.PageHeightDip <= View.ViewportHeight + 1, $"page {t.PageHeightDip:0} / view {View.ViewportHeight:0}");
        Check("zooming is not a document change", Session.UndoCount == undo);
        await ShowPage(0);
    }

    // ----- 9. glyph highlight 자동 beside pt; on disables the width (spec 9.2, 2026-10-14) -----

    private static async Task HighlightAuto()
    {
        await ClickElement(Named("HomeTabButton"));
        await ClickElement(Named("HighlighterToolButton")); // one highlighter since D14g
        var auto = (ToggleButton)Named("HighlightAutoButton");
        var width = (TextBox)Named("WidthBox");
        var unit = Descendants((Panel)width.Parent).OfType<TextBlock>().Single(b => b.Text == "pt");
        Check("자동 sits right after pt in the width group", ReferenceEquals(auto.Parent, width.Parent) &&
            auto.TranslatePoint(new Point(0, 0), window).X > unit.TranslatePoint(new Point(unit.ActualWidth, 0), window).X - .5 &&
            Math.Abs(auto.TranslatePoint(new Point(0, auto.ActualHeight / 2), window).Y - width.TranslatePoint(new Point(0, width.ActualHeight / 2), window).Y) < 2);
        if (auto.IsChecked != true) await ClickElement(auto);
        Check("자동 on: the width stays on for free strokes (spec 9.1, D14g)", auto.IsChecked == true && width.IsEnabled && ((Slider)Named("WidthSlider")).IsEnabled);
        Snapshot("t9-highlight-auto.png");
        await ClickElement(auto);
        Check("자동 off: width enabled", auto.IsChecked == false && width.IsEnabled && !window.TextHighlightToolStyle.Auto);
        await ClickElement(auto);
        Check("자동 on again", auto.IsChecked == true && window.TextHighlightToolStyle.Auto);

        // A press away from text draws a free highlight stroke and shows no notice (spec 9.2, D14g).
        ((TextBlock)Named("StatusText")).Text = "";
        int count = Session.Current.Pages[0].Annotations.Count;
        await ShowPage(0);
        await Drag(new(20, 20), new(60, 22));
        Check("no glyphs under the press: a free highlight stroke, no notice", Session.Current.Pages[0].Annotations.Count == count + 1 &&
            Session.Current.Pages[0].Annotations[^1] is FreeHighlightStroke && ((TextBlock)Named("StatusText")).Text == "", ((TextBlock)Named("StatusText")).Text);
        await Key(0x5A, control: true);
    }

    // ----- 10. tab 필기, automatic 필기 ↔ 페이지 switch (spec 3.2, 2026-10-14) -----

    private static async Task AutoTabs()
    {
        Check("the tab is named 필기", (string)((ContentControl)Named("HomeTabButton")).Content == "필기");
        await ClickElement(Named("PenToolButton"));
        await ClickThumb(1);
        Check("a plain thumbnail click keeps 필기 (spec 3.3, D14g)", window.CurrentRibbonTab == RibbonTab.Home && window.CurrentTool == ToolKind.Pen);
        window.ShowRibbonTab(RibbonTab.Page);
        await ShowPage(0);
        int strokes = Strokes();
        await Drag(new(100, 420), new(160, 430), new(220, 420));
        Check("drawing on the canvas shows 필기", Strokes() == strokes + 1 && window.CurrentRibbonTab == RibbonTab.Home);
        var list = (ListBox)window.FindName("PageList");
        await RightClick((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0));
        Check("a right-click on the page list shows 페이지", window.CurrentRibbonTab == RibbonTab.Page && list.ContextMenu?.IsOpen == true,
            $"{window.CurrentRibbonTab}, menu {list.ContextMenu?.IsOpen}");
        await RawKey(0x1B);
        if (list.ContextMenu?.IsOpen == true) list.ContextMenu.IsOpen = false;

        // Scrolling the canvas keeps 페이지; selecting an annotation shows 필기.
        window.SetTool(ToolKind.Select); // (a tool change shows 필기)
        Check("a tool change shows 필기", window.CurrentRibbonTab == RibbonTab.Home);
        window.ShowRibbonTab(RibbonTab.Page);
        Check("페이지 again", window.CurrentRibbonTab == RibbonTab.Page);
        await EnsureForeground();
        Cursor(Dip(new(300, 300)));
        mouse_event(0x800, 0, 0, -120, 0); await Settle(); await Settle();
        Check("wheel scrolling keeps 페이지", window.CurrentRibbonTab == RibbonTab.Page);
        await ShowPage(0);
        var drawn = Session.Current.Pages[0].Annotations.OfType<InkStroke>().Last();
        await ClickPage(drawn.Points[drawn.Points.Length / 2]);
        Check("clicking an annotation selects it and shows 필기", window.Selection.SelectedIds.Count == 1 && window.CurrentRibbonTab == RibbonTab.Home && window.CurrentTool == ToolKind.Select,
            $"{window.Selection.SelectedIds.Count} selected, {window.CurrentRibbonTab}");
        await Key(0x1B);

        // 파일 / 보기 stay; the setting turns it off.
        await ClickElement(Named("ViewTabButton"));
        await ClickThumb(1);
        Check("보기 stays on a thumbnail click", window.CurrentRibbonTab == RibbonTab.View);
        await ClickElement(Named("HomeTabButton"));
        window.SetDisplay(window.DisplaySettings with { AutoSwitchTabs = false });
        await ClickThumb(0);
        Check("탭 자동 전환 off: 필기 stays", window.CurrentRibbonTab == RibbonTab.Home);
        window.SetDisplay(window.DisplaySettings with { AutoSwitchTabs = true });

        // The settings window: 화면 › 탭 자동 전환 (on by default), stored with 확인.
        _ = ClickElement(Named("SettingsButton"));
        var dialog = await Modal<SettingsDialog>();
        await ClickElement((FrameworkElement)Field<ListBox>(dialog, "CategoryList").ItemContainerGenerator.ContainerFromIndex(2)); // 화면 (호환 is gone, D14g)
        var box = Field<CheckBox>(dialog, "AutoTabsBox");
        Check("화면 has 탭 자동 전환, on", box.IsChecked == true && (string)box.Content == "탭 자동 전환");
        await ClickElement(box);
        await ClickElement(Field<Button>(dialog, "OkButton"));
        await Settle();
        Check("확인 stores 탭 자동 전환 off", !window.DisplaySettings.AutoSwitchTabs);
        window.SetDisplay(window.DisplaySettings with { AutoSwitchTabs = true });
    }

    // ----- 11. page list handle (2026-10-14 user request) -----

    private static async Task PanelHandle()
    {
        var panel = Named("PagePanel");
        var handle = Named("PagePanelHandle");
        Check("handle sits on the list's right edge", panel.IsVisible && Math.Abs(handle.TranslatePoint(new Point(0, 0), window).X - panel.TranslatePoint(new Point(panel.ActualWidth, 0), window).X) < 2);
        await ClickElement(handle);
        Check("a click on the handle hides the list; the handle stays at the left edge", !panel.IsVisible && handle.TranslatePoint(new Point(0, 0), window).X < 2 &&
            ((ToggleButton)Named("PagePanelButton")).IsChecked == false && (string)handle.Tag == "Closed");
        Snapshot("t11-handle-closed.png");
        await ClickElement(handle);
        Check("a second click shows it again", panel.IsVisible && window.PagePanelVisible && ((ToggleButton)Named("PagePanelButton")).IsChecked == true);
    }

    // ----- 12. 삽입 ▾ › PDF에서 불러오기… with real clicks and keys (D13g; the file dialog is replaced) -----

    private static async Task ImportPages()
    {
        string other = Path.Combine(folder, "import-source.pdf");
        ProbeSamplePdf.Create(other, 4);
        window.ImportFileDialogOverride = () => other;
        await ClickElement(Named("PageTabButton"));
        await ClickThumb(0);
        int count = Session.Current.Pages.Count;
        var second = Session.Current.Pages[1].Id;
        int undo = Session.UndoCount;
        await ClickElement(Named("InsertPageMenuButton"));
        var import = window.LastInsertMenu!.Items.OfType<MenuItem>().Single(i => (string)i.Header == "PDF에서 불러오기…");
        _ = ClickScreen(import);
        var dialog = await Modal<PdfImportDialog>();
        await Task.Delay(800); // thumbnails render
        Check("the range dialog opens with 전체", dialog.RangeBox.Text == "전체", dialog.RangeBox.Text);
        var thumbs = dialog.PageList;
        await ClickElement((FrameworkElement)thumbs.ItemContainerGenerator.ContainerFromIndex(1));
        keybd_event(0x10, 0, 0, 0);
        await ClickElement((FrameworkElement)thumbs.ItemContainerGenerator.ContainerFromIndex(2));
        keybd_event(0x10, 0, 2, 0);
        await Settle();
        Check("thumbnail click and Shift+click write 2-3", dialog.RangeBox.Text.Replace(" ", "") == "2-3", dialog.RangeBox.Text);
        Snapshot("t12-import-owner.png");
        await ClickElement(dialog.RangeBox);
        keybd_event(0x11, 0, 0, 0); keybd_event(0x41, 0, 0, 0); keybd_event(0x41, 0, 2, 0); keybd_event(0x11, 0, 2, 0); await Settle();
        foreach (char c in "9")
            SendInput(2, [new Input { Type = 1, Scan = c, Flags = 4 }, new Input { Type = 1, Scan = c, Flags = 6 }], Marshal.SizeOf<Input>());
        await Settle(); await Settle();
        Check("a page past the end blocks 삽입 with a message", !dialog.CanInsert && dialog.ErrorText.Length > 0, dialog.ErrorText);
        keybd_event(0x08, 0, 0, 0); keybd_event(0x08, 0, 2, 0);
        foreach (char c in "1, 3")
            SendInput(2, [new Input { Type = 1, Scan = c, Flags = 4 }, new Input { Type = 1, Scan = c, Flags = 6 }], Marshal.SizeOf<Input>());
        await Settle(); await Settle();
        Check("typed 1, 3 selects those thumbnails", dialog.CanInsert && thumbs.SelectedItems.Count == 2, dialog.RangeBox.Text);
        await DialogKey(dialog, 0x0D);
        for (int i = 0; i < 30 && Session.Current.Pages.Count == count; i++) await Task.Delay(200);
        Check("Enter inserts the two pages after the current one", Session.Current.Pages.Count == count + 2 && Session.Current.Pages[3].Id == second &&
            Session.UndoCount == undo + 1, $"{Session.Current.Pages.Count} pages");
        await Key(0x5A, control: true);
        Check("Ctrl+Z removes both", Session.Current.Pages.Count == count && Session.Current.Pages[1].Id == second);
    }

    /// <summary>A real click at a point of an element in a popup.</summary>
    private static async Task ClickScreen(FrameworkElement element, Point at)
    {
        await Task.Delay(300);
        var p = element.PointToScreen(at);
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y)); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }

    // ----- helpers -----

    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        if (!passed) throw new InvalidOperationException(name + ": " + detail);
    }

    private static FrameworkElement Named(string name) => (FrameworkElement)window.FindName(name);
    private static bool PenChecked() => ((ToggleButton)window.FindName("PenToolButton")).IsChecked == true;
    private static int Strokes() => Session.Current.Pages[0].Annotations.OfType<InkStroke>().Count();
    private static Button PageButton(string tip) => Descendants(Named("PageTab")).OfType<Button>().Single(b => (string)b.ToolTip == tip);
    private static Button ViewButton(string tip) => Descendants(Named("ViewTab")).OfType<Button>().Single(b => (string)b.ToolTip == tip);

    /// <summary>A real click on a page thumbnail in the page panel (selects that page only).</summary>
    private static async Task ClickThumb(int index)
    {
        var list = (ListBox)window.FindName("PageList");
        list.ScrollIntoView(list.Items[index]); await Settle();
        await ClickElement((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(index));
        Check($"thumbnail {index + 1} selected", window.SelectedPageIds.SequenceEqual([Session.Current.Pages[index].Id]), $"{window.SelectedPageIds.Length} selected");
    }

    private static async Task TypeField(TextBox field, string text)
    {
        await ClickElement(field);
        await Key(0x41, control: true);
        await TypeText(text);
        await Key(0x0D);
    }

    private static async Task<T> Modal<T>() where T : Window
    {
        for (int i = 0; i < 40; i++) { if (Application.Current.Windows.OfType<T>().FirstOrDefault(w => w.IsVisible) is { } modal) { await Task.Delay(300); return modal; } await Task.Delay(100); }
        throw new InvalidOperationException("Modal did not open: " + typeof(T).Name);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var c = VisualTreeHelper.GetChild(parent, i); yield return c; foreach (var d in Descendants(c)) yield return d; }
    }

    private static T Field<T>(Window dialog, string name) where T : FrameworkElement => Descendants(dialog).OfType<T>().Single(c => c.Name == name);

    private static async Task ClickElement(FrameworkElement element)
    {
        await Task.Delay(400);
        var owner = Window.GetWindow(element);
        if (owner == window) await EnsureForeground();
        if (!element.IsVisible || element.ActualWidth == 0) throw new InvalidOperationException($"Element is not visible: {element.Name} {element}");
        var p = element.PointToScreen(new(element.ActualWidth / 2, element.ActualHeight / 2));
        var topLeft = owner.PointToScreen(new(0, 0)); var bottomRight = owner.PointToScreen(new(owner.ActualWidth, owner.ActualHeight));
        if (p.X < topLeft.X || p.Y < topLeft.Y || p.X > bottomRight.X || p.Y > bottomRight.Y) throw new InvalidOperationException($"Click point {p} is outside its window.");
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y)); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }

    /// <summary>A real click on an element of a popup (context menu item), which has no owner window.</summary>
    private static async Task ClickScreen(FrameworkElement element)
    {
        await Task.Delay(400);
        if (!element.IsVisible || element.ActualWidth == 0) throw new InvalidOperationException($"Element is not visible: {element}");
        var p = element.PointToScreen(new(element.ActualWidth / 2, element.ActualHeight / 2));
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y)); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(30); mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }

    private static async Task RightClick(FrameworkElement element)
    {
        await Task.Delay(400);
        await EnsureForeground();
        var p = element.PointToScreen(new(element.ActualWidth / 2, element.ActualHeight / 2));
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y)); await Task.Delay(60);
        mouse_event(8, 0, 0, 0, 0); await Task.Delay(30); mouse_event(0x10, 0, 0, 0, 0);
        await Settle(); await Settle();
    }

    /// <summary>A key to whatever has the keyboard focus now (a popup field), without moving focus first.</summary>
    private static async Task RawKey(byte key)
    {
        keybd_event(key, 0, 0, 0); await Task.Delay(30); keybd_event(key, 0, 2, 0);
        await Settle(); await Settle();
    }

    /// <summary>A key to a modal dialog (the main window is disabled while it is open).</summary>
    private static async Task DialogKey(Window dialog, byte key)
    {
        var handle = new WindowInteropHelper(dialog).Handle;
        for (int attempt = 0; attempt < 5 && GetForegroundWindow() != handle; attempt++) { dialog.Activate(); SetForegroundWindow(handle); await Task.Delay(200); }
        Check("dialog has the foreground", GetForegroundWindow() == handle);
        await RawKey(key);
    }

    private static async Task ShowPage(int index)
    {
        page = index;
        View.SetZoom(1); await Settle();
        View.GoToPage(index); await Settle();
    }

    private static Point Dip(Point2 pagePoint)
    {
        var p = View.PageTransform(page).PageToDip.Transform(pagePoint);
        return new(p.X, p.Y);
    }

    private static async Task Drag(params Point2[] points)
    {
        await EnsureForeground();
        var dips = points.Select(Dip).ToArray();
        Cursor(dips[0]); await Task.Delay(60);
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(50);
        foreach (var p in dips.Skip(1)) { Cursor(p); await Task.Delay(25); }
        await Task.Delay(40);
        mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }

    private static async Task ClickPage(Point2 pagePoint)
    {
        await Task.Delay(600);
        await Drag(pagePoint, new(pagePoint.X + .5, pagePoint.Y));
        await Settle();
    }

    /// <summary>Unicode key events into the focused control (not an IME composition test).</summary>
    private static async Task TypeText(string text)
    {
        await EnsureForeground();
        foreach (char c in text)
        {
            var down = new Input { Type = 1, Scan = c, Flags = 4 };
            var up = new Input { Type = 1, Scan = c, Flags = 4 | 2 };
            SendInput(2, [down, up], Marshal.SizeOf<Input>());
            await Task.Delay(40);
        }
        await Settle();
    }

    private static async Task Key(byte key, bool control = false)
    {
        await EnsureForeground();
        if (!window.IsKeyboardFocusWithin) View.Focus();
        if (control) keybd_event(0x11, 0, 0, 0);
        keybd_event(key, 0, 0, 0); await Task.Delay(30); keybd_event(key, 0, 2, 0);
        if (control) keybd_event(0x11, 0, 2, 0);
        await Settle(); await Settle();
    }

    private static async Task Foreground()
    {
        var handle = new WindowInteropHelper(window).Handle;
        SetForegroundWindow(handle); window.Activate(); await Settle();
        for (int attempt = 0; attempt < 5 && GetForegroundWindow() != handle; attempt++)
        { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); window.Activate(); SetForegroundWindow(handle); await Task.Delay(200); }
        if (GetForegroundWindow() != handle) { window.Topmost = true; window.Activate(); SetForegroundWindow(handle); await Settle(); window.Topmost = false; }
        Check("foreground", GetForegroundWindow() == handle, $"target={handle}, actual={GetForegroundWindow()}");
    }

    private static int regained;

    private static async Task EnsureForeground()
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (GetForegroundWindow() == handle) return;
        regained++;
        messages.Add($"foreground regained before input #{regained} (was {GetForegroundWindow()})");
        for (int attempt = 0; attempt < 5 && GetForegroundWindow() != handle; attempt++)
        { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); window.Activate(); SetForegroundWindow(handle); await Task.Delay(200); }
        if (GetForegroundWindow() != handle) throw new InvalidOperationException("The app window could not get the foreground for real input.");
        await Settle();
    }

    private static void Cursor(Point dip)
    {
        if (dip.X < 0 || dip.Y < 0 || dip.X > View.ActualWidth || dip.Y > View.ActualHeight)
            throw new InvalidOperationException($"Input point {dip} is outside the document view.");
        var p = View.PointToScreen(dip);
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    private static Task Settle() => Task.Delay(200);

    private static void Snapshot(string name)
    {
        double dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Round(window.ActualWidth * dpi), (int)Math.Round(window.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Vk;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nint Extra;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
