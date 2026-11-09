using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PdfNotes.UI.Views;

/// <summary>One row of <see cref="PdfMergeDialog"/>; everything is bound to its <see cref="ViewModels.MergeRowViewModel"/>.</summary>
public sealed partial class MergeRowView : UserControl
{
    public MergeRowView() => AvaloniaXamlLoader.Load(this);
}
