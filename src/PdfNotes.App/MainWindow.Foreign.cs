using System.Windows;

namespace PdfNotes.App;

/// <summary>
/// Other apps' annotations made editable per document (spec 14.6, D14g): counting and converting are
/// <see cref="ForeignViewModel"/>; the window shows the 필기 tab command and prepares the canvas before converting.
/// </summary>
public partial class MainWindow
{
    /// <summary>Convertible other-app annotations in the open document's sources (0 until they are counted).</summary>
    public int ForeignAnnotationCount => viewModel.Foreign.Count;

    /// <summary>True when some page still shows other apps' annotations that the command would convert.</summary>
    public bool CanConvertForeign => viewModel.Foreign.CanConvert;

    private void InitializeForeign() => viewModel.Foreign.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(ForeignViewModel.IsShown))
            ForeignEditButton.Visibility = ForeignSeparator.Visibility = viewModel.Foreign.IsShown ? Visibility.Visible : Visibility.Collapsed;
    };

    private async void ConvertForeignClick(object sender, RoutedEventArgs e) => await ConvertForeignAsync();

    /// <summary>다른 프로그램 주석 편집: converts and tells how many became editable. Returns that number.</summary>
    public async Task<int> ConvertForeignAsync()
    {
        if (document is not { } doc || busy) return 0;
        FinishTextEditing();
        View.CancelTool();
        if (NotesHidden) SetNotesHidden(false);
        int converted = await viewModel.Foreign.ConvertAsync();
        if (ReferenceEquals(document, doc)) View.Focus();
        return converted;
    }
}
