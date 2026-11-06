using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.UI.Canvas;
using PdfNotes.UI.Views;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;
using PdfNotes.Pdf.Prototype;
using PdfNotes.ViewModels;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// Roadmap P4 A5 under Avalonia.Headless: the WPF window's frame on Avalonia — the ribbon and its tabs, the three bars,
/// the palette, favorites, the page list, search, outline, links, sticky notes, the dialogs, menus and the clipboard —
/// each wired to the P1 view model. The layout checks are the D13 rules: the ribbon and every bar on one row at 1200
/// DIPs, nothing cut off at 720 DIPs.
/// </summary>
public sealed class FrameTests
{
    private static MainWindow Window(double width = 1200, double height = 1000, AppSettings? settings = null)
    {
        var window = new MainWindow(TestWindows.App(settings ?? new AppSettings(), Headless.TempFolder()), TestWindows.Dialogs) { Width = width, Height = height };
        window.Script.PromptOverride = (_, buttons) => buttons == AskButtons.YesNo ? Answer.Yes : Answer.No; // yes to questions, never save on close
        window.Script.MessageSink = _ => { };
        window.View.EditorClipboard.InMemory = true;
        window.Show();
        window.View.DocumentCanvas.SetZoom(1);
        window.View.DocumentCanvas.GoToPage(0);
        Settle(window);
        return window;
    }

