using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
using DashStyle = PdfNotes.Core.Models.DashStyle;

namespace PdfNotes.Probe;

/// <summary>
/// D13 off-screen renders of the app frame (no OS input, windows never activated and placed outside the screen).
/// <c>d13-render</c> writes PNGs of the main window in several tool states, the dialogs and a standalone context menu,
/// so the frame can be compared before/after a design change.
/// </summary>
internal static class D13Check
{
    private static MainWindow window = null!;
    private static string folder = "";
    private static readonly List<string> written = [];
    private static readonly List<object> checks = [];
    private static DocumentSession Session => window.Document.Session;
    private static DocumentView View => (DocumentView)window.FindName("View");

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
                // Renders do not depend on this PC's Windows theme (the default setting follows Windows, D13d).
                Theme.SystemThemeOverride = () => AppTheme.Light;
                if (part == "d13-textbox") await TextBoxMemory();
                else if (part == "d13-display") await Display();
                else if (part == "d13-import") await Import();
                else if (part == "d13-followup") await Followup();
                else await Render();
                result = 0;
            }
            catch (Exception e) { Console.WriteLine(e); File.WriteAllText(Path.Combine(folder, part + "-error.txt"), e.ToString()); }
            finally
            {
                File.WriteAllText(Path.Combine(folder, part + "-result.json"), JsonSerializer.Serialize(new
                { Passed = result == 0, Input = "none (windows not activated, off screen)", Checks = checks, Files = written }, new JsonSerializerOptions { WriteIndented = true }));
                if (window is not null) { window.PromptOverride = (_, _) => MessageBoxResult.No; window.Close(); }
                app.Shutdown();
            }
        };
        app.Run();
        return result;
    }

    private static T OffScreen<T>(T w, double width, double height) where T : Window
    {
        w.ShowActivated = false; w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = SystemParameters.VirtualScreenLeft - 3000 - width; w.Top = SystemParameters.VirtualScreenTop;
        if (!double.IsNaN(width)) w.Width = width;
        if (!double.IsNaN(height)) w.Height = height;
        return w;
    }

    private static async Task Render()
    {
        string pdf = Path.Combine(folder, "sample.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        window = OffScreen(new MainWindow(), 1200, 860);
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        window.MessageSink = _ => { };
        window.Show();
        await Settle(300);
        if (!await window.OpenFileAsync(pdf)) throw new InvalidOperationException("sample did not open");
        await Settle(600);
        var pageId = Session.Current.Pages[0].Id;
        var shape = new ShapeAnnotation(Guid.NewGuid(), false, Affine.Identity, ShapeKind.Rectangle, new(330, 120, 140, 80), [], ShapeStyle.Default);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, pageId, new InkStroke(Guid.NewGuid(), false, Affine.Identity,
            [.. Enumerable.Range(0, 30).Select(i => new Point2(80 + i * 6, 260 + 18 * Math.Sin(i / 4.0)))], 3, new(0xD0, 0x20, 0x20), 1)));
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, pageId, new LineAnnotation(Guid.NewGuid(), false, Affine.Identity, LineKind.Straight,
            new(80, 330), new(300, 380), [], 2, DashStyle.Solid, false, true, new(0x20, 0x40, 0xD0), 1)));
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, pageId, shape));

        window.SetTool(ToolKind.Select);
        window.Selection.Select(shape.Id);
        await Snapshot("main-select-shape.png");
        var shapeOptions = (FrameworkElement)window.FindName("ShapeOptionsPanel");
        var firstSwatch = (FrameworkElement)((Panel)window.FindName("PalettePanel")).Children[0];
        Check("shape bar: fill/border target and border style come before the palette (like the text bar)", shapeOptions.IsVisible &&
            shapeOptions.TranslatePoint(new Point(shapeOptions.ActualWidth, 0), window).X <= firstSwatch.TranslatePoint(new Point(0, 0), window).X);

        window.Selection.Select(null);
        window.SetTool(ToolKind.Pen);
        await Snapshot("main-pen.png");

        window.SetTool(ToolKind.AreaEraser);
        await Snapshot("main-eraser.png");

        window.SetTool(ToolKind.Text);
        window.BeginNewText(0, new Point2(90, 420));
        await Snapshot("main-text.png");
        window.FinishTextEditing();
        await Settle();

        window.SetTool(ToolKind.Pen);
        window.OpenSearch();
        ((TextBox)window.FindName("SearchBox")).Text = "PDF";
        await Settle(800);
        await Snapshot("main-search.png");
        window.CloseSearch();

        window.Width = 820;
        await Snapshot("main-narrow.png");
        window.Width = 1200;

        // Page list handle (2026-10-14 user request): on the list's right edge; hidden list → only the handle at the window's left edge.
        var handle = (Button)window.FindName("PagePanelHandle");
        var panel = (FrameworkElement)window.FindName("PagePanel");
        var tabToggle = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("PagePanelButton");
        double HandleLeft() => handle.TranslatePoint(new Point(0, 0), window).X;
        double panelRight = panel.TranslatePoint(new Point(panel.ActualWidth, 0), window).X;
        Check("handle sits on the page list's right edge", handle.IsVisible && Math.Abs(HandleLeft() - panelRight) <= 1, $"{HandleLeft():0.#} / {panelRight:0.#}");
        handle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Snapshot("main-panel-hidden.png");
        var chrome = (Border)handle.Template.FindName("Chrome", handle);
        Check("a hidden list leaves the handle in the accent color", ColorOf(chrome.Background) == Theme.Color("Accent"), ColorOf(chrome.Background).ToString());
        Theme.Apply(AppTheme.Dark, AccentColor.Blue);
        await Snapshot("dark-panel-hidden.png");
        Check("dark theme: the handle is the dark accent", ColorOf(chrome.Background) == Theme.Color("Accent"));
        Theme.Apply(AppTheme.Light, AccentColor.Blue);
        Check("handle hides the list and stays at the window's left edge", !window.PagePanelVisible && handle.IsVisible && HandleLeft() <= 1 && tabToggle.IsChecked == false &&
            PdfNotes.Editor.Services.AppSettings.Load().PagePanelVisible == false, $"{HandleLeft():0.#}");
        handle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Settle(300);
        Check("handle shows the list again", window.PagePanelVisible && tabToggle.IsChecked == true && Math.Abs(HandleLeft() - panelRight) <= 1);
        Check("an open list leaves the handle quiet (panel color)", ColorOf(chrome.Background) == Theme.Color("Panel.Background"), ColorOf(chrome.Background).ToString());

        await Ribbon();
        await Polish(shape);

        var settings = OffScreen(new SettingsDialog(new(true, false, new(true, 5, PdfNotes.Editor.Services.BackupMode.Folder, @"C:\Backups", 10)), () => true), double.NaN, double.NaN);
        await SnapshotWindow(settings, "dialog-settings.png");
        var setup = Session.Current.Pages[0].Setup;
        var transform = OffScreen(new PageTransformDialog(true, setup, _ => [setup]), 930, 640);
        await SnapshotWindow(transform, "dialog-page-resize.png");
        var note = OffScreen(new NewNoteDialog(), double.NaN, double.NaN);
        await SnapshotWindow(note, "dialog-new-note.png");

        SnapshotElement(Menu(), "menu.png", 220);
        SnapshotElement(new ToolTip { Content = "저장 (Ctrl+S)" }, "tooltip.png", double.NaN);

        // Runtime swap (D13a): the same window follows Theme.Apply without being rebuilt.
        int changed = 0;
        Theme.Changed += (_, _) => changed++;
        Theme.Apply(AppTheme.Dark, AccentColor.Blue);
        window.SetTool(ToolKind.Select);
        window.Selection.Select(shape.Id);
        await Snapshot("dark-select-shape.png");
        Check("window background follows the dark palette", ColorOf(window.Background) == Theme.Color("Window.Background") && ColorOf(window.Background) != LightWindow,
            ColorOf(window.Background).ToString());
        Check("document background (outside the pages) follows the dark palette", ColorOf(View.Background) == Theme.Color("Canvas.Background"), ColorOf(View.Background).ToString());
        var pageVisual = View.RealizedPages.First();
        Check("page shadow/edge follow the theme, paper stays white", ColorOf(pageVisual.Edge!) == Theme.Color("Page.Border") && Theme.Color("Paper") == Colors.White,
            ColorOf(pageVisual.Edge!).ToString());
        Check("Changed raised once per switch", changed == 1, changed.ToString());
        window.SetTool(ToolKind.Text);
        window.BeginNewText(0, new Point2(90, 420));
        await Snapshot("dark-text.png");
        window.FinishTextEditing();
        await Settle();
        window.SetTool(ToolKind.AreaEraser);
        await Snapshot("dark-eraser.png");
        await SnapshotWindow(OffScreen(new SettingsDialog(new(true, false, new(true, 5, PdfNotes.Editor.Services.BackupMode.Folder, @"C:\Backups", 10)), () => true), double.NaN, double.NaN), "dark-dialog-settings.png");
        await SnapshotWindow(OffScreen(new PageTransformDialog(true, setup, _ => [setup]), 930, 640), "dark-dialog-page-resize.png");
        await SnapshotWindow(OffScreen(new NewNoteDialog(), double.NaN, double.NaN), "dark-dialog-new-note.png");
        SnapshotElement(Menu(), "dark-menu.png", 220);

        foreach (var accent in new[] { AccentColor.Green, AccentColor.Orange })
        {
            Theme.Apply(AppTheme.Light, accent);
            window.SetTool(ToolKind.Pen);
            await Snapshot($"light-{accent.ToString().ToLowerInvariant()}-pen.png");
        }
        Theme.Apply(AppTheme.Dark, AccentColor.Purple);
        await Snapshot("dark-purple-pen.png");
        Theme.Apply(AppTheme.Light, AccentColor.Blue);
        await Settle();
        Check("back to light blue restores the frame colors", ColorOf(window.Background) == LightWindow && Theme.Color("Accent") == System.Windows.Media.Color.FromRgb(0x1E, 0x6F, 0xD9),
            ColorOf(window.Background).ToString());
        Check("selection marks do not change with the theme (paper-side token)", Theme.Color("Overlay.Selection") == System.Windows.Media.Color.FromRgb(0x1E, 0x6F, 0xD9));
    }

    /// <summary>
    /// D13b ribbon (spec 3.2): every tab fits one row at the default 1200 DIP, narrower windows wrap instead of clipping,
    /// switching tabs keeps the tool, and the page tab runs the page commands that used to be only in the context menu.
    /// </summary>
    private static async Task Ribbon()
    {
        window.SetTool(ToolKind.Pen);
        foreach (var width in new[] { 1200.0, 1000, 820, 720 })
        {
            window.Width = width;
            foreach (var tab in Enum.GetValues<RibbonTab>())
            {
                window.ShowRibbonTab(tab);
                await Snapshot($"ribbon-{tab.ToString().ToLowerInvariant()}-{width:0}.png");
                var panel = (WrapPanel)window.FindName(tab + "Tab");
                var items = panel.Children.OfType<FrameworkElement>().Where(c => c.IsVisible).ToList();
                int rows = items.Select(c => Math.Round(System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(c).Top)).Distinct().Count();
                double right = items.Max(c => c.TranslatePoint(new Point(c.ActualWidth, 0), panel).X);
                if (width == 1200) Check($"{tab} tab fits one row at 1200 DIP", rows == 1, $"{rows} rows");
                Check($"{tab} tab is not clipped at {width:0} DIP", right <= panel.ActualWidth + 0.5, $"{rows} rows, right {right:0.#} / {panel.ActualWidth:0.#}");
            }
            var bar = (Panel)((Border)window.FindName("StyleBar")).Child;
            double barRight = bar.Children.OfType<FrameworkElement>().Where(c => c.IsVisible).Max(c => c.TranslatePoint(new Point(c.ActualWidth, 0), bar).X);
            Check($"pen settings bar is not clipped at {width:0} DIP", barRight <= bar.ActualWidth + 0.5, $"right {barRight:0.#} / {bar.ActualWidth:0.#}");
            var gear = (FrameworkElement)window.FindName("SettingsButton");
            var lastTab = (FrameworkElement)window.FindName("ViewTabButton");
            double gearLeft = gear.TranslatePoint(new Point(0, 0), window).X, tabsRight = lastTab.TranslatePoint(new Point(lastTab.ActualWidth, 0), window).X;
            double client = ((FrameworkElement)window.Content).ActualWidth;
            Check($"tab row (quick commands, tabs, settings) fits at {width:0} DIP", tabsRight <= gearLeft && gearLeft + gear.ActualWidth <= client + 0.5,
                $"tabs end {tabsRight:0.#}, settings {gearLeft:0.#}..{gearLeft + gear.ActualWidth:0.#}, client {client:0.#}");
        }
        window.Width = 1200;
        Check("switching tabs keeps the current tool", window.CurrentTool == ToolKind.Pen, window.CurrentTool.ToString());
        Check("status bar shows the zoom percent", ((TextBlock)window.FindName("ZoomText")).Text == ((ComboBox)window.FindName("ZoomBox")).Text,
            ((TextBlock)window.FindName("ZoomText")).Text);

        // Page tab commands (the same methods as the page panel menu).
        System.Windows.Clipboard.Clear(); // another probe's window may have left pages there (pastable since D14g)
        window.ShowRibbonTab(RibbonTab.Page);
        await Settle();
        var pageTab = (WrapPanel)window.FindName("PageTab");
        Button PageButton(string tip) => Descendants(pageTab).OfType<Button>().Single(b => (string)b.ToolTip == Ui(tip));
        void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, b));
        var first = Session.Current.Pages[0];
        int count = Session.Current.Pages.Count;
        Press(PageButton("오른쪽으로 회전"));
        Check("page tab: rotate right", Session.Current.Pages[0].Setup.Rotation == (first.Setup.Rotation + 90) % 360, Session.Current.Pages[0].Setup.Rotation.ToString());
        Press(PageButton("왼쪽으로 회전"));
        Check("page tab: rotate left", Session.Current.Pages[0].Setup.Rotation == first.Setup.Rotation);
        Check("page tab: paste is disabled before a copy", !((Button)window.FindName("PastePageButton")).IsEnabled);
        Press(PageButton("복사 (페이지+주석)"));
        Check("page tab: paste is enabled after a copy", ((Button)window.FindName("PastePageButton")).IsEnabled);
        Press((Button)window.FindName("PastePageButton"));
        Check("page tab: paste adds the copied page", Session.Current.Pages.Count == count + 1 && Session.Current.Pages[1].Annotations.Count == first.Annotations.Count,
            Session.Current.Pages.Count.ToString());
        window.PromptOverride = (_, _) => MessageBoxResult.Yes;
        Press((Button)window.FindName("DeletePageButton"));
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        Check("page tab: delete removes the selected page", Session.Current.Pages.Count == count, Session.Current.Pages.Count.ToString());
        Check("page tab: still the pen tool", window.CurrentTool == ToolKind.Pen);
        window.ShowRibbonTab(RibbonTab.Home);
        await Settle();
    }

    /// <summary>
    /// D13c details: the three settings bars have one height (the document area does not move when the tool changes),
    /// a label never wraps away from its field, field text is centered vertically, drawn icons use the glyph stroke, and
    /// dialog buttons stay outside the scrolling settings.
    /// </summary>
    private static async Task Polish(ShapeAnnotation shape)
    {
        window.Width = 1200;
        window.ShowRibbonTab(RibbonTab.Home);
        var heights = new List<(string Name, double Bar, double ViewTop)>();
        async Task Measure(string name)
        {
            await Settle(300);
            var bar = new[] { "StyleBar", "EraserBorder", "TextStyleBorder" }.Select(n => (Border)window.FindName(n)).Single(b => b.IsVisible);
            heights.Add((name, bar.ActualHeight, View.TranslatePoint(new Point(0, 0), window).Y));
        }
        window.Selection.Select(null);
        window.SetTool(ToolKind.Pen); await Measure("pen");
        window.SetTool(ToolKind.Select); window.Selection.Select(shape.Id); await Measure("shape selected");
        window.Selection.Select(null);
        window.SetTool(ToolKind.AreaEraser); await Measure("eraser");
        window.SetTool(ToolKind.Text); window.BeginNewText(0, new Point2(90, 480)); await Measure("text");
        string detail = string.Join(", ", heights.Select(h => $"{h.Name} {h.Bar:0.#}/{h.ViewTop:0.#}"));
        var textBarPanel = (Panel)window.FindName("TextStylePanel");
        detail += $" | text panel {textBarPanel.ActualWidth:0.#} wide, items {string.Join("+", textBarPanel.Children.OfType<FrameworkElement>().Select(c => (c.DesiredSize.Width).ToString("0")))}, favorites {((FrameworkElement)window.FindName("FavoritesGroup")).ActualWidth:0.#}";
        Check("settings bars share one height at 1200 DIP", heights.All(h => Math.Abs(h.Bar - heights[0].Bar) < 0.5), detail);
        Check("the document area does not move when the tool changes", heights.All(h => Math.Abs(h.ViewTop - heights[0].ViewTop) < 0.5), detail);

        var textPanel = textBarPanel;
        var labels = Descendants(textPanel).OfType<TextBlock>().Where(t => t.Style == (Style)window.FindResource("FieldLabel")).Select(t => t.Text).ToArray();
        Check("text bar has one label: 투명도", labels.SequenceEqual(["투명도"]), string.Join(" ", labels));
        // Order (spec 3.2, 2026-10-13): [글자 채우기 테두리 | palette + | 투명도 % | 글꼴 크기 | B I U S 하이라이트 | 정렬 양식].
        double X(string name) => Descendants(textPanel).OfType<FrameworkElement>().Single(e => e.Name == name).TranslatePoint(new Point(0, 0), textPanel).X;
        double paletteX = Descendants(textPanel).OfType<Button>().First(b => b.Style == (Style)window.FindResource("Swatch")).TranslatePoint(new Point(0, 0), textPanel).X;
        var order = new[] { X("TextTargetText"), X("TextTargetFill"), X("TextTargetBorder"), paletteX, X("TextCustomColorButton"), X("TextAlphaBox"), X("TextSizeBox") };
        Check("text bar order: target, palette, +, transparency, size", order.Zip(order.Skip(1)).All(z => z.First < z.Second), string.Join(" ", order.Select(o => o.ToString("0"))));
        Check("text bar has no color code fields", !Descendants(textPanel).OfType<TextBox>().Any(t => t.Name is "TextColorBox" or "TextFillBox" or "TextBorderBox"));
        var fill = Descendants(textPanel).OfType<TextBox>().Single(t => t.Name == "TextSizeBox");
        var host = (FrameworkElement)fill.Template.FindName("PART_ContentHost", fill);
        double top = host.TranslatePoint(new Point(0, 0), fill).Y, bottom = fill.ActualHeight - top - host.ActualHeight;
        Check("field text is centered vertically", Math.Abs(top - bottom) <= 2, $"top {top:0.#}, bottom {bottom:0.#}");

        foreach (var width in new[] { 820.0, 720 })
        {
            window.Width = width;
            await Settle(300);
            await Snapshot($"text-bar-{width:0}.png");
            var split = new List<string>();
            foreach (var bar in new[] { "TextStylePanel" }.Select(n => (Panel)window.FindName(n)))
                foreach (var label in Descendants(bar).OfType<TextBlock>().Where(t => t.Style == (Style)window.FindResource("FieldLabel") && t.IsVisible))
                {
                    var parent = (Panel)VisualTreeHelper.GetParent(label);
                    int i = parent.Children.IndexOf(label);
                    var next = parent.Children.OfType<FrameworkElement>().Skip(i + 1).First(c => c.IsVisible);
                    double y1 = label.TranslatePoint(new Point(0, label.ActualHeight / 2), bar).Y, y2 = next.TranslatePoint(new Point(0, next.ActualHeight / 2), bar).Y;
                    if (Math.Abs(y1 - y2) > 4) split.Add(label.Text);
                }
            Check($"text bar labels stay with their fields at {width:0} DIP", split.Count == 0, string.Join(", ", split));
        }
        window.FinishTextEditing();
        window.Width = 1200;
        window.SetTool(ToolKind.Pen);
        await Settle();
        var pen = (Panel)((Border)window.FindName("StyleBar")).Child;
        window.Width = 720; await Settle(300);
        var penLabels = Descendants(pen).OfType<TextBlock>().Where(t => t.Style == (Style)window.FindResource("FieldLabel") && t.IsVisible).ToArray();
        Check("pen bar labels stay with their sliders at 720 DIP", penLabels.All(l =>
        {
            var parent = (Panel)VisualTreeHelper.GetParent(l);
            var next = parent.Children.OfType<FrameworkElement>().Skip(parent.Children.IndexOf(l) + 1).First(c => c.IsVisible);
            return Math.Abs(l.TranslatePoint(new Point(0, l.ActualHeight / 2), pen).Y - next.TranslatePoint(new Point(0, next.ActualHeight / 2), pen).Y) <= 4;
        }), string.Join(" ", penLabels.Select(l => l.Text)));
        window.Width = 1200; await Settle();

        var icon = (System.Windows.Shapes.Path)((ContentControl)window.FindName("ShapeToolIcon")).Content;
        Check("drawn shape icon uses the glyph cell and stroke", icon.StrokeThickness == (double)window.FindResource("Icon.Stroke") && icon.Width == (double)window.FindResource("Icon.Size"),
            $"stroke {icon.StrokeThickness}, size {icon.Width}");

        var setup = Session.Current.Pages[0].Setup;
        var dialog = OffScreen(new PageTransformDialog(true, setup, _ => [setup]), 930, 640);
        dialog.Show(); await Settle(500);
        var apply = Descendants(dialog).OfType<Button>().Single(b => b.IsDefault);
        bool scrolled = false;
        for (DependencyObject? d = apply; d is not null; d = VisualTreeHelper.GetParent(d)) if (d is ScrollViewer) scrolled = true;
        double applyBottom = apply.TranslatePoint(new Point(0, apply.ActualHeight), dialog).Y;
        double client = ((FrameworkElement)dialog.Content).ActualHeight;
        Check("page size dialog buttons are outside the scrolling settings", !scrolled && applyBottom <= client + 20.5, $"bottom {applyBottom:0.#} / content {client:0.#}");
        var cancel = Descendants(dialog).OfType<Button>().Single(b => b.IsCancel);
        Check("dialog buttons: 적용 then 취소 at the right", apply.TranslatePoint(new Point(0, 0), dialog).X < cancel.TranslatePoint(new Point(0, 0), dialog).X &&
            cancel.TranslatePoint(new Point(cancel.ActualWidth, 0), dialog).X > dialog.ActualWidth - 60);
        Save(Capture(dialog), "dialog-page-resize-930x640.png");
        dialog.Close();

        // Page tab 삽입 (spec 11.5, 2026-10-13): one insert button with ▾, no add-at-end; the dialog starts with the
        // current page's size and background and 만들기 as the default (Enter) button.
        var pageTab = (Panel)window.FindName("PageTab");
        var tips = Descendants(pageTab).OfType<Button>().Select(b => b.ToolTip as string).ToArray();
        Check("page tab: 삽입 with ▾, no 추가", tips.Contains("빈 페이지 삽입 (현재 페이지 뒤)") && window.FindName("InsertPageMenuButton") is Button && !tips.Contains("빈 페이지 추가 (맨 뒤)"),
            string.Join(", ", tips));
        var insert = OffScreen(new NewNoteDialog("빈 페이지 삽입", (182, 257), PageBackgroundKind.Grid), double.NaN, double.NaN);
        insert.Show(); await Settle(300);
        T In<T>(string name) where T : FrameworkElement => Descendants(insert).OfType<T>().Single(e => e.Name == name);
        var create = In<Button>("CreateButton");
        Check("insert dialog starts with the page's size and background", In<CheckBox>("SameSizeBox").IsChecked == true && In<TextBox>("WidthBox").Text == "182" &&
            In<TextBox>("HeightBox").Text == "257" && In<RadioButton>("GridButton").IsChecked == true);
        Check("insert dialog: 만들기 is the default button", create.IsDefault && (string)create.Content == "만들기" && FocusManager.GetFocusedElement(insert) == create);
        Save(Capture(insert), "dialog-insert-page.png");
        insert.Close();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    /// <summary>
    /// Text bar palette (spec 3.2 and 6.2, 2026-10-13): the target 글자/채우기/테두리 chooses what a swatch and the
    /// transparency change; the next new box starts with the last fill and border, also after a restart. A color added
    /// with a code becomes a user color in every palette and can be removed (app setting, not document state).
    /// No OS input: swatch clicks and menu clicks are raised Click events, fields commit on a raised LostKeyboardFocus.
    /// </summary>
    private static async Task TextBoxMemory()
    {
        window = OffScreen(new MainWindow(), 1200, 860);
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        window.MessageSink = _ => { };
        window.Show();
        await Settle(300);
        var textPanel = (Panel)window.FindName("TextStylePanel");
        T Named<T>(string name) where T : FrameworkElement => Descendants(textPanel).OfType<T>().Single(e => e.Name == name);
        var alpha = Named<TextBox>("TextAlphaBox");
        void Alpha(string text)
        {
            alpha.Text = text;
            alpha.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, alpha, null) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        }
        void Target(string name) => Named<RadioButton>(name).IsChecked = true;
        Button[] Swatches(Panel bar) => Descendants(bar).OfType<Button>().Where(b => b.Style == (Style)window.FindResource("Swatch")).ToArray();
        Button Swatch(Panel bar, RgbColor color) => Swatches(bar).First(b => Equals(b.DataContext, color));
        void Click(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        RgbColor red = new(0xE5, 0x39, 0x35), blue = new(0x1E, 0x88, 0xE5), cream = new(0xFF, 0xF2, 0xA8);

        window.SetTool(ToolKind.Text);
        window.BeginNewText(0, new Point2(90, 120));
        window.ActiveTextEditor!.CaretPosition.InsertTextInRun("첫 상자");
        window.FlushTextEdit();
        await Settle();
        Check("text target starts at 글자 with transparency off", window.TextColorTarget == TextColorTarget.Text && !alpha.IsEnabled);
        window.ActiveTextEditor.SelectAll();
        Click(Swatch(textPanel, blue));
        Check("글자 + a swatch colors the selected characters", window.EditingText!.Runs.All(r => r.Color == blue), string.Join(",", window.EditingText.Runs.Select(r => r.Color.ToHex())));
        Check("the character color is marked in the palette", (string?)Swatch(textPanel, blue).Tag == "Current");

        Target("TextTargetFill");
        Check("채우기 turns transparency on", window.TextColorTarget == TextColorTarget.Fill && alpha.IsEnabled);
        window.SetTextColor(cream); // the + entry with a color code (real typing in d13b)
        Check("a fill color for a box without fill makes it opaque", window.EditingText?.BoxStyle is { BackgroundOpacity: 1 } f && f.BackgroundColor == cream && alpha.Text == "0", window.EditingText?.BoxStyle.ToString() ?? "");
        Check("the added color joins the user colors of both bars", window.RecentColors.FirstOrDefault() == cream &&
            Swatches((Panel)((Border)window.FindName("StyleBar")).Child).Any(b => Equals(b.DataContext, cream)) && Swatches(textPanel).Count(b => Equals(b.DataContext, cream)) == 1);
        Target("TextTargetBorder");
        Click(Swatch(textPanel, red));
        Alpha("20");
        var expected = new TextBoxStyle(cream, 1, true, red, 0.8);
        Check("테두리 + a swatch and 20% set the border", window.EditingText?.BoxStyle == expected, window.EditingText?.BoxStyle.ToString() ?? "");
        Check("the characters keep their color", window.EditingText!.Runs.All(r => r.Color == blue));
        window.FinishTextEditing();
        var first = Session.Current.Pages[0].Annotations.OfType<TextBoxAnnotation>().Single();
        Check("the finished box keeps it", first.BoxStyle == expected);
        window.BeginNewText(0, new Point2(90, 300));
        Check("the next new box starts with the last fill and border", window.EditingText?.BoxStyle == expected, window.EditingText?.BoxStyle.ToString() ?? "");
        window.FinishTextEditing();

        // A selected box (selection tool) changed from the bar also becomes the new default; 100% border is no border.
        window.SetTool(ToolKind.Select);
        window.Selection.Select(first.Id);
        await Settle();
        Target("TextTargetFill");
        Click(Swatch(textPanel, new(0x43, 0xA0, 0x47)));
        Target("TextTargetBorder");
        Alpha("100");
        var second = expected with { BackgroundColor = new(0x43, 0xA0, 0x47), BorderEnabled = false };
        Check("a selected box changed from the palette", Session.Current.FindAnnotation(first.Id)?.Annotation is TextBoxAnnotation { BoxStyle: var s } && s == second,
            (Session.Current.FindAnnotation(first.Id)?.Annotation as TextBoxAnnotation)?.BoxStyle.ToString() ?? "");
        Check("no border: no swatch is marked and transparency reads 100", Swatches(textPanel).All(b => b.Tag is null) && alpha.Text == "100");
        window.SetTool(ToolKind.Text);
        window.BeginNewText(0, new Point2(90, 400));
        Check("the next new box follows that change", window.EditingText?.BoxStyle == second);
        window.FinishTextEditing();

        // Removing a user color: only user colors have the menu; the document and its Undo do not change.
        window.SetTool(ToolKind.Pen);
        window.SetColor(new(0x12, 0x34, 0x56));
        var pen = (Panel)((Border)window.FindName("StyleBar")).Child;
        Check("basic colors cannot be removed", Swatches(pen).Take(10).All(b => b.ContextMenu is null));
        var model = Session.Current;
        var remove = (MenuItem)Swatch(pen, cream).ContextMenu!.Items[0];
        Check("user color menu reads 제거", (string)remove.Header == "제거");
        remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("제거 removes the color from every palette", !window.RecentColors.Contains(cream) && !Swatches(pen).Any(b => Equals(b.DataContext, cream)) &&
            !Swatches(textPanel).Any(b => Equals(b.DataContext, cream)) && window.RecentColors.Contains(new(0x12, 0x34, 0x56)), string.Join(",", window.RecentColors.Select(c => c.ToHex())));
        Check("removing a color leaves the document and the box colors as they are", ReferenceEquals(Session.Current, model) &&
            Session.Current.FindAnnotation(first.Id)?.Annotation is TextBoxAnnotation { BoxStyle.BackgroundColor: var kept } && kept == second.BackgroundColor);

        // The + picker (2026-10-13 user request): color plane + hue strip + code, shared by the pen and the text bar.
        Check("both palettes have the color plane picker", window.CustomColorPickers.Keys.Order().SequenceEqual(["CustomColor", "TextCustomColor"]));
        var picker = window.CustomColorPickers["CustomColor"];
        picker.ShowColor(new(0x12, 0x80, 0x40));
        Check("the picker shows a color in the plane and the code", picker.SelectedColor == new RgbColor(0x12, 0x80, 0x40) && picker.Code.Text == "#128040", picker.Code.Text);
        picker.Code.Text = "#3366CC";
        Check("a typed code moves the plane", picker.SelectedColor == new RgbColor(0x33, 0x66, 0xCC), picker.SelectedColor.ToHex());
        picker.Add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check("추가 applies the color and keeps it as a user color", window.PenStyle.Color == new RgbColor(0x33, 0x66, 0xCC) && window.RecentColors.Last() == new RgbColor(0x33, 0x66, 0xCC)); // added at the end (D14g)
        SnapshotElement(new ColorPickerPanel("Probe"), "color-picker.png", double.NaN);
        // Eyedropper sampling: the drawn pixel at a point of the window (here the red basic swatch's center).
        var redSwatch = Swatch(pen, red);
        var sampled = window.SampleColor(redSwatch.TranslatePoint(new Point(redSwatch.ActualWidth / 2, redSwatch.ActualHeight / 2), window));
        Check("the eyedropper samples the drawn pixel", sampled == red, sampled.ToHex());

        // Restart: a new window reads the saved settings.
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        window.Close();
        await Settle(300);
        window = OffScreen(new MainWindow(), 1200, 860);
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        window.Show();
        await Settle(300);
        Check("after a restart new boxes still start with it", window.NewTextBoxStyle == second, window.NewTextBoxStyle.ToString());
        Check("after a restart the removed color stays removed", !window.RecentColors.Contains(cream) && window.RecentColors.Contains(new(0x12, 0x34, 0x56)));
        Theme.Apply(AppTheme.Dark, AccentColor.Blue);
        SnapshotElement(new ColorPickerPanel("Probe"), "dark-color-picker.png", double.NaN);
        Theme.Apply(AppTheme.Light, AccentColor.Blue);
    }

    /// <summary>
    /// D13d 화면 settings (spec 3.2, 4.5, 18.1) without OS input: following the Windows theme (WM_SETTINGCHANGE sent to
    /// the window, the Windows value replaced by <see cref="Theme.SystemThemeOverride"/>), the dark title bar, the
    /// settings window preview, storage across a restart, and the page numbers drawn only at the pages' bottom-right.
    /// The modal OK/Cancel path of the settings window runs with real input in d13b.
    /// </summary>
    private static async Task Import()
    {
        window = OffScreen(new MainWindow(), 1200, 860);
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        window.MessageSink = _ => { };
        window.Show();
        await Settle(300);
        await D13gCheck.Run(window, folder, (name, passed, detail) => { Check(name, passed, detail); return passed; }, Snapshot,
            (w, name) => { Save(Capture(w), name); return Task.CompletedTask; }, w => OffScreen(w, double.NaN, double.NaN));
    }

    private static async Task Display()
    {
        string pdf = Path.Combine(folder, "sample.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        window = OffScreen(new MainWindow(), 1200, 1400);
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        window.MessageSink = _ => { };
        window.Show();
        await Settle(300);
        if (!await window.OpenFileAsync(pdf)) throw new InvalidOperationException("sample did not open");
        await Settle(600);
        Check("defaults: follow Windows, blue, page numbers on", window.DisplaySettings == DisplayValues.Default && Theme.Choice == ThemeChoice.System &&
            Theme.Mode == AppTheme.Light && Theme.Accent == AccentColor.Blue && window.PageNumbersShown, window.DisplaySettings.ToString());

        // Windows switches its app theme while the app runs.
        Theme.SystemThemeOverride = () => AppTheme.Dark;
        SettingChange(window, "ImmersiveColorSet");
        await Settle(300);
        Check("follows Windows to dark", Theme.Mode == AppTheme.Dark && ColorOf(window.Background) == Theme.Color("Window.Background"), ColorOf(window.Background).ToString());
        var titleBar = Theme.IsTitleBarDark(window);
        Check("title bar turns dark with it", titleBar != false, titleBar?.ToString() ?? "not reported by Windows");
        await Snapshot("display-follow-dark.png");
        Theme.SystemThemeOverride = () => AppTheme.Light;
        SettingChange(window, "Policy"); // another setting: ignored
        await Settle(300);
        Check("other setting changes are ignored", Theme.Mode == AppTheme.Dark);
        SettingChange(window, "ImmersiveColorSet");
        await Settle(300);
        Check("follows Windows back to light", Theme.Mode == AppTheme.Light && Theme.IsTitleBarDark(window) != true);
        window.SetDisplay(new(ThemeChoice.Light, AccentColor.Blue, true));
        Theme.SystemThemeOverride = () => AppTheme.Dark;
        SettingChange(window, "ImmersiveColorSet");
        await Settle(300);
        Check("밝게 does not follow Windows", Theme.Mode == AppTheme.Light);
        Theme.SystemThemeOverride = () => AppTheme.Light;

        // Settings window 화면: every choice shows at once; nothing is stored before OK.
        var stored = window.DisplaySettings;
        var dialog = OffScreen(new SettingsDialog(new(true, false, new(true, 5, PdfNotes.Editor.Services.BackupMode.Folder, @"C:\Backups", 10), stored), () => true, window.PreviewDisplay),
            double.NaN, double.NaN);
        dialog.Show();
        await Settle(400);
        T In<T>(string name) where T : FrameworkElement => Descendants(dialog).OfType<T>().Single(e => e.Name == name);
        var categories = In<ListBox>("CategoryList");
        Check("settings window has 화면", categories.Items.Cast<ListBoxItem>().Select(i => (string)i.Content).SequenceEqual(["필기", "저장", "화면"])); // 호환 is gone (D14g)
        categories.SelectedIndex = 2;
        await Settle(300);
        Check("화면 starts with the stored values", In<RadioButton>("ThemeLight").IsChecked == true && In<RadioButton>("AccentBlue").IsChecked == true && In<CheckBox>("PageNumbersBox").IsChecked == true);
        Save(Capture(dialog), "dialog-settings-display-light.png");
        In<RadioButton>("ThemeDark").IsChecked = true;
        await Settle(300);
        Check("어둡게 shows at once, the settings window too", Theme.Mode == AppTheme.Dark && ColorOf(dialog.Background) == Theme.Color("Surface.Background") &&
            ColorOf(window.Background) == Theme.Color("Window.Background"));
        In<RadioButton>("AccentGreen").IsChecked = true;
        await Settle(300);
        Check("강조색 shows at once", Theme.Accent == AccentColor.Green && Theme.Color("Accent") == Theme.AccentOf(AccentColor.Green));
        var dot = (System.Windows.Shapes.Ellipse)In<RadioButton>("AccentGreen").Content;
        Check("accent swatches show the dark theme variants", ((SolidColorBrush)dot.Fill).Color == Theme.AccentOf(AccentColor.Green) && Theme.AccentOf(AccentColor.Green) == System.Windows.Media.Color.FromRgb(0x4C, 0xC0, 0x7A));
        In<CheckBox>("PageNumbersBox").IsChecked = false;
        await Settle(300);
        Check("page numbers off at once", !window.PageNumbersShown);
        Save(Capture(dialog), "dialog-settings-display-dark.png");
        await Snapshot("display-preview-dark-green.png");
        Check("nothing is stored before OK", window.DisplaySettings == stored);
        dialog.Close();
        window.PreviewDisplay(stored); // what 취소 does (ShowSettings)
        await Settle(300);
        Check("cancel restores the stored theme and page numbers", Theme.Mode == AppTheme.Light && Theme.Accent == AccentColor.Blue && window.PageNumbersShown);

        // Page numbers: only at each page's bottom-right, the same size at every zoom, under the annotations, not in thumbnails.
        window.SetDisplay(new(ThemeChoice.System, AccentColor.Blue, true));
        var corners = new List<string>();
        var heights = new List<double>();
        foreach (var zoom in new[] { 0.5, 0.75 })
        {
            View.SetZoom(zoom);
            View.GoToPage(0);
            await Settle(600);
            var on = Pixels(Capture(window));
            var thumbsOn = Pixels(Capture(window), (FrameworkElement)window.FindName("PagePanel"));
            window.PreviewDisplay(window.DisplaySettings with { PageNumbers = false });
            await Settle(400);
            var off = Pixels(Capture(window));
            var thumbsOff = Pixels(Capture(window), (FrameworkElement)window.FindName("PagePanel"));
            window.PreviewDisplay(window.DisplaySettings);
            await Settle(300);
            if (zoom == 0.5) await Snapshot("display-page-numbers.png");
            Check($"thumbnails never show the number ({zoom:0.##})", thumbsOn.SequenceEqual(thumbsOff));
            double dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
            int width = (int)Math.Round(window.ActualWidth * dpi);
            var changed = Enumerable.Range(0, on.Length / 4).Where(i => Math.Abs(on[i * 4] - off[i * 4]) + Math.Abs(on[i * 4 + 1] - off[i * 4 + 1]) + Math.Abs(on[i * 4 + 2] - off[i * 4 + 2]) > 6)
                .Select(i => new Point(i % width / dpi, i / width / dpi)).ToArray();
            var pages = View.RealizedPages.Select(v => new Rect(v.TranslatePoint(new Point(0, 0), window), new Size(v.ActualWidth, v.ActualHeight)))
                .Where(r => r.Bottom <= ((FrameworkElement)window.FindName("Scroller")).TranslatePoint(new Point(0, ((FrameworkElement)window.FindName("Scroller")).ActualHeight), window).Y).ToArray();
            bool InCorner(Point p) => pages.Any(r => p.X >= r.Right - 40 && p.X <= r.Right && p.Y >= r.Bottom - 30 && p.Y <= r.Bottom);
            var outside = changed.Where(p => !InCorner(p)).ToArray();
            Check($"numbers only at the bottom-right of the pages ({zoom:0.##})", changed.Length > 0 && outside.Length == 0 && pages.Length > 0 &&
                pages.All(r => changed.Any(p => r.Contains(p))), $"{changed.Length} px changed, {outside.Length} outside, {pages.Length} pages fully visible");
            var first = changed.Where(p => pages[0].Contains(p)).ToArray();
            heights.Add(first.Max(p => p.Y) - first.Min(p => p.Y));
            corners.Add($"{zoom}: right gap {pages[0].Right - first.Max(p => p.X):0.#}, bottom gap {pages[0].Bottom - first.Max(p => p.Y):0.#}, height {heights[^1]:0.#}");
        }
        Check("the number has the same screen size at every zoom", Math.Abs(heights[0] - heights[1]) <= 1, string.Join("; ", corners));
        Check("toggling the numbers leaves the document unchanged", !Session.IsDirty);

        // Stored across a restart; the title bar of the new window is dark from the start.
        window.SetDisplay(new(ThemeChoice.Dark, AccentColor.Purple, false));
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        window.Close();
        await Settle(300);
        Theme.Use(ThemeChoice.Light, AccentColor.Blue); // as a fresh process would start
        window = OffScreen(new MainWindow(), 1200, 860);
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        window.Show();
        await Settle(400);
        Check("after a restart: 어둡게, 보라, page numbers off", Theme.Choice == ThemeChoice.Dark && Theme.Mode == AppTheme.Dark && Theme.Accent == AccentColor.Purple && !window.PageNumbersShown &&
            window.DisplaySettings == new DisplayValues(ThemeChoice.Dark, AccentColor.Purple, false));
        titleBar = Theme.IsTitleBarDark(window);
        Check("a new window opens with a dark title bar", titleBar != false, titleBar?.ToString() ?? "not reported by Windows");
        await Snapshot("display-restart-dark-purple.png");
        window.SetDisplay(DisplayValues.Default);
        await Settle(300);
        Check("back to the defaults", Theme.Mode == AppTheme.Light && Theme.Accent == AccentColor.Blue && window.PageNumbersShown);
    }

    /// <summary>
    /// D13g follow-up requests without OS input: the zoom menu of the status bar (4.4), no notice on a page without
    /// glyphs (9.3), 자동 beside pt disabling the width (9.2), the tab named 필기 and the automatic 필기 ↔ 페이지 switch
    /// with its 화면 setting (3.2, 18.1). Real clicks on the thumbnails and the canvas run in d13b.
    /// </summary>
    private static async Task Followup()
    {
        string pdf = Path.Combine(folder, "sample.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        window = OffScreen(new MainWindow(), 1200, 860);
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        window.MessageSink = _ => { };
        window.Show();
        await Settle(300);
        if (!await window.OpenFileAsync(pdf)) throw new InvalidOperationException("sample did not open");
        await Settle(600);
        T Named<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));

        // Tab name.
        Check("tabs are 파일 필기 페이지 보기, start on 필기", new[] { "FileTabButton", "HomeTabButton", "PageTabButton", "ViewTabButton" }
            .Select(n => (string)Named<ContentControl>(n).Content).SequenceEqual(["파일", "필기", "페이지", "보기"]) && window.CurrentRibbonTab == RibbonTab.Home);

        // Zoom menu.
        View.SetZoom(1);
        await Settle();
        var zoomButton = Named<Button>("ZoomButton");
        Check("zoom percent is a button that looks clickable and keeps the focus where it is", zoomButton.Cursor == Cursors.Hand && !zoomButton.Focusable &&
            Named<TextBlock>("ZoomText").Text == "100%");
        Click(zoomButton);
        await Settle(300);
        var menu = window.LastZoomMenu!;
        var items = menu.Items.OfType<MenuItem>().ToArray();
        Check("menu: the zoom steps 25–800% (D14g), a separator, 너비 맞춤·페이지 맞춤", items.Select(i => (string)i.Header).SequenceEqual(["25%", "50%", "75%", "100%", "125%", "150%", "200%", "300%", "400%", "800%", "너비 맞춤", "페이지 맞춤"]) &&
            menu.Items[10] is Separator);
        Check("only the current zoom is checked", items.Where(i => i.IsChecked).Select(i => (string)i.Header).SequenceEqual(["100%"]));
        items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, items[2]));
        menu.IsOpen = false;
        await Settle();
        Check("75% applies", Math.Abs(View.Zoom - .75) < 1e-9 && Named<TextBlock>("ZoomText").Text == "75%");
        Click(zoomButton); await Settle(300);
        items = window.LastZoomMenu!.Items.OfType<MenuItem>().ToArray();
        items[10].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, items[10]));
        window.LastZoomMenu!.IsOpen = false;
        await Settle();
        double fromMenu = View.Zoom;
        View.SetZoom(1); View.FitWidth(); await Settle();
        Check("너비 맞춤 in the menu = the view tab's fit width", Math.Abs(View.Zoom - fromMenu) < 1e-9, $"{fromMenu:0.###}");
        Click(zoomButton); await Settle(300);
        items = window.LastZoomMenu!.Items.OfType<MenuItem>().ToArray();
        Check("a fitted zoom off the presets checks nothing", items.All(i => !i.IsChecked) || items.Count(i => i.IsChecked) == 1 && Math.Abs(View.Zoom * 100 - int.Parse(((string)items.Single(i => i.IsChecked).Header).TrimEnd('%'))) < .5);
        items[11].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, items[11]));
        window.LastZoomMenu!.IsOpen = false;
        await Settle();
        double fitPage = View.Zoom;
        View.SetZoom(1); View.FitPage(); await Settle();
        Check("페이지 맞춤 in the menu = fit page", Math.Abs(View.Zoom - fitPage) < 1e-9);
        Check("the zoom menu leaves the document unchanged", !Session.IsDirty && Session.UndoCount == 0);
        View.SetZoom(1); await Settle();
        await Snapshot("followup-main.png");

        // The highlighter (D14g): 자동 beside pt; the width stays on for free strokes.
        window.SetTool(ToolKind.FreeHighlight);
        window.SetHighlightAuto(true);
        await Settle();
        var auto = Named<ToggleButton>("HighlightAutoButton");
        var width = Named<TextBox>("WidthBox");
        var group = (Panel)width.Parent;
        var unit = group.Children.OfType<TextBlock>().Single(b => b.Text == "pt");
        Check("자동 is in the width group right after pt", ReferenceEquals(auto.Parent, group) && group.Children.IndexOf(auto) == group.Children.IndexOf(unit) + 1 &&
            auto.TranslatePoint(new Point(0, 0), window).X >= unit.TranslatePoint(new Point(unit.ActualWidth, 0), window).X);
        Check("자동 on: the width stays enabled for free strokes (spec 9.1)", auto.IsVisible && auto.IsChecked == true && width.IsEnabled && Named<Slider>("WidthSlider").IsEnabled);
        var bar = (Border)window.FindName("StyleBar");
        await Snapshot("followup-highlight-auto.png");
        window.SetHighlightAuto(false); await Settle();
        Check("자동 off: width enabled", width.IsEnabled && Named<Slider>("WidthSlider").IsEnabled);
        window.SetHighlightAuto(true); await Settle();
        window.SetTool(ToolKind.Pen); await Settle();
        Check("other tools: 자동 hidden, width enabled", !auto.IsVisible && width.IsEnabled && bar.IsVisible);

        // No notice on a page without glyphs (the old notice stayed in the status bar, spec 9.3).
        Check("new note", await window.CreateNoteAsync(NewPageSpec.Default));
        await Settle(400);
        window.SetTool(ToolKind.FreeHighlight);
        Named<TextBlock>("StatusText").Text = "";
        var at = View.PageTransform(0).PageToDip.Transform(new Point2(200, 200));
        bool started = View.Tool!.PointerDown(Ptr(new Point(at.X, at.Y), ModifierKeys.None, 1));
        View.Tool!.Cancel();
        Check("no glyphs: the press starts a free stroke and shows no notice (spec 9.2)", started && Named<TextBlock>("StatusText").Text == "" && !Session.Current.AllAnnotations.Any(),
            Named<TextBlock>("StatusText").Text);

        if (!await window.OpenFileAsync(pdf)) throw new InvalidOperationException("sample did not reopen");
        await Settle(600);

        // Automatic tab switch.
        var multi = Named<ToggleButton>("MultiSelectButton");
        void ToggleMulti() { multi.IsChecked = !multi.IsChecked; Click(multi); }
        window.SetTool(ToolKind.Pen);
        window.ShowRibbonTab(RibbonTab.Home);
        var focus = Keyboard.FocusedElement;
        ToggleMulti(); await Settle();
        Check("page list work (multi-select) shows 페이지, the tool stays", window.CurrentRibbonTab == RibbonTab.Page && window.CurrentTool == ToolKind.Pen &&
            Named<FrameworkElement>("PageTab").IsVisible && ReferenceEquals(Keyboard.FocusedElement, focus));
        ToggleMulti(); await Settle();
        window.SetTool(ToolKind.Select); await Settle();
        Check("a tool change shows 필기", window.CurrentRibbonTab == RibbonTab.Home);
        var shape = new ShapeAnnotation(Guid.NewGuid(), false, Affine.Identity, ShapeKind.Rectangle, new(100, 100, 120, 80), [], ShapeStyle.Default);
        Session.Execute(DocumentEdits.AddAnnotation(Session.Current, Session.Current.Pages[0].Id, shape));
        window.ShowRibbonTab(RibbonTab.Page);
        View.SetZoom(1.5); View.GoToPage(1); await Settle();
        Check("zoom and page moves keep 페이지", window.CurrentRibbonTab == RibbonTab.Page);
        window.Selection.Select(shape.Id); await Settle();
        Check("selecting an annotation shows 필기", window.CurrentRibbonTab == RibbonTab.Home && window.CurrentTool == ToolKind.Select);
        window.Selection.Select(null);
        foreach (var tab in new[] { RibbonTab.File, RibbonTab.View })
        {
            window.ShowRibbonTab(tab);
            ToggleMulti(); await Settle();
            window.SetTool(ToolKind.Pen); await Settle();
            ToggleMulti(); await Settle();
            Check($"{tab} stays open", window.CurrentRibbonTab == tab);
        }
        Check("탭 자동 전환 is on by default", window.DisplaySettings.AutoSwitchTabs && DisplayValues.Default.AutoSwitchTabs);
        window.SetDisplay(window.DisplaySettings with { AutoSwitchTabs = false });
        window.ShowRibbonTab(RibbonTab.Home);
        ToggleMulti(); await Settle();
        Check("off: page list work keeps 필기", window.CurrentRibbonTab == RibbonTab.Home);
        window.ShowRibbonTab(RibbonTab.Page);
        window.SetTool(ToolKind.Select); window.Selection.Select(shape.Id); await Settle();
        Check("off: tool change and selection keep 페이지", window.CurrentRibbonTab == RibbonTab.Page);
        ToggleMulti();
        window.Selection.Select(null);

        // The settings window shows it under 화면; stored across a restart.
        var dialog = OffScreen(new SettingsDialog(new(true, false, new(true, 5, PdfNotes.Editor.Services.BackupMode.Folder, @"C:\Backups", 10), window.DisplaySettings), () => true, window.PreviewDisplay),
            double.NaN, double.NaN);
        dialog.Show();
        await Settle(400);
        Descendants(dialog).OfType<ListBox>().Single(e => e.Name == "CategoryList").SelectedIndex = 2; // 필기·저장·화면 (호환 is gone, D14g)
        await Settle(300);
        var box = Descendants(dialog).OfType<CheckBox>().Single(e => e.Name == "AutoTabsBox");
        Check("화면 › 탭 자동 전환 shows the stored value", (string)box.Content == "탭 자동 전환" && box.IsChecked == false && box.IsVisible);
        Save(Capture(dialog), "followup-settings-display.png");
        dialog.Close();
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        window.Close();
        await Settle(300);
        window = OffScreen(new MainWindow(), 1200, 860);
        window.PromptOverride = (_, _) => MessageBoxResult.No;
        window.Show();
        await Settle(400);
        Check("after a restart: 탭 자동 전환 off, start tab 필기", !window.DisplaySettings.AutoSwitchTabs && window.CurrentRibbonTab == RibbonTab.Home);
        window.SetDisplay(DisplayValues.Default);
        Check("back on", window.DisplaySettings.AutoSwitchTabs);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint SendMessage(nint window, int message, nint wParam, string lParam);

    /// <summary>The broadcast Windows sends when a setting changes (here only to this window; not user input).</summary>
    private static void SettingChange(Window w, string area) =>
        SendMessage(new System.Windows.Interop.WindowInteropHelper(w).Handle, 0x001A, 0, area);

    private static byte[] Pixels(BitmapSource bitmap, FrameworkElement? part = null)
    {
        if (part is not null)
        {
            double dpi = bitmap.DpiX / 96;
            var origin = part.TranslatePoint(new Point(0, 0), window);
            bitmap = new CroppedBitmap(bitmap, new Int32Rect((int)(origin.X * dpi), (int)(origin.Y * dpi), (int)(part.ActualWidth * dpi), (int)(part.ActualHeight * dpi)));
        }
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static ContextMenu Menu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "복사", InputGestureText = "Ctrl+C" });
        menu.Items.Add(new MenuItem { Header = "붙여넣기", IsEnabled = false, InputGestureText = "Ctrl+V" });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "잠금", IsCheckable = true, IsChecked = true });
        var sub = new MenuItem { Header = "저장된 양식" };
        sub.Items.Add(new MenuItem { Header = "Slot 1" });
        menu.Items.Add(sub);
        menu.Items.Add(new MenuItem { Header = "사각형", Icon = new TextBlock { Text = "\uE739", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") } });
        return menu;
    }

    private static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;

    private static readonly System.Windows.Media.Color LightWindow = System.Windows.Media.Color.FromRgb(0xFA, 0xFA, 0xFA);

    private static void Check(string name, bool passed, string detail = "")
    {
        checks.Add(new { Name = name, Passed = passed, Detail = detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        // With another language pack (D14e long-string pack) layout checks only report, so every screen is still rendered.
        if (!passed && Environment.GetEnvironmentVariable("PDFNOTES_LANGUAGE_PACK") is not { Length: > 0 }) throw new InvalidOperationException(name + ": " + detail);
    }

    private static Task Settle(int ms = 250) => Task.Delay(ms);

    private static async Task Snapshot(string name)
    {
        await Settle(450);
        Save(Capture(window), name);
    }

    private static async Task SnapshotWindow(Window w, string name)
    {
        w.Show();
        await Settle(500);
        Save(Capture(w), name);
        w.Close();
    }

    private static void SnapshotElement(FrameworkElement element, string name, double width)
    {
        var available = new Size(double.IsNaN(width) ? double.PositiveInfinity : width, double.PositiveInfinity);
        element.Measure(available);
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        // Shared-size columns (menus) settle after the first pass.
        element.InvalidateMeasure();
        element.Measure(available);
        var size = element.DesiredSize;
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        Save(bitmap, name);
    }

    private static RenderTargetBitmap Capture(Window w)
    {
        double dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Round(w.ActualWidth * dpi), (int)Math.Round(w.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(w);
        return bitmap;
    }

    private static void Save(BitmapSource bitmap, string name)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
        written.Add(name);
    }
}
