using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PdfNotes.Core.Commands;
using PdfNotes.Editor.Localization;

namespace PdfNotes.UI.Views;

/// <summary>
/// 내보내기 (spec 11.10, D13j, D14g; the WPF PageExportDialog): page numbers and ranges, several files separated by
/// <c>;</c>, editable or with the notes merged (Flatten). The file name is asked after this window. Nothing changes here.
/// </summary>
public sealed class PageExportDialog : AppDialog
{
    private readonly int pageCount;
    private readonly TextBox range = new() { Name = "ExportRangeBox", Width = 300 };
    private readonly TextBlock summary = new() { Name = "ExportSummaryText", Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock error = new() { Name = "ExportRangeError", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly RadioButton editable = new() { Name = "ExportEditable", Content = Strings.Get("export.editable"), GroupName = "ExportFormat", IsChecked = true, Margin = new Thickness(0, 4) };
    private readonly RadioButton flatten = new() { Name = "ExportFlatten", Content = Strings.Get("export.flattened"), GroupName = "ExportFormat", Margin = new Thickness(0, 4) };

    /// <summary>Zero-based page indices per output file, in document order; null until accepted.</summary>
    public int[][]? Result { get; private set; }

    public bool Flatten { get => flatten.IsChecked == true; set { flatten.IsChecked = value; editable.IsChecked = !value; } }

    public TextBox RangeBox => range;
    public string ErrorText => error.Text ?? "";
    public string SummaryText => summary.Text ?? "";
    public bool CanAccept => OkButton.IsEnabled;

    public PageExportDialog(int pageCount, string initialRange) : base(Strings.Get("export.title"))
    {
        this.pageCount = pageCount;
        error.Bind(TextBlock.ForegroundProperty, error.GetResourceObservable("Text.Danger"));
        summary.Bind(TextBlock.ForegroundProperty, summary.GetResourceObservable("Text.Secondary"));
        ToolTip.SetTip(flatten, "Flatten"); // the PDF term, not UI text (D14e)
        var body = new StackPanel
        {
            MinWidth = 380,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = Strings.Get("export.pages"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, range } },
                Muted(Strings.Get("export.splitHint")), summary, error,
                new StackPanel { Margin = new Thickness(0, 14, 0, 0), Children = { editable, flatten } }
            }
        };
        ((Control)body.Children[1]).Margin = new Thickness(0, 6, 0, 0);
        OkButton.Name = "ExportAcceptButton";
        Build(body, Strings.Get("export.ok"));
        range.TextChanged += (_, _) => Validate();
        range.Text = initialRange;
        Validate();
        Opened += (_, _) => { range.Focus(); range.SelectAll(); };
    }

    public override bool Accept()
    {
        if (PageRanges.TryParseGroups(range.Text ?? "", pageCount, out var groups, out _) is not null) { Validate(); return false; }
        Result = groups;
        return true;
    }

    private void Validate()
    {
        string text = range.Text ?? "";
        var problem = PageRanges.TryParseGroups(text, pageCount, out var groups, out int bad);
        string where = bad > 0 || text.Contains(';') || text.Contains('；') ? Strings.Format("export.fileError", ("number", bad + 1)) : "";
        error.Text = problem switch
        {
            PageRangeError.Empty => Strings.Get("export.empty"),
            PageRangeError.Syntax => where + Strings.Get("range.example"),
            PageRangeError.OutOfRange => where + Strings.Format("range.outOfRange", ("count", pageCount)),
            _ => ""
        };
        error.IsVisible = error.Text.Length > 0;
        summary.Text = problem is null ? groups.Length == 1 ? Strings.Format("export.summaryOne", ("pages", groups[0].Length)) : Strings.Format("export.summary", ("files", groups.Length), ("pages", groups.Sum(g => g.Length))) : "";
        summary.IsVisible = summary.Text.Length > 0;
        OkButton.IsEnabled = problem is null;
    }
}
