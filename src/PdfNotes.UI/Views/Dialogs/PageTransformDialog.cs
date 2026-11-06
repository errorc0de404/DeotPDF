using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.ViewModels;

namespace PdfNotes.UI.Views;

/// <summary>
/// 페이지 크기 / 내용 축소 (spec 11.6–11.8, D10d, the WPF PageTransformDialog): the settings scroll on the left, the
/// placement preview on the right, the buttons below both. Opening or cancelling changes nothing.
/// </summary>
public sealed class PageTransformDialog : Window, IAcceptDialog
{
    private readonly UniformGrid scope = new() { Name = "ScopeBox", Rows = 3, Columns = 1, Height = 84 };
    private readonly ComboBox preset = new() { Name = "PresetBox", HorizontalAlignment = HorizontalAlignment.Stretch }, mode = new() { Name = "ModeBox", HorizontalAlignment = HorizontalAlignment.Stretch };
    private PageApplyScope selectedScope;
    private readonly TextBox percent = new() { Name = "PercentBox", Text = "80" }, width = new() { Name = "WidthBox" }, height = new() { Name = "HeightBox" };
    private readonly TextBlock summary = new() { Margin = new Thickness(0, 12, 0, 6) }, error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Control resizeChoice;
    private int alignment = 4;
    private readonly bool resize;
    private readonly Func<PageApplyScope, PageSetup[]> targets;
    private readonly PagePlacementPreview preview;
    private readonly RadioButton free = new() { Name = "FreePlacement", Content = Strings.Get("transform.free"), GroupName = "Placement", Margin = new Thickness(3, 8, 3, 3) };

    public PageTransformRequest? Result { get; private set; }
    public TextBox PercentBox => percent;
    public TextBox WidthBox => width;
    public TextBox HeightBox => height;
    public PagePlacementPreview Preview => preview;

