using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PdfNotes.Core.Commands;
using PdfNotes.Editor.Localization;
using PdfNotes.Pdf.Engine;

namespace PdfNotes.UI.Views;

/// <summary>One page of the file being imported: its number and a thumbnail drawn in the background.</summary>
public sealed class ImportThumb(int index) : INotifyPropertyChanged
{
    private IImage? image;
    public int Index { get; } = index;
    public string Label => (Index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
    public IImage? Image { get => image; set { image = value; PropertyChanged?.Invoke(this, new(nameof(Image))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The range window of PDF에서 불러오기 (spec 11.9, the WPF PdfImportDialog): page thumbnails on the left (click, Ctrl,
/// Shift), the range text and the chosen count kept in step with them, and a large preview of the clicked page. Invalid
/// text shows a short message and disables 삽입. The file is drawn as saved (with its annotations). Nothing changes here.
/// </summary>
public sealed class PdfImportDialog : AppDialog
{
    private const double ThumbWidth = 132, PreviewWidth = 420, PreviewHeight = 520;
    private readonly PdfiumDocument file;
    private readonly ImportThumb[] thumbs;
    private readonly ListBox list = new() { Name = "ImportPageList", SelectionMode = SelectionMode.Multiple, Width = 190 };
    private readonly TextBox range = new() { Name = "ImportRangeBox", Width = 220 };
    private readonly TextBlock count = new() { Name = "ImportCountText", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock rangeError = new() { Name = "ImportRangeError", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Image preview = new() { Name = "ImportPreview", Stretch = Stretch.Uniform, MaxWidth = PreviewWidth, MaxHeight = PreviewHeight };
    private readonly CancellationTokenSource closing = new();
    private bool syncing;
    private int previewIndex = -1, previewRequest;

    /// <summary>Chosen zero-based pages in file order; null until 삽입.</summary>
    public int[]? Result { get; private set; }

    public TextBox RangeBox => range;
    public ListBox PageList => list;
    public string ErrorText => rangeError.Text ?? "";
    public bool CanInsert => OkButton.IsEnabled;
    public int PreviewIndex => previewIndex;

    public PdfImportDialog(string fileName, PdfiumDocument file) : base(Strings.Format("import.titleFile", ("name", fileName)))
    {
        this.file = file;
        rangeError.Bind(TextBlock.ForegroundProperty, rangeError.GetResourceObservable("Text.Danger"));
        count.Bind(TextBlock.ForegroundProperty, count.GetResourceObservable("Text.Secondary"));
        thumbs = [.. Enumerable.Range(0, file.PageCount).Select(i => new ImportThumb(i))];
        list.ItemTemplate = new FuncDataTemplate<ImportThumb>((_, _) =>
        {
            var image = new Image { Width = ThumbWidth, Stretch = Stretch.Uniform };
            image.Bind(Image.SourceProperty, new Binding(nameof(ImportThumb.Image)));
            var frame = new Border { BorderThickness = new Thickness(1), Width = ThumbWidth, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Center, Child = image };
            frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Page.Border"));
            frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("Paper"));
            var label = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            label.Bind(TextBlock.TextProperty, new Binding(nameof(ImportThumb.Label)));
            return new StackPanel { Margin = new Thickness(4, 6), HorizontalAlignment = HorizontalAlignment.Center, Children = { frame, label } };
        });
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        list.ItemsSource = thumbs;
        list.SelectionChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            range.Text = PageRanges.Format((list.SelectedItems ?? new List<object>()).OfType<ImportThumb>().Select(t => t.Index), thumbs.Length);
            syncing = false;
            Validate();
        };
        list.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true)?.DataContext is ImportThumb thumb) ShowPreview(thumb.Index);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        range.TextChanged += (_, _) =>
        {
            if (syncing) return;
            if (PageRanges.TryParse(range.Text ?? "", thumbs.Length, out var indices) is null)
            {
                syncing = true;
                list.SelectedItems?.Clear();
                foreach (int i in indices) list.SelectedItems?.Add(thumbs[i]);
                syncing = false;
                if (indices.Length > 0 && !indices.Contains(previewIndex)) ShowPreview(indices[0]);
            }
            Validate();
        };
        var fields = new StackPanel
        {
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = Strings.Get("import.range"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, range, count } },
                rangeError
            }
        };
        var frame = new Border { BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 0, 0), Child = preview };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Page.Border"));
        frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("Paper"));
        var right = new StackPanel { Margin = new Thickness(20, 0, 0, 0), Width = PreviewWidth + 20, Children = { fields, frame } };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { list, right } };
        Grid.SetColumn(right, 1);
        OkButton.Name = "ImportInsertButton";
        Fit(760, 720, 620, 480);
        Build(body, Strings.Get("import.insert"));
        Closed += (_, _) => closing.Cancel();
        range.Text = PageRanges.All;
        ShowPreview(0);
        Opened += (_, _) => { range.Focus(); range.SelectAll(); RenderVisible(); };
        // Only the rows in view are drawn (the WPF dialog): when the list scrolls or lays out new rows, and again at another screen scale.
        list.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => RenderVisible());
        list.LayoutUpdated += (_, _) => RenderVisible();
        ScalingChanged += (_, _) =>
        {
            scaleGeneration++;
            foreach (var thumb in thumbs) thumb.Image = null;
            RenderVisible();
            if (previewIndex >= 0) ShowPreview(previewIndex);
        };
    }

    public override bool Accept()
    {
        if (PageRanges.TryParse(range.Text ?? "", thumbs.Length, out var indices) is not null) { Validate(); return false; }
        Result = indices;
        return true;
    }

    /// <summary>Stops drawing (a window that was never shown).</summary>
    public void Abandon() => closing.Cancel();

    private void Validate()
    {
        var problem = PageRanges.TryParse(range.Text ?? "", thumbs.Length, out var indices);
        rangeError.Text = problem switch
        {
            PageRangeError.Empty => Strings.Get("range.choose"),
            PageRangeError.Syntax => Strings.Get("range.example"),
            PageRangeError.OutOfRange => Strings.Format("range.outOfRange", ("count", thumbs.Length)),
            _ => ""
        };
        rangeError.IsVisible = rangeError.Text.Length > 0;
        count.Text = problem is null ? Strings.Format("import.summary", ("chosen", indices.Length), ("total", thumbs.Length)) : "";
        OkButton.IsEnabled = problem is null;
    }

    private bool rendering;
    private int scaleGeneration; // a thumbnail drawn for an earlier screen scale is not kept

    /// <summary>An unreadable page: a 3:4 sheet of paper instead of stopping the others (the WPF Unreadable image).</summary>
    private static readonly IImage Unreadable = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(3, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul).Also(b =>
    {
        using var l = b.Lock();
        System.Runtime.InteropServices.Marshal.Copy(Enumerable.Repeat((byte)255, 3 * 4 * 4).ToArray(), 0, l.Address, 3 * 4 * 4);
    });

    /// <summary>Draws the thumbnails of the rows in view that have none yet, one at a time off the UI thread.</summary>
    private async void RenderVisible()
    {
        if (rendering || closing.IsCancellationRequested) return;
        rendering = true;
        try
        {
            while (!closing.IsCancellationRequested)
            {
                var next = thumbs.FirstOrDefault(t => t.Image is null && list.ContainerFromItem(t) is { IsEffectivelyVisible: true } row && InView(row));
                if (next is null) break;
                int generation = scaleGeneration;
                var image = await Render(next.Index, ThumbWidth) ?? Unreadable;
                if (generation == scaleGeneration) next.Image = image;
            }
        }
        finally { rendering = false; }
    }

    private bool InView(Control row) => row.TranslatePoint(new Point(0, 0), list) is { } top && top.Y + row.Bounds.Height >= 0 && top.Y <= list.Bounds.Height;

    private async void ShowPreview(int index)
    {
        if (index < 0 || index >= thumbs.Length) return;
        previewIndex = index;
        int request = ++previewRequest;
        var image = await Render(index, PreviewWidth, PreviewHeight);
        if (request == previewRequest && image is not null) preview.Source = image;
    }

    /// <summary>The page fitted into <paramref name="width"/> × <paramref name="height"/> DIPs, or null when closed or unreadable.</summary>
    private async Task<IImage?> Render(int index, double width, double height = double.PositiveInfinity)
    {
        var token = closing.Token;
        double dpi = RenderScaling > 0 ? RenderScaling : 1;
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
            var bitmap = new WriteableBitmap(new PixelSize(page.Width, page.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul); // see PageSnapshots
            using (var locked = bitmap.Lock())
                for (int y = 0; y < page.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(page.Bgra, y * page.Stride, locked.Address + y * locked.RowBytes, Math.Min(page.Stride, locked.RowBytes));
            return bitmap;
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or InvalidDataException or ArgumentOutOfRangeException or PdfiumOpenException)
        {
            return null;
        }
    }
}

internal static class Fluent
{
    public static T Also<T>(this T value, Action<T> act) { act(value); return value; }
}
