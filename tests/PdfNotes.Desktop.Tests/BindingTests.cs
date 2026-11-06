using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Desktop.Views;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;
using PdfNotes.ViewModels;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// Roadmap P4.5 R4a–R4d: the window's title, status line, ribbon tabs, the quick and 파일 commands, 보기, the status bar
/// and the 필기 tab with its bars are bound to the view model. Each test changes the view model (or runs its command) and checks the control, with no
/// window code in between.
/// </summary>
public sealed class BindingTests
{
    private static MainWindow Window()
    {
        var window = new MainWindow(TestWindows.App(new AppSettings(), Headless.TempFolder()), TestWindows.Dialogs) { Width = 1200, Height = 1000 };
        window.Script.PromptOverride = (_, buttons) => buttons == AskButtons.YesNo ? Answer.Yes : Answer.No;
        window.Script.MessageSink = _ => { };
        window.EditorClipboard.InMemory = true;
        window.Show();
        window.DocumentCanvas.SetZoom(1);
        window.DocumentCanvas.GoToPage(0);
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
        w.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? w.FindControl<T>(name) ?? throw new InvalidOperationException($"{name} not found");

    private static Avalonia.Point At(MainWindow w, Point2 page)
    {
        var dip = w.DocumentCanvas.PageTransform(0).PageToDip.Transform(page);
        return w.DocumentCanvas.TranslatePoint(new Avalonia.Point(dip.X, dip.Y), w)!.Value;
    }

    private static void Stroke(MainWindow w)
    {
        w.SetTool(ToolKind.Pen);
        Point2[] points = [.. Enumerable.Range(0, 8).Select(i => new Point2(80 + 180 * i / 7.0, 100 + 30 * i / 7.0))];
        w.MouseMove(At(w, points[0]));
        w.MouseDown(At(w, points[0]), MouseButton.Left);
        foreach (var p in points.Skip(1)) w.MouseMove(At(w, p));
        w.MouseUp(At(w, points[^1]), MouseButton.Left);
        Settle(w);
    }

    [Fact]
    public void TheTitleAndTheStatusLineFollowTheViewModel() => Headless.Run(() =>
    {
        var w = Window();
        Assert.Equal(w.ViewModel.Title, w.Title);
        w.ViewModel.Status = "바인딩 확인"; // not UI text (D14e)
        Settle(w);
        Assert.Equal("바인딩 확인", Find<TextBlock>(w, "StatusText").Text); // not UI text (D14e)
        Stroke(w);
        Assert.Contains(" *", w.Title); // the unsaved mark comes from the view model's title
        w.Close();
    });

    [Fact]
    public void UndoAndRedoAreEnabledByTheirCommandsAndNameTheStep() => Headless.Run(() =>
    {
        var w = Window();
        var undo = Find<Button>(w, "UndoButton");
        var redo = Find<Button>(w, "RedoButton");
        Assert.False(undo.IsEffectivelyEnabled);
        Assert.False(redo.IsEffectivelyEnabled);
        Stroke(w);
        Assert.True(undo.IsEffectivelyEnabled);
        Assert.Equal(w.ViewModel.UndoTip, ToolTip.GetTip(undo));
        w.ViewModel.UndoCommand.Execute();
        Settle(w);
        Assert.Empty(w.Document!.Session.Current.Pages[0].Annotations);
        Assert.False(undo.IsEffectivelyEnabled);
        Assert.True(redo.IsEffectivelyEnabled);
        w.Close();
    });

    [Fact]
    public void TheRibbonTabsFollowTheViewModel() => Headless.Run(() =>
    {
        var w = Window();
        foreach (var tab in Enum.GetValues<RibbonTab>())
        {
            w.ViewModel.Ribbon.Current = tab;
            Settle(w);
            foreach (var other in Enum.GetValues<RibbonTab>())
            {
                Assert.Equal(other == tab, Find<WrapPanel>(w, other + "Tab").IsVisible);
                Assert.Equal(other == tab, Find<RadioButton>(w, other + "TabButton").IsChecked);
            }
        }
        // A tab head runs the view model's command.
        var head = Find<RadioButton>(w, "FileTabButton");
        head.Command!.Execute(head.CommandParameter);
        Assert.Equal(RibbonTab.File, w.ViewModel.Ribbon.Current);
        w.Close();
    });

    [Fact]
    public void TheRecentButtonIsOffWhileTheListIsEmpty() => Headless.Run(() =>
    {
        var w = Window();
        var recent = Find<Button>(w, "RecentButton");
        Assert.False(recent.IsEffectivelyEnabled);
        w.ViewModel.Recent.Add(Path.Combine(Path.GetTempPath(), "somewhere.pdf"));
        Settle(w);
        Assert.True(recent.IsEffectivelyEnabled);
        w.ViewModel.Recent.Remove(Path.Combine(Path.GetTempPath(), "somewhere.pdf"));
        Settle(w);
        Assert.False(recent.IsEffectivelyEnabled);
        w.Close();
    });

    [Fact]
    public void TheStatusBarAndTheViewTabFollowTheView() => Headless.Run(() =>
    {
        var w = Window();
        var navigation = w.ViewModel.Navigation;
        navigation.SetZoom(2);
        Settle(w);
        Assert.Equal(navigation.ZoomText, Find<TextBlock>(w, "ZoomText").Text);
        Assert.Equal(navigation.ZoomText, Find<ComboBox>(w, "ZoomBox").Text);
        Assert.Equal(navigation.PageCountText, Find<TextBlock>(w, "PageCountText").Text);

        navigation.SinglePageCommand.Execute();
        Settle(w);
        Assert.True(Find<ToggleButton>(w, "SinglePageButton").IsChecked);
        Assert.False(Find<ToggleButton>(w, "ContinuousButton").IsChecked);
        navigation.ContinuousCommand.Execute();
        Settle(w);
        Assert.True(Find<ToggleButton>(w, "ContinuousButton").IsChecked);

        w.ViewModel.HideNotesCommand.Execute();
        Settle(w);
        Assert.True(Find<ToggleButton>(w, "HideNotesButton").IsChecked);
        Assert.True(Find<TextBlock>(w, "NotesHiddenText").IsVisible);
        w.ViewModel.HideNotesCommand.Execute();
        Settle(w);
        Assert.False(Find<TextBlock>(w, "NotesHiddenText").IsVisible);
        w.Close();
    });

    [Fact]
    public void ABoxBeingTypedInKeepsItsTextUntilEnterOrEsc() => Headless.Run(() =>
    {
        var w = Window();
        var navigation = w.ViewModel.Navigation;
        var zoomBox = Find<ComboBox>(w, "ZoomBox");
        zoomBox.Text = "15"; // the user typing
        navigation.SetZoom(1.5); // the view changes meanwhile (Ctrl+wheel)
        Settle(w);
        Assert.Equal("15", zoomBox.Text);
        Assert.Equal(navigation.ZoomText, Find<TextBlock>(w, "ZoomText").Text);
        navigation.CommitZoomInput(); // Enter
        Settle(w);
        Assert.Equal(0.15, w.DocumentCanvas.Zoom, 3);
        Assert.Equal(navigation.ZoomText, zoomBox.Text);

        var pageBox = Find<TextBox>(w, "PageBox");
        pageBox.Text = "abc";
        navigation.CommitPageInput(); // not a number: ignored, the box shows the page again
        Settle(w);
        Assert.Equal(navigation.PageNumberText, pageBox.Text);
        w.Close();
    });

    private static void ClickOn(MainWindow w, Control control)
    {
        var center = control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), w)!.Value;
        w.MouseMove(center);
        w.MouseDown(center, MouseButton.Left);
        w.MouseUp(center, MouseButton.Left);
        Settle(w);
    }

    [Fact]
    public void ClickedToolButtonsShowTheToolTheViewModelChose() => Headless.Run(() =>
    {
        var w = Window();
        var pen = Find<RadioButton>(w, "PenToolButton");
        var lasso = Find<RadioButton>(w, "LassoToolButton");
        ClickOn(w, lasso);
        Assert.Equal(ToolKind.Lasso, w.CurrentTool);
        Assert.True(lasso.IsChecked);
        Assert.False(pen.IsChecked);
        // The eraser bar picks the mode; the eraser button stays on for either.
        ClickOn(w, Find<RadioButton>(w, "EraserToolButton"));
        ClickOn(w, Find<RadioButton>(w, "AreaEraserButton"));
        Assert.Equal(ToolKind.AreaEraser, w.CurrentTool);
        Assert.True(Find<RadioButton>(w, "EraserToolButton").IsChecked);
        Assert.True(Find<RadioButton>(w, "AreaEraserButton").IsChecked);
        Assert.False(Find<RadioButton>(w, "StrokeEraserButton").IsChecked);
        // A tool chosen elsewhere (a shortcut) moves the buttons too.
        w.SetTool(ToolKind.Pen);
        Settle(w);
        Assert.True(pen.IsChecked);
        Assert.False(Find<RadioButton>(w, "EraserToolButton").IsChecked);
        Assert.True(Find<Border>(w, "StyleBar").IsVisible);
        Assert.False(Find<Border>(w, "EraserBorder").IsVisible);
        w.Close();
    });

    [Fact]
    public void TheStyleBarFollowsTheToolAndKeepsWhatIsBeingTyped() => Headless.Run(() =>
    {
        var w = Window();
        var styles = w.ViewModel.ToolStyles;
        w.SetTool(ToolKind.Pen);
        styles.SetWidth(2.5);
        Settle(w);
        var box = Find<TextBox>(w, "WidthBox");
        Assert.Equal("2.5", box.Text);
        Assert.Equal(2.5, Find<Slider>(w, "WidthSlider").Value, 3);
        box.Text = "7"; // the user typing
        styles.SetColor(ColorPalette.Basic[3]); // the style changes meanwhile
        Settle(w);
        Assert.Equal("7", box.Text);
        styles.CommitWidth(); // leaving the box
        Settle(w);
        Assert.Equal(7, styles.Pen.WidthPt, 3);
        Assert.Equal("7", box.Text);
        // The line tool shows its options; the shape button draws the shape group's tool.
        w.SetTool(ToolKind.Line);
        Settle(w);
        Assert.True(Find<StackPanel>(w, "LineOptionsPanel").IsVisible);
        Assert.False(Find<StackPanel>(w, "ShapeOptionsPanel").IsVisible);
        Assert.Equal(styles.LineBar.DashIndex, Find<ComboBox>(w, "DashBox").SelectedIndex);
        Assert.Equal(PdfNotes.Desktop.Themes.AppIcon.ShapeOutline(ToolKind.Line, w.ViewModel.Settings.ShapeKind), Find<PdfNotes.Desktop.Themes.IconLabel>(w, "ShapeToolLabel").Outline);
        // The text tool swaps the style bar for the text bar.
        w.SetTool(ToolKind.Text);
        Settle(w);
        Assert.True(Find<Border>(w, "TextStyleBorder").IsVisible);
        Assert.False(Find<Border>(w, "StyleBar").IsVisible);
        w.Close();
    });

    [Fact]
    public void FavoriteButtonsFollowTheirSlots() => Headless.Run(() =>
    {
        var w = Window();
        Button Favorite(int i) => Find<ItemsControl>(w, "FavoritesPanel").GetVisualDescendants().OfType<Button>().ElementAt(i);
        Assert.Equal(w.ViewModel.Settings.Favorites.Length, Find<ItemsControl>(w, "FavoritesPanel").GetVisualDescendants().OfType<Button>().Count());
        w.ViewModel.Favorites.Clear(1); // the default slots hold favorites
        Settle(w);
        Assert.Null(Favorite(1).GetVisualDescendants().OfType<PdfNotes.Desktop.Themes.FavoriteMark>().Single().Favorite);
        w.SetTool(ToolKind.Pen);
        w.ViewModel.ToolStyles.SetWidth(3);
        Favorite(1).Command!.Execute(null); // an empty favorite stores the pen
        Settle(w);
        Assert.NotNull(Favorite(1).GetVisualDescendants().OfType<PdfNotes.Desktop.Themes.FavoriteMark>().Single().Favorite);
        Assert.Equal(w.ViewModel.Favorites.Tip(w.ViewModel.Settings.Favorites[1], 1), ToolTip.GetTip(Favorite(1)));
        w.ViewModel.Display.Apply(w.ViewModel.Display.Stored with { Favorites = false });
        Settle(w);
        Assert.False(Find<StackPanel>(w, "FavoritesGroup").IsVisible);
        w.Close();
    });
}