    private static void Settle(MainWindow w)
    {
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T Find<T>(MainWindow w, string name) where T : Control =>
        w.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? w.View.FindControl<T>(name) ?? throw new InvalidOperationException($"{name} not found");

    /// <summary>The swatch buttons of a palette (an items control, R4d).</summary>
    private static List<Button> Swatches(MainWindow w, string name) =>
        [.. Find<ItemsControl>(w, name).GetVisualDescendants().OfType<Button>().Where(b => b.Tag is RgbColor)];

    /// <summary>What a click does: the Click event, then the bound command.</summary>
    private static void Press(Button button)
    {
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        if (button.Command is { } command && command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point At(MainWindow w, Point2 page)
    {
        var dip = w.View.DocumentCanvas.PageTransform(0).PageToDip.Transform(page);
        return w.View.DocumentCanvas.TranslatePoint(new Point(dip.X, dip.Y), w)!.Value;
    }

    private static void Drag(MainWindow w, params Point2[] points)
    {
        w.MouseMove(At(w, points[0]));
        w.MouseDown(At(w, points[0]), MouseButton.Left);
        foreach (var p in points.Skip(1)) w.MouseMove(At(w, p));
        w.MouseUp(At(w, points[^1]), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(MainWindow w, Point2 p, MouseButton button = MouseButton.Left)
    {
        w.MouseMove(At(w, p));
        w.MouseDown(At(w, p), button);
        w.MouseUp(At(w, p), button);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point2[] Line(Point2 a, Point2 b, int n = 8) =>
        [.. Enumerable.Range(0, n).Select(i => new Point2(a.X + (b.X - a.X) * i / (n - 1), a.Y + (b.Y - a.Y) * i / (n - 1)))];

    private static IReadOnlyList<Annotation> Annotations(MainWindow w, int page = 0) => w.View.Document!.Session.Current.Pages[page].Annotations;

    // ----- layout (D13b–D13e) -----

    /// <summary>The visible children of a wrap panel: their rows and whether any reaches past the panel.</summary>
    private static (int Rows, bool Clipped) Rows(WrapPanel panel)
    {
        var shown = panel.Children.Where(c => c.IsVisible && c.Bounds.Width > 0).ToList();
        // A row starts where an item sits below the tops of the row before it (dividers are inset by their margin).
        int rows = 0;
        double bottom = double.NegativeInfinity;
        foreach (var c in shown.OrderBy(c => c.Bounds.Y))
        {
            if (c.Bounds.Y >= bottom - 0.5) { rows++; bottom = c.Bounds.Bottom; }
            else bottom = Math.Max(bottom, c.Bounds.Bottom);
        }
        bool clipped = shown.Any(c => c.Bounds.Right > panel.Bounds.Width + 0.5);
        return (rows, clipped);
    }

    private static void ShowBar(MainWindow w, string bar)
    {
        switch (bar)
        {
            case "Pen": w.View.SetTool(ToolKind.Pen); break;
            case "Highlighter": w.View.SetTool(ToolKind.FreeHighlight); break;
            case "Line": w.View.SetTool(ToolKind.Curve); break;
            case "Shape": w.View.SetTool(ToolKind.Shape); break;
            case "Eraser": w.View.SetTool(ToolKind.AreaEraser); break;
            case "Text": w.View.SetTool(ToolKind.Text); break;
        }
        Settle(w);
    }

    [Fact]
    public void TheRibbonAndEveryBarFitOneRowAt1200AndNothingIsCutAt720() => Headless.Run(() =>
    {
        foreach (double width in new[] { 1200.0, 720 })
        {
            var w = Window(width, 800);
            foreach (var tab in Enum.GetValues<RibbonTab>())
            {
                w.View.ShowRibbonTab(tab);
                Settle(w);
                var panel = Find<WrapPanel>(w, tab + "Tab");
                var (rows, clipped) = Rows(panel);
                Assert.False(clipped, $"{tab} tab cut at {width}");
                if (width == 1200) Assert.True(rows == 1, $"{tab} tab: {rows} rows at 1200");
            }
            foreach (var bar in new[] { "Pen", "Highlighter", "Line", "Shape", "Eraser", "Text" })
            {
                ShowBar(w, bar);
                var panel = bar switch
                {
                    "Eraser" => (WrapPanel)Find<Border>(w, "EraserBorder").Child!,
                    "Text" => Find<WrapPanel>(w, "TextStylePanel"),
                    _ => Find<WrapPanel>(w, "StylePanel"),
                };
                Assert.True(panel.IsEffectivelyVisible, bar);
                var (rows, clipped) = Rows(panel);
                Assert.False(clipped, $"{bar} bar cut at {width}");
                if (width == 1200) Assert.True(rows == 1, $"{bar} bar: {rows} rows at 1200, panel {panel.Bounds.Width}: " + string.Join(", ", panel.Children.Where(c => c.IsVisible).Select(c => $"{c.GetType().Name}{c.Name} {c.Bounds.X:0}+{c.Bounds.Width:0}@{c.Bounds.Y:0}")));
            }
            w.Close();
        }
    });

    [Fact]
    public void ABarChangeDoesNotMoveTheDocumentAt1200() => Headless.Run(() =>
    {
        var w = Window();
        double? top = null;
        foreach (var bar in new[] { "Pen", "Highlighter", "Line", "Shape", "Eraser", "Text" })
        {
            ShowBar(w, bar);
            double y = w.View.DocumentCanvas.TranslatePoint(new Point(0, 0), w)!.Value.Y;
            top ??= y;
            Assert.Equal(top.Value, y, 3);
        }
        w.Close();
    });

    // ----- ribbon, tools, bars -----

    [Fact]
    public void TabsToolButtonsAndTheAutomaticTabFollowTheViewModel() => Headless.Run(() =>
    {
        var w = Window();
        Assert.Equal(RibbonTab.Home, w.View.CurrentRibbonTab);
        Press(Find<RadioButton>(w, "PageTabButton"));
        Assert.Equal(RibbonTab.Page, w.View.CurrentRibbonTab);
        Assert.True(Find<WrapPanel>(w, "PageTab").IsVisible);
        Assert.False(Find<WrapPanel>(w, "HomeTab").IsVisible);
        w.View.SetTool(ToolKind.Pen); // a tool change shows 필기 (spec 3.2)
        Assert.Equal(RibbonTab.Home, w.View.CurrentRibbonTab);

        Press(Find<RadioButton>(w, "LassoToolButton"));
        Assert.Equal(ToolKind.Lasso, w.View.CurrentTool);
        Assert.True(Find<RadioButton>(w, "LassoToolButton").IsChecked);
        Assert.False(Find<RadioButton>(w, "PenToolButton").IsChecked);
        // The eraser button takes the eraser used last; the eraser bar picks one.
        Press(Find<RadioButton>(w, "EraserToolButton"));
        Assert.True(w.View.CurrentTool is ToolKind.StrokeEraser or ToolKind.AreaEraser);
        Press(Find<RadioButton>(w, "StrokeEraserButton"));
        Assert.Equal(ToolKind.StrokeEraser, w.View.CurrentTool);
        Assert.True(Find<Border>(w, "EraserBorder").IsVisible);
        Assert.False(Find<Border>(w, "StyleBar").IsVisible);
        // The shape menu chooses the shape and the tool.
        var menu = w.View.OpenShapeMenu();
        var ellipse = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header! == PdfNotes.Editor.Localization.Strings.Get("shape.ellipse"));
        ellipse.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Assert.Equal(ToolKind.Shape, w.View.CurrentTool);
        Assert.Equal(ShapeKind.Ellipse, w.View.ViewModel.Settings.ShapeKind);
        Assert.True(Find<RadioButton>(w, "ShapeToolButton").IsChecked);

        // A lasso result goes to the selection tool and marks the lasso button to come back to (spec 5.4).
        w.View.SetTool(ToolKind.Pen);
        Drag(w, Line(new(100, 100), new(200, 120)));
        w.View.SetTool(ToolKind.Lasso);
        Drag(w, new(60, 60), new(260, 60), new(260, 200), new(60, 200), new(60, 60));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ToolKind.Select, w.View.CurrentTool);
        Assert.Contains("return", Find<RadioButton>(w, "LassoToolButton").Classes);
        w.Close();
    });

    [Fact]
    public void TheStyleBarSetsColorWidthAndTransparencyAndKeepsUserColors() => Headless.Run(() =>
    {
        var w = Window();
        w.View.SetTool(ToolKind.Pen);
        Settle(w);
        // A basic swatch sets the pen color; the current one has the ring.
        var red = Swatches(w, "PalettePanel").Single(b => (RgbColor)b.Tag! == ColorPalette.Basic[2]);
        Press(red);
        Assert.Equal(ColorPalette.Basic[2], w.View.ViewModel.ToolStyles.Pen.Color);
        Assert.Contains("current", red.Classes);
        // The width field applies on Enter.
        var width = Find<TextBox>(w, "WidthBox");
        width.Focus();
        width.Text = "3.5";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3.5, w.View.ViewModel.ToolStyles.Pen.WidthPt, 3);
        var transparency = Find<TextBox>(w, "TransparencyBox");
        transparency.Focus();
        transparency.Text = "40";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(40, w.View.ViewModel.ToolStyles.Pen.TransparencyPercent, 3);
        // The + window adds a user color to every palette.
        var picker = w.View.CustomColorPickers["CustomColor"];
        picker.Code.Text = "#12AB34";
        Dispatcher.UIThread.RunJobs();
        var chosen = picker.SelectedColor; // the code through the color plane (HSV) may round by one
        Assert.True(Math.Abs(chosen.R - 0x12) <= 1 && Math.Abs(chosen.G - 0xAB) <= 1 && Math.Abs(chosen.B - 0x34) <= 1);
        Press(picker.Add);
        Settle(w);
        Assert.Equal(chosen, w.View.ViewModel.ToolStyles.Pen.Color);
        Assert.Contains(chosen, w.View.ViewModel.ToolStyles.UserColors);
        Assert.Single(Swatches(w, "RecentPanel"));

        // A drawn stroke has those values; selecting it lets the bar edit it in one Undo step.
        Drag(w, Line(new(100, 100), new(300, 160)));
        var ink = Assert.IsType<InkStroke>(Assert.Single(Annotations(w)));
        Assert.Equal(3.5, ink.WidthPt, 3);
        w.View.SetTool(ToolKind.Select);
        w.View.Selection.Select(ink.Id);
        Settle(w);
        Assert.True(Find<Border>(w, "StyleBar").IsEnabled);
        int steps = w.View.Document!.Session.UndoCount;
        Press(Swatches(w, "PalettePanel").Single(b => (RgbColor)b.Tag! == ColorPalette.Basic[6]));
        Assert.Equal(ColorPalette.Basic[6], ((InkStroke)Annotations(w)[0]).Color);
        Assert.Equal(steps + 1, w.View.Document.Session.UndoCount);
        w.Close();
    });

    [Fact]
    public void LineAndShapeOptionsAndTheHighlighterKindsAreOnTheBar() => Headless.Run(() =>
    {
        var w = Window();
        w.View.SetTool(ToolKind.Line);
        Settle(w);
        Assert.True(Find<StackPanel>(w, "LineOptionsPanel").IsVisible);
        Find<ComboBox>(w, "DashBox").SelectedIndex = 2;
        Assert.Equal(DashStyle.Dot, w.View.ViewModel.ToolStyles.LineOptions.Dash);
        w.View.SetTool(ToolKind.Shape);
        Settle(w);
        Assert.True(Find<StackPanel>(w, "ShapeOptionsPanel").IsVisible);
        Find<ComboBox>(w, "ShapeBorderBox").SelectedIndex = 2; // dashed border
        Assert.True(w.View.ViewModel.ToolStyles.Shape.BorderEnabled);
        w.View.SetTool(ToolKind.FreeHighlight);
        Settle(w);
        Assert.True(Find<StackPanel>(w, "MarkKindPanel").IsVisible);
        Press(Find<RadioButton>(w, "MarkUnderlineButton"));
        Assert.Equal(TextMarkupKind.Underline, w.View.ViewModel.ToolStyles.MarkKind);
        Assert.True(Find<ToggleButton>(w, "HighlighterSnapButton").IsVisible);
        w.Close();
    });

    [Fact]
    public void TheTextBarFormatsTheEditedBoxAndSelectedBoxes() => Headless.Run(() =>
    {
        var w = Window();
        w.View.SetTool(ToolKind.Text);
        Settle(w);
        Assert.True(Find<Border>(w, "TextStyleBorder").IsVisible);
        Click(w, new(100, 400));
        Assert.True(w.View.IsEditingText);
        w.KeyTextInput("abc");
        Dispatcher.UIThread.RunJobs();
        w.View.ActiveTextEditor!.Model!.SelectAll();
        var bold = Find<ToggleButton>(w, "TextBButton");
        bold.IsChecked = true; // what the click toggles first
        Press(bold);
        w.View.FinishTextEditing();
        var box = Assert.IsType<TextBoxAnnotation>(Assert.Single(Annotations(w)));
        Assert.All(box.Runs, r => Assert.True(r.Bold));
        // With the box selected (not edited) the bar edits the whole box: the fill target and a color.
        w.View.SetTool(ToolKind.Select);
        w.View.Selection.Select(box.Id);
        Settle(w);
        Press(Find<RadioButton>(w, "TextTargetFill"));
        var swatches = Find<WrapPanel>(w, "TextStylePanel").GetVisualDescendants().OfType<Button>().Where(b => b.Tag is RgbColor).ToList();
        Press(swatches.First(b => (RgbColor)b.Tag! == ColorPalette.Basic[4]));
        var filled = (TextBoxAnnotation)Annotations(w)[0];
        Assert.Equal(ColorPalette.Basic[4], filled.BoxStyle.BackgroundColor);
        Assert.True(filled.BoxStyle.BackgroundOpacity > 0);
        w.Close();
    });

    [Fact]
    public void FavoritesStoreAndApplyThePen() => Headless.Run(() =>
    {
        var w = Window();
        w.View.SetTool(ToolKind.Pen);
        w.View.ViewModel.ToolStyles.SetColor(ColorPalette.Basic[5]);
        w.View.ViewModel.ToolStyles.SetWidth(4);
        w.View.StoreFavorite(0); // 즐겨찾기 교체 (an empty slot stores it on a click too)
        Assert.NotNull(w.View.Favorites[0]);
        w.View.ViewModel.ToolStyles.SetColor(ColorPalette.Basic[0]);
        w.View.SetTool(ToolKind.Select);
        w.KeyPress(Key.D1, RawInputModifiers.Control, PhysicalKey.Digit1, "1"); // Ctrl+1 (spec 24.7)
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ToolKind.Pen, w.View.CurrentTool);
        Assert.Equal(ColorPalette.Basic[5], w.View.ViewModel.ToolStyles.Pen.Color);
        Assert.Equal(4, w.View.ViewModel.ToolStyles.Pen.WidthPt, 3);
        w.Close();
    });

    // ----- the page panel -----

    [Fact]
    public void ThePageListDrawsThumbnailsAndRunsThePageCommands() => Headless.Run(async () =>
    {
        string pdf = Path.Combine(Headless.TempFolder(), "pages.pdf");
        ProbeSamplePdf.Create(pdf, 3);
        var w = Window();
        w.Script.WindowOverride = window => true; // OK on every app window
        Assert.True(await w.View.OpenFileAsync(pdf));
        Settle(w);
        Assert.Equal(3, w.View.PageThumbnails.Count);
        await w.View.ViewModel.Pages.RenderThumbnailsAsync();
        Assert.All(w.View.PageThumbnails, t => Assert.IsAssignableFrom<Avalonia.Media.IImage>(t.Image));
        Assert.Equal(PagesViewModel.ThumbnailWidth, w.View.PageThumbnails[0].ImageWidth, 3);
        // A click on the second row goes to that page.
        w.View.PageListBox.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, w.View.ActivePageIndex);
        Assert.Equal(1, w.View.DocumentCanvas.CurrentPageIndex);
        // 삽입: the blank page dialog (OK with the active page's size) puts a page after it.
        Press(Find<Button>(w, "InsertPageButton"));
        Assert.Equal(4, w.View.Document!.Session.Current.Pages.Count);
        Assert.Equal(PageSourceKind.Generated, w.View.Document.Session.Current.Pages[2].Setup.Source.Kind);
        // Rotate, copy and paste, move, delete (one Undo step each).
        var ids = w.View.Document.Session.Current.Pages.Select(p => p.Id).ToArray();
        Press(Find<Button>(w, "RotateRightButton"));
        Assert.Equal(90, w.View.Document.Session.Current.Pages[2].Setup.Rotation);
        Assert.True(w.View.MovePagesTo([ids[0]], 4));
        Assert.Equal(ids[0], w.View.Document.Session.Current.Pages[^1].Id);
        w.View.ViewModel.Pages.SelectPages([ids[1]]);
        Press(Find<Button>(w, "CopyPageButton"));
        Assert.True(Find<Button>(w, "PastePageButton").IsEnabled);
        Press(Find<Button>(w, "PastePageButton"));
        Assert.Equal(5, w.View.Document.Session.Current.Pages.Count);
        w.View.ViewModel.Pages.SelectPages([ids[1]]);
        Press(Find<Button>(w, "DeletePageButton")); // asks first (PromptOverride says yes)
        Assert.Equal(4, w.View.Document.Session.Current.Pages.Count);
        Assert.DoesNotContain(w.View.Document.Session.Current.Pages, p => p.Id == ids[1]);
        // The context menu has the page commands.
        var menu = w.View.OpenPageMenu();
        Assert.Contains(menu.Items.OfType<MenuItem>(), i => (string)i.Header! == PdfNotes.Editor.Localization.Strings.Get("page.rotateRight"));
        menu.Close();
        // The handle hides and shows the list.
        Press(Find<Button>(w, "PagePanelHandle"));
        Assert.False(w.View.PagePanelVisible);
        Assert.Contains("closed", Find<Button>(w, "PagePanelHandle").Classes);
        Press(Find<Button>(w, "PagePanelHandle"));
        Assert.True(w.View.PagePanelVisible);
        w.Close();
    });

    [Fact]
    public void ShrinkResizeExportAndClearNotesUseTheirWindows() => Headless.Run(async () =>
    {
        string pdf = Path.Combine(Headless.TempFolder(), "pages.pdf");
        ProbeSamplePdf.Create(pdf, 2);
        var w = Window();
        Assert.True(await w.View.OpenFileAsync(pdf));
        Settle(w);
        // 내용 축소 at 50 %.
        w.Script.WindowOverride = window => { if (window is PageTransformDialog d) d.PercentBox.Text = "50"; return true; };
        Assert.True(w.View.ShowPageTransform(resize: false));
        Assert.NotNull(w.View.Document!.Session.Current.Pages[0].Setup.Content);
        // 페이지 크기 to A5.
        w.Script.WindowOverride = window => { if (window is PageTransformDialog d) { d.WidthBox.Text = "148"; d.HeightBox.Text = "210"; } return true; };
        Assert.True(w.View.ShowPageTransform(resize: true));
        Assert.Equal(Units.MillimetersToPoints(148), w.View.Document.Session.Current.Pages[0].Setup.Frame.DisplayWidthPt, 1);
        // 이 페이지의 필기 삭제 removes the notes (after the question).
        w.View.SetTool(ToolKind.Pen);
        Drag(w, Line(new(50, 50), new(120, 90)));
        Assert.NotEmpty(Annotations(w));
        Assert.True(w.View.ClearSelectedPageNotes());
        Assert.Empty(Annotations(w));
        // 내보내기: the range window, then the file name.
        string target = Path.Combine(Headless.TempFolder(), "out.pdf");
        w.Script.SaveDialogOverride = _ => target;
        w.Script.WindowOverride = window => { if (window is PageExportDialog d) d.RangeBox.Text = "1-2"; return true; };
        Assert.Equal(1, await w.View.ExportPagesAsync(allPages: true));
        Assert.True(File.Exists(target));
        w.Close();
    });

    [Fact]
    public void PdfPagesAreImportedThroughTheRangeWindow() => Headless.Run(async () =>
    {
        string folder = Headless.TempFolder();
        string other = Path.Combine(folder, "other.pdf");
        ProbeSamplePdf.Create(other, 4);
        var w = Window();
        w.Script.OpenDialogOverride = () => [other];
        w.Script.WindowOverride = window => { if (window is PdfImportDialog d) d.RangeBox.Text = "2-3"; return true; };
        int before = w.View.Document!.Session.Current.Pages.Count;
        Assert.True(await w.View.ImportPdfPagesAsync());
        Assert.Equal(before + 2, w.View.Document.Session.Current.Pages.Count);
        w.Close();
    });

    // ----- search, outline, links -----

    [Fact]
    public void SearchOutlineAndLinksWork() => Headless.Run(async () =>
    {
        string pdf = Path.Combine(Headless.TempFolder(), "linked.pdf");
        ProbeSamplePdf.CreateLinked(pdf, 5);
        var w = Window();
        Assert.True(await w.View.OpenFileAsync(pdf));
        Settle(w);
        // Ctrl+F opens the bar; the query searches after the delay.
        w.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.View.IsSearchOpen);
        Assert.True(Find<Border>(w, "SearchBar").IsVisible);
        w.View.SearchField.Text = "Target line";
        Headless.Pump(() => !w.View.ViewModel.Search.IsSearching && w.View.ViewModel.Search.Hits.Count > 0);
        await w.View.ViewModel.Search.Running;
        Assert.Equal(5, w.View.ViewModel.Search.Hits.Count);
        Assert.Equal("1/5", Find<TextBlock>(w, "SearchCount").Text);
        Press(Find<Button>(w, "SearchNextButton"));
        Assert.Equal(1, w.View.ViewModel.Search.Index);
        Press(Find<Button>(w, "SearchCloseButton"));
        Assert.False(w.View.IsSearchOpen);
        // The outline: toggle on, a tree of the bookmarks; following one jumps there.
        Assert.True(Find<ToggleButton>(w, "OutlineButton").IsEnabled);
        w.View.SetOutlineVisible(true);
        Settle(w);
        Assert.True(w.View.OutlineShown);
        var second = OutlineViewModel.All(w.View.Outline).First(i => i.Title == "2장");
        int beforeJump = w.View.DocumentCanvas.CurrentPageIndex;
        w.View.FollowOutline(second);
        Assert.Equal(3, w.View.DocumentCanvas.CurrentPageIndex);
        Assert.True(w.View.ViewModel.Navigation.CanGoBack);
        w.KeyPress(Key.Left, RawInputModifiers.Alt, PhysicalKey.ArrowLeft, null); // back (spec 24.3)
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(beforeJump, w.View.DocumentCanvas.CurrentPageIndex);
        // Links of the first page are found under the pointer.
        Assert.NotEmpty(w.View.PageLinks(0));
        w.Close();
    });

