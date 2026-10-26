using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.App.Themes;
using PdfNotes.Core.Commands;
using PdfNotes.Pdf.Engine;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App.Views;

/// <summary>One page of the file being imported: its number and a thumbnail rendered when it is first shown.</summary>
public sealed class ImportThumb(int index) : INotifyPropertyChanged
{
    private ImageSource? image;
    public int Index { get; } = index;
    public string Label => (Index + 1).ToString();
    public ImageSource? Image { get => image; set { image = value; PropertyChanged?.Invoke(this, new(nameof(Image))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Range dialog of "PDF에서 불러오기" (spec 11.9): page thumbnails on the left (virtualized; click, Ctrl, Shift), the range
/// text and chosen page count kept in step with them, and a large preview of the clicked page. Invalid text shows a short
/// message and disables 삽입. Renders the file as saved (with its annotations); opening or cancelling changes nothing.
/// </summary>
public sealed class PdfImportDialog : Window
{
    private const double ThumbWidth = 132, PreviewWidth = 420, PreviewHeight = 520;
    private readonly PdfiumDocument file;
    private readonly ImportThumb[] thumbs;
    private readonly ListBox list = new() { Name = "ImportPageList", SelectionMode = SelectionMode.Extended, Width = 190 };
    private readonly TextBox range = new() { Name = "ImportRangeBox", Width = 220 };
    private readonly TextBlock count = new() { Name = "ImportCountText" }, error = new() { Name = "ImportRangeError", TextWrapping = TextWrapping.Wrap };
    private readonly Image preview = new() { Name = "ImportPreview", Stretch = Stretch.Uniform, MaxWidth = PreviewWidth, MaxHeight = PreviewHeight };
    private readonly Button insert = new() { Name = "ImportInsertButton", Content = Strings.Get("import.insert"), IsDefault = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
    private readonly CancellationTokenSource closing = new();
    private double scale;
    private bool syncing;
    private int previewIndex = -1, previewRequest;

    /// <summary>Chosen zero-based pages in file order; null until 삽입.</summary>
    public int[]? Result { get; private set; }

    public PdfImportDialog(string fileName, PdfiumDocument file)
    {
        Theme.Attach(this);
        Theme.UseDialogTokens(this);
        this.file = file;
        Title = Strings.Format("import.titleFile", ("name", fileName));
        ScreenArea.Fit(this, 760, 720, 620, 480);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        scale = VisualTreeHelper.GetDpi(this).DpiScaleX is > 0 and var dpi ? dpi : 1;
        error.SetResourceReference(TextBlock.ForegroundProperty, "Text.Danger");
        count.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

        thumbs = [.. Enumerable.Range(0, file.PageCount).Select(i => new ImportThumb(i))];
        list.ItemTemplate = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Margin="4,6,4,6" HorizontalAlignment="Center">
                <Border BorderThickness="1" BorderBrush="{DynamicResource Page.Border}" Background="{DynamicResource Paper}" Width="132" MinHeight="40" HorizontalAlignment="Center">
                  <Image Source="{Binding Image}" Width="132" Stretch="Uniform" />
                </Border>
                <TextBlock Text="{Binding Label}" HorizontalAlignment="Center" Margin="0,4,0,0" Foreground="{DynamicResource Text.Secondary}" />
              </StackPanel>
            </DataTemplate>
            """);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.ItemsSource = thumbs;
        list.ItemContainerGenerator.StatusChanged += (_, _) => RenderVisible();
        list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => RenderVisible()));
        list.SelectionChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            range.Text = PageRanges.Format(list.SelectedItems.Cast<ImportThumb>().Select(t => t.Index), thumbs.Length);
            syncing = false;
            Validate();
        };
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is ListBoxItem { DataContext: ImportThumb thumb })
                ShowPreview(thumb.Index);
        };
        range.TextChanged += (_, _) =>
        {
            if (syncing) return;
            if (PageRanges.TryParse(range.Text, thumbs.Length, out var indices) is null)
            {
                syncing = true;
                list.SelectedItems.Clear();
                foreach (int i in indices) list.SelectedItems.Add(thumbs[i]);
                syncing = false;
                if (indices.Length > 0 && !indices.Contains(previewIndex)) ShowPreview(indices[0]);
            }
            Validate();
        };

        var root = new Grid { Margin = new Thickness(20) }; Content = root;
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(list);

        var right = new Grid { Margin = new Thickness(20, 0, 0, 0) };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition());
        Grid.SetColumn(right, 1); root.Children.Add(right);
        var fields = new StackPanel();
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = Strings.Get("import.range"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        line.Children.Add(range);
        count.Margin = new Thickness(12, 0, 0, 0); count.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(count);
        fields.Children.Add(line);
        error.Margin = new Thickness(0, 6, 0, 0);
        fields.Children.Add(error);
        right.Children.Add(fields);
        var frame = new Border { BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 0, 0), Child = preview };
        frame.SetResourceReference(Border.BorderBrushProperty, "Page.Border");
        frame.SetResourceReference(Border.BackgroundProperty, "Paper");
        Grid.SetRow(frame, 1); right.Children.Add(frame);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.SetResourceReference(MarginProperty, "Space.DialogButtons");
        Grid.SetRow(actions, 1); Grid.SetColumnSpan(actions, 2); root.Children.Add(actions);
        var cancel = new Button { Content = Strings.Get("common.cancel"), IsCancel = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4) };
        actions.Children.Add(insert); actions.Children.Add(cancel);
        insert.Click += (_, _) => { if (Accept()) DialogResult = true; };
        Closed += (_, _) => closing.Cancel();

        range.Text = PageRanges.All;
        ShowPreview(0);
        Loaded += (_, _) => { range.Focus(); range.SelectAll(); RenderVisible(); };
    }

    /// <summary>Range text box (validation harness).</summary>
    public TextBox RangeBox => range;
    public ListBox PageList => list;
    public string ErrorText => error.Text;
    public bool CanInsert => insert.IsEnabled;
    public int PreviewIndex => previewIndex;

    /// <summary>What 삽입 does before closing (also the validation harness): takes the range, false when it is invalid.</summary>
    public bool Accept()
    {
        if (PageRanges.TryParse(range.Text, thumbs.Length, out var indices) is not null) { Validate(); return false; }
        Result = indices;
        return true;
    }

    /// <summary>Stops rendering (a dialog that was never shown, validation harness).</summary>
    public void Abandon() => closing.Cancel();

    private void Validate()
    {
        var problem = PageRanges.TryParse(range.Text, thumbs.Length, out var indices);
        error.Text = problem switch
        {
            PageRangeError.Empty => Strings.Get("range.choose"),
            PageRangeError.Syntax => Strings.Get("range.example"),
            PageRangeError.OutOfRange => Strings.Format("range.outOfRange", ("count", thumbs.Length)),
            _ => ""
        };
        error.Visibility = error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        count.Text = problem is null ? Strings.Format("import.summary", ("chosen", indices.Length), ("total", thumbs.Length)) : "";
        insert.IsEnabled = problem is null;
    }

    /// <summary>Moved to a monitor with another scale (or opened on one): render the thumbnails and the preview again at its pixels (D13e).</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (Math.Abs(newDpi.DpiScaleX - scale) < 1e-9) return;
        scale = newDpi.DpiScaleX;
        foreach (var thumb in thumbs) thumb.Image = null;
        RenderVisible();
        if (previewIndex >= 0) ShowPreview(previewIndex);
    }

    /// <summary>Pixels per DIP the thumbnails and the preview are rendered at (validation harness).</summary>
    public double RenderScale => scale;

    private bool rendering;
    private static readonly ImageSource Unreadable = new DrawingImage(new GeometryDrawing(Theme.Brush("Paper"), null, new RectangleGeometry(new Rect(0, 0, 3, 4))));

    /// <summary>Renders the thumbnails of the realized (visible) items that have none yet, one at a time off the UI thread.</summary>
    private async void RenderVisible()
    {
        if (rendering || closing.IsCancellationRequested) return;
        rendering = true;
        try
        {
            while (!closing.IsCancellationRequested)
            {
                var next = thumbs.FirstOrDefault(t => t.Image is null && list.ItemContainerGenerator.ContainerFromItem(t) is ListBoxItem { IsVisible: true } item && InView(item));
                if (next is null) break;
                var image = await Render(next.Index, ThumbWidth);
                if (closing.IsCancellationRequested) break;
                next.Image = image ?? Unreadable; // an unreadable page keeps an empty frame instead of stopping the others
            }
        }
        finally { rendering = false; }
    }

    private bool InView(FrameworkElement item)
    {
        if (!item.IsLoaded) return false;
        var top = item.TranslatePoint(new Point(0, 0), list).Y;
        return top + item.ActualHeight >= 0 && top <= list.ActualHeight;
    }

    private async void ShowPreview(int index)
    {
        if (index < 0 || index >= thumbs.Length) return;
        previewIndex = index;
        int request = ++previewRequest;
        var image = await Render(index, PreviewWidth, PreviewHeight);
        if (request == previewRequest && image is not null) preview.Source = image;
    }

    /// <summary>The page fitted into <paramref name="width"/> × <paramref name="height"/> DIPs, or null when closed or unreadable.</summary>
    private async Task<ImageSource?> Render(int index, double width, double height = double.PositiveInfinity)
    {
        var token = closing.Token;
        double dpi = scale;
        try
        {
            var page = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var box = file.GetPageBox(index);
                double w = box.Right - box.Left, h = box.Top - box.Bottom;
                if (box.Rotation is 90 or 270) (w, h) = (h, w);
                double perPoint = Math.Min(width / Math.Max(w, 1), height / Math.Max(h, 1)) * dpi;
                return file.Render(index, perPoint, annotations: true, token);
            }, token);
            var bitmap = BitmapSource.Create(page.Width, page.Height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32, null, page.Bgra, page.Stride);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or InvalidDataException or ArgumentOutOfRangeException or PdfiumOpenException)
        {
            return null;
        }
    }
}
