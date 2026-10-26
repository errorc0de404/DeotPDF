using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.App.Themes;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App.Views;

public sealed record PageTransformRequest(PageApplyScope Scope, double Percent, double WidthMm, double HeightMm, PageResizeMode Mode, int Alignment)
{
    public Point2? FreePosition { get; init; }
    public Guid[]? TargetPageIds { get; init; }
}

/// <summary>Shared toolbar/panel dialog. Opening/cancelling does not edit the document or finish an active editor.</summary>
public sealed class PageTransformDialog : Window
{
    private readonly UniformGrid scope = new() { Name = "ScopeBox", Rows = 3, Columns = 1 };
    private readonly ComboBox preset = new() { Name = "PresetBox" }, mode = new() { Name = "ModeBox" };
    private PageApplyScope selectedScope;
    private readonly TextBox percent = new() { Name = "PercentBox", Text = "80" }, width = new() { Name = "WidthBox" }, height = new() { Name = "HeightBox" };
    private readonly TextBlock summary = new(), error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly FrameworkElement resizeChoice;
    private int alignment = 4;
    private readonly bool resize;
    private readonly Func<PageApplyScope, PageSetup[]> targets;
    private readonly PagePlacementPreview preview;
    private readonly RadioButton free = new() { Name = "FreePlacement", Content = Strings.Get("transform.free"), GroupName = "Placement", Margin = new Thickness(3, 8, 3, 3) };
    public PageTransformRequest? Result { get; private set; }

