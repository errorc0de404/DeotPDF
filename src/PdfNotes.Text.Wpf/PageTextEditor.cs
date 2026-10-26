using System.Windows;
using System.Windows.Controls;

namespace PdfNotes.Text;

/// <summary>RichTextBox reserves 5 units for its caret by replacing PagePadding; enforce the spec's fixed 4pt instead.</summary>
public sealed class PageTextEditor : RichTextBox
{
    public double LayoutScale { get; set; } = 1;
    protected override Size MeasureOverride(Size constraint)
    {
        var size = base.MeasureOverride(constraint);
        if (Document.PagePadding != new Thickness(4 * LayoutScale)) { Document.PagePadding = new Thickness(4 * LayoutScale); size = base.MeasureOverride(constraint); }
        return size;
    }
}