    // ----- notes, images, menus, clipboard -----

    [Fact]
    public void AStickyNoteCardIsTypedAndBecomesOneNote() => Headless.Run(() =>
    {
        var w = Window();
        w.View.SetTool(ToolKind.Note);
        Click(w, new(200, 200));
        Assert.NotNull(w.View.OpenNoteAnnotation);
        Assert.NotNull(w.View.NoteTextBox);
        Assert.Empty(Annotations(w)); // not in the document while it is open and empty
        w.View.NoteTextBox!.Text = "메모 내용";
        int steps = w.View.Document!.Session.UndoCount;
        w.View.CloseNote();
        var note = Assert.IsType<StickyNote>(Assert.Single(Annotations(w)));
        Assert.Equal("메모 내용", note.Text);
        Assert.Equal(steps + 1, w.View.Document.Session.UndoCount);
        w.Close();
    });

    [Fact]
    public void HidingNotesAndInsertingAnImage() => Headless.Run(() =>
    {
        var w = Window();
        w.View.SetTool(ToolKind.Pen);
        Drag(w, Line(new(100, 100), new(200, 150)));
        Press(Find<RadioButton>(w, "ViewTabButton"));
        var hide = Find<ToggleButton>(w, "HideNotesButton");
        hide.IsChecked = true;
        Press(hide);
        Assert.True(w.View.NotesHidden);
        Assert.True(Find<TextBlock>(w, "NotesHiddenText").IsVisible);
        Assert.Equal(ToolKind.Select, w.View.CurrentTool);
        // Inserting shows the notes again (spec 24.10) and selects the new image.
        using var bitmap = new SkiaSharp.SKBitmap(40, 20);
        bitmap.Erase(SkiaSharp.SKColors.OrangeRed);
        using var png = SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        Assert.True(w.View.InsertImage(png.ToArray()));
        Assert.False(w.View.NotesHidden);
        var image = Assert.IsType<ImageAnnotation>(Annotations(w).Last());
        Assert.Equal([image.Id], w.View.Selection.SelectedIds);
        w.Close();
    });