    public PageTransformDialog(bool resize, PageSetup active, Func<PageApplyScope, PageSetup[]> targets, IImage? image = null)
    {
        this.resize = resize;
        this.targets = targets;
        Title = resize ? Strings.Get("transform.resizeTitle") : Strings.Get("transform.shrinkTitle");
        DialogLook.Fit(this, 930, 760, 760, 560); // the WPF size and minimum, clamped to the screen
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DialogLook.Apply(this);
        error.Bind(TextBlock.ForegroundProperty, error.GetResourceObservable("Text.Danger"));
        var root = new Grid { Margin = new Thickness(20), ColumnDefinitions = new ColumnDefinitions("330,*"), RowDefinitions = new RowDefinitions("*,Auto") };
        Content = root;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        preview = new PagePlacementPreview(active, image, resize) { Name = "PlacementPreview", Margin = new Thickness(20, 0, 0, 0) };
        Grid.SetColumn(preview, 1);
        root.Children.Add(preview);
        void Row(string name, Control field)
        {
            panel.Children.Add(new TextBlock { Text = name, Margin = new Thickness(0, 8, 0, 4) });
            panel.Children.Add(field);
        }
        int scopeIndex = 0;
        foreach (string name in new[] { Strings.Get("transform.currentPage"), Strings.Get("transform.selectedPages"), Strings.Get("transform.allPages") })
        {
            var value = (PageApplyScope)scopeIndex++;
            var button = new RadioButton { Content = name, Tag = value, GroupName = "Scope", IsChecked = value == PageApplyScope.Current, VerticalContentAlignment = VerticalAlignment.Center };
            button.IsCheckedChanged += (_, _) => { if (button.IsChecked == true) { selectedScope = value; Refresh(); } };
            scope.Children.Add(button);
        }
        Row(Strings.Get("transform.scope"), scope);
        width.Text = Units.PointsToMillimeters(active.Frame.DisplayWidthPt).ToString("0.######", CultureInfo.CurrentCulture);
        height.Text = Units.PointsToMillimeters(active.Frame.DisplayHeightPt).ToString("0.######", CultureInfo.CurrentCulture);
        if (resize)
        {
            preset.ItemsSource = new[] { Strings.Get("transform.custom"), Strings.Get("paper.a4Portrait"), Strings.Get("paper.a4Landscape"), Strings.Get("paper.a5Portrait"), Strings.Get("paper.a5Landscape"), Strings.Get("paper.letterPortrait"), Strings.Get("paper.letterLandscape") };
            preset.SelectedIndex = 0;
            Row(Strings.Get("transform.size"), preset);
            Row(Strings.Get("transform.width"), width);
            Row(Strings.Get("transform.height"), height);
            preset.SelectionChanged += (_, _) =>
            {
                var size = preset.SelectedIndex switch { 1 => (210.0, 297.0), 2 => (297.0, 210.0), 3 => (148.0, 210.0), 4 => (210.0, 148.0), 5 => (215.9, 279.4), 6 => (279.4, 215.9), _ => (0.0, 0.0) };
                if (size.Item1 == 0) return;
                width.Text = size.Item1.ToString(CultureInfo.CurrentCulture);
                height.Text = size.Item2.ToString(CultureInfo.CurrentCulture);
            };
        }
        else Row(Strings.Get("transform.ratio"), percent);
        var choice = new StackPanel();
        resizeChoice = choice;
        choice.Children.Add(new TextBlock { Text = Strings.Get("transform.smaller"), Margin = new Thickness(0, 8, 0, 4) });
        mode.ItemsSource = new[] { Strings.Get("transform.keepRatio"), Strings.Get("transform.crop") };
        mode.SelectedIndex = 0;
        choice.Children.Add(mode);
        panel.Children.Add(choice);
        panel.Children.Add(new TextBlock { Text = Strings.Get("transform.anchor"), Margin = new Thickness(0, 12, 0, 6) });
        var grid = new UniformGrid { Rows = 3, Columns = 3, Height = 108 };
        panel.Children.Add(grid);
        int count = 0;
        foreach (string name in new[] { Strings.Get("anchor.topLeft"), Strings.Get("anchor.top"), Strings.Get("anchor.topRight"), Strings.Get("anchor.left"), Strings.Get("anchor.center"), Strings.Get("anchor.right"), Strings.Get("anchor.bottomLeft"), Strings.Get("anchor.bottom"), Strings.Get("anchor.bottomRight") })
        {
            int index = count++;
            var button = new RadioButton { Content = name, GroupName = "Placement", IsChecked = index == 4, Margin = new Thickness(3), VerticalContentAlignment = VerticalAlignment.Center };
            button.IsCheckedChanged += (_, _) => { if (button.IsChecked == true) { alignment = index; Refresh(); } };
            grid.Children.Add(button);
        }
        panel.Children.Add(free);
        free.IsCheckedChanged += (_, _) => { if (free.IsChecked == true) preview.BeginFree(); Refresh(); };
        preview.PlacementChanged += (_, _) => { if (!resize) percent.Text = preview.ScalePercent.ToString("0.######", CultureInfo.CurrentCulture); };
        panel.Children.Add(summary);
        panel.Children.Add(error);
        var apply = new Button { Content = Strings.Get("transform.apply"), IsDefault = true, MinWidth = 84, Padding = new Thickness(10, 4) };
        var cancel = new Button { Content = Strings.Get("common.cancel"), MinWidth = 84, Padding = new Thickness(10, 4) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 16, 0, 0), Children = { apply, cancel } };
        Grid.SetRow(actions, 1);
        Grid.SetColumnSpan(actions, 2);
        root.Children.Add(actions);
        mode.SelectionChanged += (_, _) => Refresh();
        width.TextChanged += (_, _) => Refresh();
        height.TextChanged += (_, _) => Refresh();
        percent.TextChanged += (_, _) => Refresh();
        apply.Click += (_, _) => { if (Accept()) Close(true); };
        cancel.Click += (_, _) => Close(false);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(false); };
        Refresh();
    }

    public bool Accept()
    {
        if ((!resize && (!Value(percent, out double p) || p <= 0 || p > 100)) ||
            (resize && (!Value(width, out double w) || !Value(height, out double h) || w < 10 || w > 2000 || h < 10 || h > 2000)))
        {
            error.Text = resize ? Strings.Get("transform.badSize") : Strings.Get("transform.badRatio");
            return false;
        }
        Value(percent, out double pc); Value(width, out double pw); Value(height, out double ph);
        Result = new(selectedScope, resize ? 100 : pc, pw, ph, (PageResizeMode)mode.SelectedIndex, alignment) { FreePosition = free.IsChecked == true ? preview.FreePosition : null };
        return true;
    }

    private void Refresh()
    {
        if (preview is null || resizeChoice is null) return;
        var pages = targets(selectedScope);
        bool smaller = resize && Value(width, out double w) && Value(height, out double h) && pages.Any(p =>
            Units.MillimetersToPoints(w) < p.Frame.DisplayWidthPt - 1e-7 || Units.MillimetersToPoints(h) < p.Frame.DisplayHeightPt - 1e-7);
        resizeChoice.IsVisible = smaller;
        summary.Text = Strings.Format("transform.targets", ("count", pages.Length)) + (smaller ? mode.SelectedIndex == 0 ? Strings.Get("transform.summaryKeep") : Strings.Get("transform.summaryCrop") : "");
        if (Value(width, out double pw) && Value(height, out double ph) && Value(percent, out double pc) && pw >= 10 && pw <= 2000 && ph >= 10 && ph <= 2000 && pc > 0 && pc <= 100)
            preview.Update(resize ? Units.MillimetersToPoints(pw) : null, resize ? Units.MillimetersToPoints(ph) : null, pc / 100, mode.SelectedIndex == 0, alignment, free.IsChecked == true);
    }

    private static bool Value(TextBox box, out double value) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);
}

