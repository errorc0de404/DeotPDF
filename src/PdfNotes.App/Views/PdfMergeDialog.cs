using System.Windows;
using System.Windows.Controls;
using PdfNotes.App.Themes;
using PdfNotes.Core.Commands;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Reading;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App.Views;

/// <summary>One PDF of a merge: the opened file (checked like opening), its renderer for the range picker, and the range text.</summary>
public sealed class MergeFile(string name, PdfSourceDocument file, PdfiumDocument shown)
{
    public string Name { get; } = name;
    public PdfSourceDocument File { get; } = file;
    public PdfiumDocument Shown { get; } = shown;
    public int PageCount => Shown.PageCount;
    public string Range { get; set; } = PageRanges.All;
}

/// <summary>Where the pages of several PDFs go (spec 11.9 여러 PDF, D13j).</summary>
public enum MergePosition { AfterCurrent, Start, End }

/// <summary>
/// Several PDFs at once (spec 11.9, D13j): the files in the order they will be inserted (▲ ▼ to reorder, ✕ to leave out,
/// `파일 추가…`), a range per file (`전체`, `1-3, 5`, or picked on thumbnails with `고르기…`), and for an insert where the
/// pages go. Used for 삽입 ▾ › PDF에서 불러오기 with several files and for 파일 › PDF 합치기 (a new document).
/// Choosing or cancelling changes nothing; the caller owns (and disposes) the files.
/// </summary>
public sealed class PdfMergeDialog : Window
{
    private readonly List<MergeFile> files;
    private readonly bool insertMode;
    private readonly Func<Task<IReadOnlyList<MergeFile>>>? addFiles;
    private readonly StackPanel rows = new() { Name = "MergeRows" };
    private readonly TextBlock total = new() { Name = "MergeTotalText", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock error = new() { Name = "MergeError", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox position = new() { Name = "MergePositionBox", Width = 150, Items = { Strings.Get("merge.afterCurrent"), Strings.Get("merge.atStart"), Strings.Get("merge.atEnd") }, SelectedIndex = 0 };
    private readonly Button accept;

    /// <summary>The files and their pages (zero-based, file order) in insert order; null until accepted.</summary>
    public IReadOnlyList<(MergeFile File, int[] Pages)>? Result { get; private set; }

    public PdfMergeDialog(IEnumerable<MergeFile> files, bool insert, Func<Task<IReadOnlyList<MergeFile>>>? addFiles = null)
    {
        Theme.Attach(this);
        Theme.UseDialogTokens(this);
        this.files = [.. files];
        insertMode = insert;
        this.addFiles = addFiles;
        Title = insert ? Strings.Get("merge.importTitle") : Strings.Get("merge.title");
        ScreenArea.Fit(this, 720, 460, 560, 320);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        error.SetResourceReference(TextBlock.ForegroundProperty, "Text.Danger");
        total.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

        var root = new Grid { Margin = new Thickness(20) }; Content = root;
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var list = new Border { BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        list.SetResourceReference(Border.BorderBrushProperty, "Border");
        root.Children.Add(list);

        var below = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var line = new DockPanel();
        var add = new Button { Name = "MergeAddButton", Content = Strings.Get("merge.addFiles"), Padding = new Thickness(10, 4, 10, 4), IsEnabled = addFiles is not null };
        add.Click += async (_, _) => await AddFilesAsync();
        line.Children.Add(add);
        if (insert)
        {
            var where = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
            where.Children.Add(new TextBlock { Text = Strings.Get("merge.position"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            where.Children.Add(position);
            line.Children.Add(where);
        }
        total.Margin = new Thickness(16, 0, 0, 0); total.HorizontalAlignment = HorizontalAlignment.Right;
        line.Children.Add(total);
        below.Children.Add(line);
        below.Children.Add(error);
        Grid.SetRow(below, 1); root.Children.Add(below);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.SetResourceReference(MarginProperty, "Space.DialogButtons");
        accept = new Button { Name = "MergeAcceptButton", Content = insert ? Strings.Get("merge.insert") : Strings.Get("common.create"), IsDefault = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Strings.Get("common.cancel"), IsCancel = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4) };
        actions.Children.Add(accept); actions.Children.Add(cancel);
        Grid.SetRow(actions, 2); root.Children.Add(actions);
        accept.Click += (_, _) => { if (Accept()) DialogResult = true; };

        Rebuild();
    }

    /// <summary>The files in insert order (validation harness).</summary>
    public IReadOnlyList<MergeFile> Files => files;
    public string ErrorText => error.Text;
    public string TotalText => total.Text;
    public bool CanAccept => accept.IsEnabled;

    public MergePosition Position
    {
        get => (MergePosition)Math.Max(0, position.SelectedIndex);
        set => position.SelectedIndex = (int)value;
    }

    /// <summary>Moves file <paramref name="index"/> up (−1) or down (+1).</summary>
    public void Move(int index, int delta)
    {
        int to = index + delta;
        if (index < 0 || index >= files.Count || to < 0 || to >= files.Count) return;
        (files[index], files[to]) = (files[to], files[index]);
        Rebuild();
    }

    /// <summary>Leaves file <paramref name="index"/> out (the caller still disposes it).</summary>
    public void Remove(int index)
    {
        if (index < 0 || index >= files.Count) return;
        files.RemoveAt(index);
        Rebuild();
    }

    public void SetRange(int index, string text)
    {
        files[index].Range = text;
        if (RangeBox(index) is { } box && box.Text != text) box.Text = text; // TextChanged validates
        else Validate();
    }

    /// <summary>Adds files at the end (also what `파일 추가…` does after opening them).</summary>
    public void AddFiles(IEnumerable<MergeFile> added)
    {
        files.AddRange(added);
        Rebuild();
    }

    /// <summary>What 삽입/만들기 does before closing (also the validation harness): false while a range is invalid.</summary>
    public bool Accept()
    {
        if (!Validate()) return false;
        Result = [.. files.Select(f => { PageRanges.TryParse(f.Range, f.PageCount, out var pages); return (f, pages); })];
        return true;
    }

    private TextBox? RangeBox(int index) => rows.Children.Count > index && rows.Children[index] is Grid row ? row.Children.OfType<TextBox>().FirstOrDefault() : null;

    private async Task AddFilesAsync()
    {
        if (addFiles is null) return;
        var added = await addFiles();
        if (added.Count > 0) AddFiles(added);
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
        var row = new Grid { Margin = new Thickness(8, 6, 8, 6) };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        void Put(UIElement element, int column) { Grid.SetColumn(element, column); row.Children.Add(element); }
        var number = new TextBlock { Text = $"{index + 1}", Width = 22, VerticalAlignment = VerticalAlignment.Center };
        number.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        Put(number, 0);
        Put(new TextBlock { Text = file.Name, ToolTip = file.File.Path, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, 1);
        var pages = new TextBlock { Text = Strings.Format("merge.pageCount", ("pages", file.PageCount)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        pages.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        Put(pages, 2);
        var range = new TextBox { Name = $"MergeRange{index}", Text = file.Range, Width = 130, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = Strings.Get("merge.rangeTip") };
        range.TextChanged += (_, _) => { file.Range = range.Text; Validate(); };
        Put(range, 3);
        var pick = new Button { Content = Strings.Get("merge.pick"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 10, 0), ToolTip = Strings.Get("merge.pickTip") };
        pick.Click += (_, _) => PickRange(index);
        Put(pick, 4);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(Glyph("", Strings.Get("merge.up"), () => Move(index, -1), index > 0));
        tools.Children.Add(Glyph("", Strings.Get("merge.down"), () => Move(index, 1), index < files.Count - 1));
        tools.Children.Add(Glyph("", Strings.Get("merge.remove"), () => Remove(index), true));
        Put(tools, 5);
        return row;
    }

    private Button Glyph(string glyph, string tip, Action action, bool enabled)
    {
        var text = new TextBlock { Text = glyph, FontSize = 12, Margin = new Thickness(0) };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        var button = new Button { Content = text, ToolTip = tip, IsEnabled = enabled, Focusable = false, Style = (Style)FindResource("ToolButton"), Padding = new Thickness(6, 4, 6, 4) };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>`고르기…`: the one-file range dialog on this file, starting from the current text.</summary>
    private void PickRange(int index)
    {
        var file = files[index];
        var picker = new PdfImportDialog(file.Name, file.Shown) { Owner = this, Title = Strings.Format("merge.pickTitle", ("name", file.Name)) };
        if (PageRanges.TryParse(file.Range, file.PageCount, out _) is null) picker.RangeBox.Text = file.Range;
        if (picker.ShowDialog() == true && picker.Result is { } chosen) SetRange(index, PageRanges.Format(chosen, file.PageCount));
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
        error.Text = message;
        error.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        total.Text = message.Length == 0 ? Strings.Format("merge.summary", ("files", files.Count), ("pages", pages)) : "";
        accept.IsEnabled = message.Length == 0;
        return message.Length == 0;
    }
}