    [Fact]
    public void TheCanvasMenuAndTheClipboard() => Headless.Run(() =>
    {
        var w = Window();
        w.View.SetTool(ToolKind.Pen);
        Drag(w, Line(new(100, 100), new(300, 160)));
        var ink = Annotations(w)[0];
        // On an empty place: a new text box, the quick tools and paste.
        var empty = w.View.DocumentCanvas.PageTransform(0).PageToDip.Transform(new Point2(400, 600));
        var menu = w.View.OpenAnnotationMenu(empty)!;
        Assert.Contains(menu.Items.OfType<MenuItem>(), i => (string)i.Header! == PdfNotes.Editor.Localization.Strings.Get("menu.addTextBox"));
        menu.Close();
        // On the stroke: it is selected; copy, then paste puts a second one (system clipboard of the window).
        var on = w.View.DocumentCanvas.PageTransform(0).PageToDip.Transform(new Point2(200, 130));
        menu = w.View.OpenAnnotationMenu(on)!;
        Assert.Equal([ink.Id], w.View.Selection.SelectedIds);
        var copy = menu.Items.OfType<MenuItem>().First(i => (string)i.Header! == PdfNotes.Editor.Localization.Strings.Get("command.copy"));
        copy.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Assert.True(w.View.PasteSelection());
        Assert.Equal(2, Annotations(w).Count);
        // Plain text pastes as a text box.
        w.View.EditorClipboard.SetText("붙여 넣은 글");
        Assert.True(w.View.PasteSelection(new Point2(200, 400)));
        w.View.FinishTextEditing();
        Assert.Contains(Annotations(w), a => a is TextBoxAnnotation { Text: "붙여 넣은 글" });
        // Arrows move the selection (spec 5.3).
        w.View.SetTool(ToolKind.Select);
        w.View.Selection.Select(ink.Id);
        w.View.DocumentCanvas.Focus();
        var before = w.View.Document!.Session.Current.FindAnnotation(ink.Id)!.Annotation;
        int steps = w.View.Document.Session.UndoCount;
        w.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        w.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(steps + 1, w.View.Document.Session.UndoCount);
        var bounds = (Before: AnnotationGeometry.Bounds([before]), After: AnnotationGeometry.Bounds([w.View.Document.Session.Current.FindAnnotation(ink.Id)!.Annotation]));
        Assert.True(bounds.After.X > bounds.Before.X, $"{bounds}");
        w.Close();
    });