/// <summary>The paper and the placed content of the page size window; in 자유 배치 the content is dragged (and resized at its corners when shrinking).</summary>
public sealed class PagePlacementPreview : Control
{
    private readonly double sourceWidth, sourceHeight;
    private readonly IImage? image;
    private readonly bool resize;
    private double paperWidth, paperHeight, scale = .8, left, top, factor = 1;
    private Point start;
    private double startLeft, startTop, startScale;
    private int drag = -1;
    private bool free;
    private Rect contentRect;

    public event EventHandler? PlacementChanged;
    public double ScalePercent => scale * 100;
    public Rect ContentBounds => contentRect;
    public Point2 FreePosition => new(left / paperWidth, top / paperHeight);

    public PagePlacementPreview(PageSetup source, IImage? image, bool resize)
    {
        sourceWidth = source.Frame.DisplayWidthPt;
        sourceHeight = source.Frame.DisplayHeightPt;
        paperWidth = sourceWidth;
        paperHeight = sourceHeight;
        this.image = image;
        this.resize = resize;
        Focusable = true;
        ClipToBounds = true;
    }

    public void BeginFree() => free = true;

    public void Update(double? width, double? height, double percent, bool fit, int alignment, bool isFree)
    {
        var position = FreePosition; // normalized placement survives a paper size change
        paperWidth = width ?? sourceWidth;
        paperHeight = height ?? sourceHeight;
        scale = resize ? fit ? Math.Min(1, Math.Min(paperWidth / sourceWidth, paperHeight / sourceHeight)) : 1 : percent;
        if (isFree) { left = position.X * paperWidth; top = position.Y * paperHeight; }
        else { left = alignment % 3 / 2.0 * (paperWidth - sourceWidth * scale); top = alignment / 3 / 2.0 * (paperHeight - sourceHeight * scale); }
        free = isFree;
        InvalidateVisual();
    }

    private IBrush Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush b ? b : Brushes.Gray;

    public override void Render(DrawingContext dc)
    {
        var size = Bounds.Size;
        dc.FillRectangle(Brush("Canvas.Background"), new Rect(size));
        factor = Math.Max(.0001, Math.Min(Math.Max(1, size.Width - 100) / Math.Max(paperWidth, sourceWidth), Math.Max(1, size.Height - 100) / Math.Max(paperHeight, sourceHeight)));
        var origin = new Point((size.Width - paperWidth * factor) / 2, (size.Height - paperHeight * factor) / 2);
        var paper = new Rect(origin.X, origin.Y, paperWidth * factor, paperHeight * factor);
        contentRect = new(origin.X + left * factor, origin.Y + top * factor, sourceWidth * scale * factor, sourceHeight * scale * factor);
        dc.DrawRectangle(Brush("Paper"), new Pen(Brush("Paper.Edge"), 1), paper);
        using (dc.PushClip(paper))
            if (image is not null) dc.DrawImage(image, new Rect(image.Size), contentRect);
        dc.DrawRectangle(null, new Pen(Brush(free ? "Accent" : "Overlay.Neutral"), 1.5), contentRect);
        if (free && !resize)
            foreach (var point in Corners(contentRect)) dc.DrawRectangle(Brush("Overlay.Halo"), new Pen(Brush("Accent"), 1.5), new Rect(point.X - 5, point.Y - 5, 10, 10));
    }

    private static Point[] Corners(Rect r) => [r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft];

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!free || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        var corners = Corners(contentRect);
        drag = !resize ? Array.FindIndex(corners, c => Point.Distance(c, p) <= 12) : -1;
        if (drag < 0 && !contentRect.Contains(p)) return;
        if (drag < 0) drag = 4;
        start = p; startLeft = left; startTop = top; startScale = scale;
        Focus();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (drag < 0)
        {
            Cursor = new Cursor(free ? !resize && Corners(contentRect).Any(c => Point.Distance(c, p) <= 12) ? StandardCursorType.BottomRightCorner : contentRect.Contains(p) ? StandardCursorType.SizeAll : StandardCursorType.Arrow : StandardCursorType.Arrow);
            return;
        }
        var delta = (p - start) / factor;
        if (drag == 4) { left = startLeft + delta.X; top = startTop + delta.Y; }
        else
        {
            double sx = drag is 0 or 3 ? -1 : 1, sy = drag is 0 or 1 ? -1 : 1;
            scale = Math.Clamp(startScale + (delta.X * sx * sourceWidth + delta.Y * sy * sourceHeight) / (sourceWidth * sourceWidth + sourceHeight * sourceHeight), .001, 1);
            left = startLeft + (sx < 0 ? sourceWidth * (startScale - scale) : 0);
            top = startTop + (sy < 0 ? sourceHeight * (startScale - scale) : 0);
        }
        InvalidateVisual();
        PlacementChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>The drag ends with the capture (another window came up, the app was switched).</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        drag = -1;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (drag < 0) return;
        e.Pointer.Capture(null);
        drag = -1;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!free || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        left += e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
        top += e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
        InvalidateVisual();
        PlacementChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
