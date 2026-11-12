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
/// Several PDFs at once (spec 11.9, D13j): the files in insert order (up, down, leave out, 파일
/// 추가…), a range per file (typed or picked on thumbnails with 고르기…), and for an insert where the pages go. Choosing or
/// cancelling changes nothing; the caller owns the files.
/// </summary>
public sealed class PdfMergeDialog : AppDialog
{
    private readonly List<MergeFile> files;
    private readonly Func<Task<IReadOnlyList<MergeFile>>>? addFiles;
    private readonly ItemsControl rows = new() { Name = "MergeRows", ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MergeRowViewModel>((_, _) => new MergeRowView()) };
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
        var add = new Button { Name = "MergeAddButton", Content = Strings.Get("merge.addFiles"), Padding = new Thickness(10, 4), Command = new AsyncCommand(AddFilesAsync, () => addFiles is not null) };
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

    /// <summary>Not while files are being added (Esc, the window's X).</summary>
    public override bool CanClose => Content is not Control { IsEnabled: false };

    public MergePosition Position
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
        if (rows.ItemsSource is IReadOnlyList<MergeRowViewModel> shown && index < shown.Count) shown[index].Range = text;
        else { files[index].Range = text; Validate(); }
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
        rows.ItemsSource = files.Select((file, i) => new MergeRowViewModel(file, i, files.Count, Strings.Current, () => Validate(), Move, Remove, PickRangeAsync)).ToArray();
        Validate();
    }

    /// <summary>고르기…: the one-file range window on this file, starting from the current text.</summary>
    private async Task PickRangeAsync(int index)
    {
        var file = files[index];
        var picker = new PdfImportDialog(file.Name, file.Shown) { Title = Strings.Format("merge.pickTitle", ("name", file.Name)) };
        if (PageRanges.TryParse(file.Range, file.PageCount, out _) is null) picker.RangeBox.Text = file.Range;
        bool ok = ShowPicker is { } show && await show(picker);
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