    [Fact]
    public void TheFirstKeyOnASelectedBoxComposesThroughTheCanvas() => Headless.Run(() =>
    {
        var w = Window();
        w.View.SetTool(ToolKind.Text);
        Click(w, new(100, 400));
        w.KeyTextInput("가");
        w.View.FinishTextEditing();
        var box = Assert.IsType<TextBoxAnnotation>(Assert.Single(Annotations(w)));
        // The text tool selects the box (a visit of the selection tool that returns to the text tool, spec 5.4).
        Thread.Sleep(700);
        Click(w, new Point2(box.Transform.OffsetX + 10, box.Transform.OffsetY + 8));
        Assert.Equal(ToolKind.Select, w.View.CurrentTool);
        Assert.Equal([box.Id], w.View.Selection.SelectedIds);
        w.View.DocumentCanvas.Focus();
        Dispatcher.UIThread.RunJobs();
        // The IME composes the first syllable on the canvas: the box opens with it, the document does not have it yet.
        w.View.DocumentCanvas.ProbePreedit("ㅎ");
        Assert.True(w.View.IsEditingText);
        Assert.Equal("ㅎ", w.View.ActiveTextEditor!.Model!.Composition);
        w.View.DocumentCanvas.ProbePreedit("하");
        Assert.Equal("가", ((TextBoxAnnotation)Annotations(w)[0]).Text);
        Assert.True(w.View.DocumentCanvas.IsFocused);
        // The commit goes into the box and the editor takes the keyboard for the rest.
        w.KeyTextInput("하");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("가하", ((TextBoxAnnotation)Annotations(w)[0]).Text);
        Assert.True(w.View.ActiveTextEditor!.IsFocused);
        w.KeyTextInput("다");
        Dispatcher.UIThread.RunJobs();
        w.View.FinishTextEditing();
        Assert.Equal("가하다", ((TextBoxAnnotation)Annotations(w)[0]).Text);
        w.Close();
    });

