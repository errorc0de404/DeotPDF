using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PdfNotes.Core.Commands;
using PdfNotes.UI.Themes;
using PdfNotes.Editor.Localization;
using PdfNotes.ViewModels;

namespace PdfNotes.UI.Views;

/// <summary>
/// Several PDFs at once (spec 11.9, D13j, the WPF PdfMergeDialog): the files in insert order (up, down, leave out, 파일
/// 추가…), a range per file (typed or picked on thumbnails with 고르기…), and for an insert where the pages go. Choosing or
/// cancelling changes nothing; the caller owns the files.
/// </summary>
public sealed class PdfMergeDialog : AppDialog
{
    private readonly List<MergeFile> files;
    private readonly Func<Task<IReadOnlyList<MergeFile>>>? addFiles;
    private readonly StackPanel rows = new() { Name = "MergeRows" };
    private readonly TextBlock total = new() { Name = "MergeTotalText", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock mergeError = new() { Name = "MergeError", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox position = new() { Name = "MergePositionBox", Width = 150, ItemsSource = new[] { Strings.Get("merge.afterCurrent"), Strings.Get("merge.atStart"), Strings.Get("merge.atEnd") }, SelectedIndex = 0 };

    /// <summary>The files and their pages (zero-based, file order) in insert order; null until accepted.</summary>
    public IReadOnlyList<(MergeFile File, int[] Pages)>? Result { get; private set; }

    /// <summary>Shows the one-file range window of 고르기… (the window that owns this one shows it).</summary>
    public Func<PdfImportDialog, Task<bool>>? ShowPicker { get; set; }

    public PdfMergeDialog(IEnumerable<MergeFile> files, bool insert, Func<Task<IReadOnlyList<MergeFile>>>? addFiles = null)
        : base(insert ? Strings.Get("merge.importTitle") : Strings.Get("merge.title"))
    {
        this.files = [.. files];
        this.addFiles = addFiles;
        mergeError.Bind(TextBlock.ForegroundProperty, mergeError.GetResourceObservable("Text.Danger"));
        total.Bind(TextBlock.ForegroundProperty, total.GetResourceObservable("Text.Secondary"));
        var list = new Border { BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = rows } };
        list.Bind(Border.BorderBrushProperty, list.GetResourceObservable("Border"));
        var add = new Button { Name = "MergeAddButton", Content = Strings.Get("merge.addFiles"), Padding = new Thickness(10, 4), IsEnabled = addFiles is not null };
        add.Click += async (_, _) => await AddFilesAsync();
        Closing += (_, e) => { if (Content is Control { IsEnabled: false }) e.Cancel = true; }; // not while files are being added (Esc, the X)
        var line = new StackPanel { Orientation = Orientation.Horizontal, Children = { add } };
        if (insert)
            line.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), Children = { new TextBlock { Text = Strings.Get("merge.position"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, position } });
        line.Children.Add(total);
        OkButton.Name = "MergeAcceptButton";
        Fit(720, 460, 560, 320);
        var below = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Children = { line, mergeError } };
        DockPanel.SetDock(below, Dock.Bottom);
        Build(new DockPanel { Children = { below, list } }, insert ? Strings.Get("merge.insert") : Strings.Get("common.create"));
        Rebuild();
    }

    public IReadOnlyList<MergeFile> Files => files;
    public string ErrorText => mergeError.Text ?? "";
    public string TotalText => total.Text ?? "";
    public bool CanAccept => OkButton.IsEnabled;

    public new MergePosition Position
    {
        get => (MergePosition)Math.Max(0, position.SelectedIndex);
        set => position.SelectedIndex = (int)value;
    }

    public void Move(int index, int delta)
    {
        int to = index + delta;
        if (index < 0 || index >= files.Count || to < 0 || to >= files.Count) return;
        (files[index], files[to]) = (files[to], files[index]);
        Rebuild();
    }

    public void Remove(int index)
    {
        if (index < 0 || index >= files.Count) return;
        files.RemoveAt(index);
        Rebuild();
    }

    public void SetRange(int index, string text)
    {
        files[index].Range = text;
        if (RangeBox(index) is { } box && box.Text != text) box.Text = text;
        else Validate();
    }

    public void AddFiles(IEnumerable<MergeFile> added)
    {
        files.AddRange(added);
        Rebuild();
    }

    public override bool Accept()
    {
        if (!Validate()) return false;
        Result = [.. files.Select(f => { PageRanges.TryParse(f.Range, f.PageCount, out var pages); return (f, pages); })];
        return true;
    }

    private TextBox? RangeBox(int index) => rows.Children.Count > index && rows.Children[index] is Grid row ? row.Children.OfType<TextBox>().FirstOrDefault() : null;

    private async Task AddFilesAsync()
    {
        if (addFiles is null || Content is not Control content || !content.IsEnabled) return;
        // The picker and the opening of the files belong to the main window: this one waits meanwhile, so it cannot close
        // (and clean up its files) before the new ones arrive, or open a second picker.
        content.IsEnabled = false;
        try
        {
            var added = await addFiles();
            if (added.Count > 0) AddFiles(added);
        }
        finally { content.IsEnabled = true; }
    }

    private void Rebuild()
    {
        rows.Children.Clear();
        for (int i = 0; i < files.Count; i++) rows.Children.Add(Row(i));
        Validate();
    }

    private Grid Row(int index)
    {
        var file = files[index];
        var row = new Grid { Margin = new Thickness(8, 6), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto") };
        void Put(Control element, int column) { Grid.SetColumn(element, column); row.Children.Add(element); }
        var number = new TextBlock { Text = $"{index + 1}", Width = 22, VerticalAlignment = VerticalAlignment.Center };
        number.Bind(TextBlock.ForegroundProperty, number.GetResourceObservable("Text.Muted"));
        Put(number, 0);
        var name = new TextBlock { Text = file.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        ToolTip.SetTip(name, file.File.Path);
        Put(name, 1);
        var pages = new TextBlock { Text = Strings.Format("merge.pageCount", ("pages", file.PageCount)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        pages.Bind(TextBlock.ForegroundProperty, pages.GetResourceObservable("Text.Secondary"));
        Put(pages, 2);
        var range = new TextBox { Name = $"MergeRange{index}", Text = file.Range, Width = 130 };
        ToolTip.SetTip(range, Strings.Get("merge.rangeTip"));
        range.TextChanged += (_, _) => { file.Range = range.Text ?? ""; Validate(); };
        Put(range, 3);
        var pick = new Button { Content = Strings.Get("merge.pick"), Padding = new Thickness(8, 3), Margin = new Thickness(6, 0, 10, 0) };
        ToolTip.SetTip(pick, Strings.Get("merge.pickTip"));
        pick.Click += (_, _) => PickRange(index);
        Put(pick, 4);
        Put(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { Tool("ChevronUp", Strings.Get("merge.up"), () => Move(index, -1), index > 0), Tool("ChevronDown", Strings.Get("merge.down"), () => Move(index, 1), index < files.Count - 1), Tool("Close", Strings.Get("merge.remove"), () => Remove(index), true) }
        }, 5);
        return row;
    }

    private Button Tool(string glyph, string tip, Action action, bool enabled)
    {
        var button = new Button { Content = new AppIcon { Glyph = glyph, Size = 12 }, IsEnabled = enabled, Padding = new Thickness(6, 4), Theme = (Avalonia.Styling.ControlTheme)this.FindResource("ToolButton")! };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>고르기…: the one-file range window on this file, starting from the current text.</summary>
    private async void PickRange(int index)
    {
        var file = files[index];
        var picker = new PdfImportDialog(file.Name, file.Shown) { Title = Strings.Format("merge.pickTitle", ("name", file.Name)) };
        if (PageRanges.TryParse(file.Range, file.PageCount, out _) is null) picker.RangeBox.Text = file.Range;
        bool ok = ShowPicker is { } show ? await show(picker) : await picker.ShowDialog<bool>(this);
        picker.Abandon();
        if (ok && picker.Result is { } chosen) SetRange(index, PageRanges.Format(chosen, file.PageCount));
    }

    private bool Validate()
    {
        string message = "";
        int pages = 0;
        if (files.Count == 0) message = Strings.Get("merge.noFiles");
        for (int i = 0; i < files.Count && message.Length == 0; i++)
        {
            var problem = PageRanges.TryParse(files[i].Range, files[i].PageCount, out var indices);
            message = problem switch
            {
                PageRangeError.Empty => Strings.Format("merge.fileChoose", ("number", i + 1)),
                PageRangeError.Syntax => Strings.Format("merge.fileExample", ("number", i + 1)),
                PageRangeError.OutOfRange => Strings.Format("merge.fileOutOfRange", ("number", i + 1), ("count", files[i].PageCount)),
                _ => ""
            };
            pages += indices.Length;
        }
        mergeError.Text = message;
        mergeError.IsVisible = message.Length > 0;
        total.Text = message.Length == 0 ? Strings.Format("merge.summary", ("files", files.Count), ("pages", pages)) : "";
        OkButton.IsEnabled = message.Length == 0;
        return message.Length == 0;
    }
}