    public PageTransformDialog(bool resize, PageSetup active, Func<PageApplyScope, PageSetup[]> targets, ImageSource? image = null)
    {
        Theme.Attach(this);
        this.resize = resize; this.targets = targets;
        Title = resize ? Strings.Get("transform.resizeTitle") : Strings.Get("transform.shrinkTitle");
        ScreenArea.Fit(this, 930, 760, 760, 560); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme.UseDialogTokens(this);
        error.SetResourceReference(TextBlock.ForegroundProperty, "Text.Danger");
        // D13c: the settings scroll on the left, the preview on the right, and the dialog buttons below both (never scrolled out of view).
        var root = new Grid { Margin = new Thickness(20) }; Content = root;
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) }); root.ColumnDefinitions.Add(new ColumnDefinition());
        root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; root.Children.Add(scroll);
        preview = new PagePlacementPreview(active, image, resize) { Name = "PlacementPreview", Margin = new Thickness(20, 0, 0, 0) };
        Grid.SetColumn(preview, 1); root.Children.Add(preview);
        void Row(string name, FrameworkElement field)
        {
            panel.Children.Add(new TextBlock { Text = name, Margin = new Thickness(0, 8, 0, 4) });
            panel.Children.Add(field);
        }
        int scopeIndex = 0;
        foreach (string name in new[] { Strings.Get("transform.currentPage"), Strings.Get("transform.selectedPages"), Strings.Get("transform.allPages") })
        {
            var value = (PageApplyScope)scopeIndex++;
            var button = new RadioButton { Content = name, Tag = value, GroupName = "Scope", IsChecked = value == PageApplyScope.Current, VerticalContentAlignment = VerticalAlignment.Center };
            button.Checked += (_, _) => { selectedScope = value; Refresh(); };
            scope.Children.Add(button);
        }
        Row(Strings.Get("transform.scope"), scope); scope.Height = 84;
        width.Text = Units.PointsToMillimeters(active.Frame.DisplayWidthPt).ToString("0.######", CultureInfo.CurrentCulture);
        height.Text = Units.PointsToMillimeters(active.Frame.DisplayHeightPt).ToString("0.######", CultureInfo.CurrentCulture);
        if (resize)
        {
            foreach (string name in new[] { Strings.Get("transform.custom"), Strings.Get("paper.a4Portrait"), Strings.Get("paper.a4Landscape"), Strings.Get("paper.a5Portrait"), Strings.Get("paper.a5Landscape"), Strings.Get("paper.letterPortrait"), Strings.Get("paper.letterLandscape") }) preset.Items.Add(name);
            preset.SelectedIndex = 0; Row(Strings.Get("transform.size"), preset); Row(Strings.Get("transform.width"), width); Row(Strings.Get("transform.height"), height);
            preset.SelectionChanged += (_, _) =>
            {
                var size = preset.SelectedIndex switch { 1 => (210.0, 297.0), 2 => (297.0, 210.0), 3 => (148.0, 210.0), 4 => (210.0, 148.0), 5 => (215.9, 279.4), 6 => (279.4, 215.9), _ => (0.0, 0.0) };
                if (size.Item1 == 0) return; width.Text = size.Item1.ToString(CultureInfo.CurrentCulture); height.Text = size.Item2.ToString(CultureInfo.CurrentCulture);
            };
        }
        else Row(Strings.Get("transform.ratio"), percent);
        var choice = new StackPanel(); resizeChoice = choice;
        choice.Children.Add(new TextBlock { Text = Strings.Get("transform.smaller"), Margin = new Thickness(0, 8, 0, 4) });
        mode.Items.Add(Strings.Get("transform.keepRatio")); mode.Items.Add(Strings.Get("transform.crop")); mode.SelectedIndex = 0; choice.Children.Add(mode); panel.Children.Add(choice);
        panel.Children.Add(new TextBlock { Text = Strings.Get("transform.anchor"), Margin = new Thickness(0, 12, 0, 6) });
        var grid = new UniformGrid { Rows = 3, Columns = 3, Height = 108 }; panel.Children.Add(grid);
        var buttons = new List<RadioButton>();
        foreach (string name in new[] { Strings.Get("anchor.topLeft"), Strings.Get("anchor.top"), Strings.Get("anchor.topRight"), Strings.Get("anchor.left"), Strings.Get("anchor.center"), Strings.Get("anchor.right"), Strings.Get("anchor.bottomLeft"), Strings.Get("anchor.bottom"), Strings.Get("anchor.bottomRight") })
        {
            int index = buttons.Count;
            var button = new RadioButton { Content = name, GroupName = "Placement", IsChecked = index == 4, Margin = new Thickness(3), VerticalContentAlignment = VerticalAlignment.Center };
            button.Checked += (_, _) => { alignment = index; if (preview != null) Refresh(); }; buttons.Add(button); grid.Children.Add(button);
        }
        panel.Children.Add(free);
        free.Checked += (_, _) => { preview.BeginFree(); Refresh(); };
        preview.PlacementChanged += (_, _) => { if (!resize) percent.Text = preview.ScalePercent.ToString("0.######", CultureInfo.CurrentCulture); };
        summary.Margin = new Thickness(0, 12, 0, 6); panel.Children.Add(summary); panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.SetResourceReference(MarginProperty, "Space.DialogButtons");
        Grid.SetRow(actions, 1); Grid.SetColumnSpan(actions, 2); root.Children.Add(actions);
        var apply = new Button { Content = Strings.Get("transform.apply"), IsDefault = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Strings.Get("common.cancel"), IsCancel = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4) }; actions.Children.Add(apply); actions.Children.Add(cancel);
        mode.SelectionChanged += (_, _) => Refresh(); width.TextChanged += (_, _) => Refresh(); height.TextChanged += (_, _) => Refresh();
        percent.TextChanged += (_, _) => Refresh();
        apply.Click += (_, _) =>
        {
            if ((!resize && (!Value(percent, out double p) || p <= 0 || p > 100)) ||
                (resize && (!Value(width, out double w) || !Value(height, out double h) || w < 10 || w > 2000 || h < 10 || h > 2000)))
            { error.Text = resize ? Strings.Get("transform.badSize") : Strings.Get("transform.badRatio"); return; }
            Result = new(selectedScope, resize ? 100 : double.Parse(percent.Text, CultureInfo.CurrentCulture),
                double.Parse(width.Text, CultureInfo.CurrentCulture), double.Parse(height.Text, CultureInfo.CurrentCulture), (PageResizeMode)mode.SelectedIndex, alignment)
                { FreePosition = free.IsChecked == true ? preview.FreePosition : null };
            DialogResult = true;
        };
        Refresh();
    }
    private void Refresh()
    {
        var pages = targets(selectedScope);
        bool smaller = resize && Value(width, out double w) && Value(height, out double h) && pages.Any(p =>
            Units.MillimetersToPoints(w) < p.Frame.DisplayWidthPt - 1e-7 || Units.MillimetersToPoints(h) < p.Frame.DisplayHeightPt - 1e-7);
        resizeChoice.Visibility = smaller ? Visibility.Visible : Visibility.Collapsed;
        summary.Text = Strings.Format("transform.targets", ("count", pages.Length)) + (smaller ? mode.SelectedIndex == 0 ? Strings.Get("transform.summaryKeep") : Strings.Get("transform.summaryCrop") : "");
        if (Value(width, out double pw) && Value(height, out double ph) && Value(percent, out double pc) && pw >= 10 && pw <= 2000 && ph >= 10 && ph <= 2000 && pc > 0 && pc <= 100)
            preview.Update(resize ? Units.MillimetersToPoints(pw) : null, resize ? Units.MillimetersToPoints(ph) : null, pc / 100, mode.SelectedIndex == 0, alignment, free.IsChecked == true);
    }
    private static bool Value(TextBox box, out double value) => double.TryParse(box.Text, out value) && double.IsFinite(value);
}