    /// <summary>The whole frame drawn (with PDFNOTES_SNAPSHOT=folder it is saved there, one picture per tab and bar, for a look).</summary>
    [Fact]
    public void TheFrameIsDrawn() => Headless.Run(async () =>
    {
        string pdf = Path.Combine(Headless.TempFolder(), "look.pdf");
        ProbeSamplePdf.CreateLinked(pdf, 4);
        var w = Window(1200, 800);
        Assert.True(await w.View.OpenFileAsync(pdf));
        await w.View.ViewModel.Pages.RenderThumbnailsAsync();
        w.View.DocumentCanvas.PlanRendersNow();
        Headless.Pump(() => w.View.DocumentCanvas.Queue is { IsIdle: true });
        string? folder = Environment.GetEnvironmentVariable("PDFNOTES_SNAPSHOT");
        void Shot(string name)
        {
            Settle(w);
            var frame = w.CaptureRenderedFrame()!;
            Assert.True(frame.PixelSize.Width >= 1200);
            if (folder is not null) { Directory.CreateDirectory(folder); frame.Save(Path.Combine(folder, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        }
        foreach (var (tab, tool) in new[] { (RibbonTab.Home, ToolKind.Pen), (RibbonTab.File, ToolKind.FreeHighlight), (RibbonTab.Page, ToolKind.Shape), (RibbonTab.View, ToolKind.Text) })
        {
            w.View.SetTool(tool);
            w.View.ShowRibbonTab(tab);
            Shot($"{tab}-{tool}");
        }
        w.View.OpenSearch();
        w.View.SetOutlineVisible(true);
        w.View.SetTool(ToolKind.AreaEraser);
        Shot("search-outline-eraser");
        w.Close();
    });

    /// <summary>
    /// Thumbnails on a scaled screen show the whole page (user report 2026-11-04 at 150 %: only the top-left part): the
    /// bitmap is 96 DPI, so the Image control's source rectangle in pixels is the whole picture.
    /// </summary>
    [Fact]
    public void AThumbnailOnAScaledScreenIsTheWholePage() => Headless.Run(async () =>
    {
        string pdf = Path.Combine(Headless.TempFolder(), "t.pdf");
        ProbeSamplePdf.CreateLinked(pdf, 1);
        var w = Window();
        Assert.True(await w.View.OpenFileAsync(pdf));
        var doc = w.View.Document!;
        var page = doc.Session.Current.Pages[0];
        double zoom = 132 / (page.Setup.Frame.DisplayWidthPt * Units.DipsPerPoint);
        using var bitmap = await PageSnapshots.RenderAsync(doc, doc.Session.Current, page, zoom, 1.5, false, false, CancellationToken.None);
        Assert.Equal(198, bitmap!.PixelSize.Width);
        Assert.Equal(bitmap.PixelSize.Width, bitmap.Size.Width, 3); // pixels = the size the Image control crops by
        w.Close();
    });

    [Fact]
    public void TheEditedBoxHasItsFrameAndTheAlignmentListHasNoSharedControls() => Headless.Run(() =>
    {
        var w = Window();
        w.View.SetTool(ToolKind.Text);
        Click(w, new(100, 400));
        w.KeyTextInput("frame");
        Dispatcher.UIThread.RunJobs();
        var box = w.View.EditingText!;
        // The frame's top edge just left of the box's top-right corner: the 0.5 DIP accent line over a white halo.
        var corner = At(w, new Point2(box.Transform.OffsetX + box.WidthPt - 4, box.Transform.OffsetY));
        using var frame = w.CaptureRenderedFrame()!;
        using var copy = new Avalonia.Media.Imaging.WriteableBitmap(frame.PixelSize, frame.Dpi, Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var l = copy.Lock()) frame.CopyPixels(new PixelRect(frame.PixelSize), l.Address, l.RowBytes * l.Size.Height, l.RowBytes);
        using var locked = copy.Lock();
        var pixels = new byte[locked.RowBytes * locked.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(locked.Address, pixels, 0, pixels.Length);
        bool blue = false;
        for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
            {
                int at = ((int)Math.Round(corner.Y) + dy) * locked.RowBytes + ((int)Math.Round(corner.X) + dx) * 4;
                blue |= Math.Abs(pixels[at] - pixels[at + 2]) > 8; // colored (the frame line), not white paper or black text; the frame may be RGBA or BGRA
            }
        Assert.True(blue, $"no edit frame at {corner}");
        Assert.All(Find<ComboBox>(w, "TextAlignmentBox").Items, item => Assert.IsType<string>(item));
        w.Close();
    });

    [Fact]
    public void ThePenToolHasAPenCursor() => Headless.Run(() =>
    {
        using var bitmap = CanvasCursors.DrawPen(32);
        Assert.Equal(32, bitmap.PixelSize.Width);
        Assert.NotNull(CanvasCursors.Pen(1.5));
    });

    // ----- windows, cursors -----

    [Fact]
    public void TheSettingsWindowStoresItsValuesAndCancelLeavesThem() => Headless.Run(() =>
    {
        var settings = new AppSettings();
        var w = Window(settings: settings);
        w.Script.WindowOverride = window => { ((SettingsDialog)window).Categories.SelectedIndex = 0; window.GetLogicalDescendants().OfType<CheckBox>().First(c => c.Name == "PageDragBox").IsChecked = true; return true; };
        Assert.True(w.View.ShowSettings());
        Assert.True(settings.PageDrag);
        w.Script.WindowOverride = window => { window.GetLogicalDescendants().OfType<CheckBox>().First(c => c.Name == "PageDragBox").IsChecked = false; return false; };
        Assert.False(w.View.ShowSettings());
        Assert.True(settings.PageDrag);
        w.Close();
    });

    [Fact]
    public void ANewNoteUsesItsWindow() => Headless.Run(async () =>
    {
        var w = Window();
        w.Script.PromptOverride = (_, _) => Answer.No; // the empty note is not kept
        w.Script.WindowOverride = window => { if (window is NewNoteDialog d) d.LandscapeButton.IsChecked = true; return true; };
        Assert.True(await w.View.NewNoteAsync());
        var setup = w.View.Document!.Session.Current.Pages[0].Setup;
        Assert.True(setup.WidthPt > setup.HeightPt);
        w.Close();
    });

    [Fact]
    public void TheRotateAndLockCursorsAreDrawn() => Headless.Run(() =>
    {
        foreach (bool padlock in new[] { false, true })
        {
            using var bitmap = CanvasCursors.Draw(padlock, 32);
            Assert.Equal(32, bitmap.PixelSize.Width);
            using var copy = new Avalonia.Media.Imaging.WriteableBitmap(bitmap.PixelSize, bitmap.Dpi, Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var locked = copy.Lock()) bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), locked.Address, locked.RowBytes * locked.Size.Height, locked.RowBytes);
            using var again = copy.Lock();
            var pixels = new byte[again.RowBytes * again.Size.Height];
            System.Runtime.InteropServices.Marshal.Copy(again.Address, pixels, 0, pixels.Length);
            int opaque = Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] > 200);
            Assert.InRange(opaque, 60, 32 * 32 - 100); // a shape, not empty and not a filled square
        }
        Assert.NotNull(CanvasCursors.Rotate(1.5));
        Assert.NotNull(CanvasCursors.Lock(1));
    });
}
