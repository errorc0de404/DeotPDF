using System.Windows;
using System.Windows.Controls;
using PdfNotes.App.Themes;
using PdfNotes.Core.Commands;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App.Views;

/// <summary>
/// 내보내기 (spec 11.10, D13j, one window since D14g): the pages as page numbers and ranges, several files separated by
/// <c>;</c> (<c>1,3-10; 2,11-20; 21-30</c> makes three files), and the format — editable (this app's PDF) or with the
/// notes merged (필기 합친 PDF, Flatten).
/// The file name is asked after this dialog. Choosing or cancelling changes nothing.
/// </summary>
public sealed class PageExportDialog : Window
{
    private readonly int pageCount;
    private readonly TextBox range = new() { Name = "ExportRangeBox", Width = 300, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock summary = new() { Name = "ExportSummaryText", Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock error = new() { Name = "ExportRangeError", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly RadioButton editable = new() { Name = "ExportEditable", Content = Strings.Get("export.editable"), GroupName = "ExportFormat", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly RadioButton flatten = new() { Name = "ExportFlatten", Content = Strings.Get("export.flattened"), ToolTip = "Flatten", GroupName = "ExportFormat", Margin = new Thickness(0, 4, 0, 4) }; // Flatten: the PDF term, not UI text (D14e)
    private readonly Button accept = new() { Name = "ExportAcceptButton", Content = Strings.Get("export.ok"), IsDefault = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };

    /// <summary>Zero-based page indices per output file, in document order; null until accepted.</summary>
    public int[][]? Result { get; private set; }

    public bool Flatten { get => flatten.IsChecked == true; set { flatten.IsChecked = value; editable.IsChecked = !value; } }

    public PageExportDialog(int pageCount, string initialRange)
    {
        Theme.Attach(this);
        Theme.UseDialogTokens(this);
        this.pageCount = pageCount;
        Title = Strings.Get("export.title");
        SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        error.SetResourceReference(TextBlock.ForegroundProperty, "Text.Danger");
        summary.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

        var root = new StackPanel { Margin = new Thickness(20), MinWidth = 380 }; Content = root;
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = Strings.Get("export.pages"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        line.Children.Add(range);
        root.Children.Add(line);
        var hint = new TextBlock { Text = Strings.Get("export.splitHint"), Margin = new Thickness(0, 6, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        root.Children.Add(hint);
        root.Children.Add(summary);
        root.Children.Add(error);
        var format = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        format.Children.Add(editable);
        format.Children.Add(flatten);
        root.Children.Add(format);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.SetResourceReference(MarginProperty, "Space.DialogButtons");
        var cancel = new Button { Content = Strings.Get("common.cancel"), IsCancel = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4) };
        actions.Children.Add(accept); actions.Children.Add(cancel);
        root.Children.Add(actions);
        accept.Click += (_, _) => { if (Accept()) DialogResult = true; };

        range.TextChanged += (_, _) => Validate();
        range.Text = initialRange;
        Validate();
        Loaded += (_, _) => { range.Focus(); range.SelectAll(); };
    }

    /// <summary>Range text box (validation harness).</summary>
    public TextBox RangeBox => range;
    public string ErrorText => error.Text;
    public string SummaryText => summary.Text;
    public bool CanAccept => accept.IsEnabled;

    /// <summary>What 내보내기 does before closing (also the validation harness): false while the text is invalid.</summary>
    public bool Accept()
    {
        if (PageRanges.TryParseGroups(range.Text, pageCount, out var groups, out _) is not null) { Validate(); return false; }
        Result = groups;
        return true;
    }

    private void Validate()
    {
        var problem = PageRanges.TryParseGroups(range.Text, pageCount, out var groups, out int bad);
        string where = bad > 0 || range.Text.Contains(';') || range.Text.Contains('；') ? Strings.Format("export.fileError", ("number", bad + 1)) : "";
        error.Text = problem switch
        {
            PageRangeError.Empty => Strings.Get("export.empty"),
            PageRangeError.Syntax => where + Strings.Get("range.example"),
            PageRangeError.OutOfRange => where + Strings.Format("range.outOfRange", ("count", pageCount)),
            _ => ""
        };
        error.Visibility = error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        summary.Text = problem is null ? (groups.Length == 1 ? Strings.Format("export.summaryOne", ("pages", groups[0].Length)) : Strings.Format("export.summary", ("files", groups.Length), ("pages", groups.Sum(g => g.Length)))) : "";
        summary.Visibility = summary.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        accept.IsEnabled = problem is null;
    }
}
