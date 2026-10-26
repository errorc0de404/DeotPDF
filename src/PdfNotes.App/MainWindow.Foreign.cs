using System.Windows;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

/// <summary>
/// Other apps' annotations made editable per document (spec 14.6, D14g): when the open document's source PDFs hold
/// annotations of other apps that convert, the 필기 tab shows 다른 프로그램 주석 편집 (and the empty-space quick menu offers
/// it); running it is one Undo unit. They are counted in the background after a document opens or pages are imported.
/// </summary>
public partial class MainWindow
{
    private OpenDocument? foreignCounted;
    private int foreignCountedParts = -1;
    private int foreignCount;

    /// <summary>Convertible other-app annotations in the open document's sources (0 until they are counted).</summary>
    public int ForeignAnnotationCount => ReferenceEquals(foreignCounted, document) ? foreignCount : 0;

    /// <summary>True when some page still shows other apps' annotations that the command would convert.</summary>
    public bool CanConvertForeign => document is { } doc && ForeignAnnotationCount > 0 && !busy && doc.Sources.ForeignConversion(doc.Session.Current) is not null;

    /// <summary>Counts again when the document or its imported sources changed; shows the count once in the status bar.</summary>
    private async void CountForeign()
    {
        if (document is not { } doc) return;
        int parts = doc.Sources.Imported.Count;
        if (ReferenceEquals(foreignCounted, doc) && foreignCountedParts == parts) { UpdateForeignButton(); return; }
        bool first = !ReferenceEquals(foreignCounted, doc);
        foreignCounted = doc; foreignCountedParts = parts; foreignCount = 0;
        UpdateForeignButton();
        int count;
        try { count = await doc.ForeignCountAsync(); }
        catch (Exception) { count = 0; } // the annotations simply stay read-only
        // Back on the window's thread: without a dispatcher context (tests, a caller off the UI thread) the await resumes
        // on the thread pool, and touching the controls there would end the process (async void).
        void Show()
        {
            if (!ReferenceEquals(document, doc) || foreignCountedParts != parts) return;
            foreignCount = count;
            UpdateForeignButton();
            if (first && count > 0 && !busy) StatusText.Text = Strings.Format("status.foreignFound", ("count", count)); // told once (spec 14.6)
        }
        if (Dispatcher.CheckAccess()) Show();
        else if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(Show);
    }

    private void UpdateForeignButton()
    {
        bool shown = ForeignAnnotationCount > 0;
        ForeignEditButton.Visibility = ForeignSeparator.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        ForeignEditButton.IsEnabled = shown && CanConvertForeign;
    }

    private async void ConvertForeignClick(object sender, RoutedEventArgs e) => await ConvertForeignAsync();

    /// <summary>다른 프로그램 주석 편집: converts and tells how many became editable. Returns that number.</summary>
    public async Task<int> ConvertForeignAsync()
    {
        if (document is not { } doc || busy) return 0;
        FinishTextEditing();
        View.CancelTool();
        if (NotesHidden) SetNotesHidden(false);
        int converted = await doc.ConvertForeignAsync();
        if (!ReferenceEquals(document, doc)) return 0;
        StatusText.Text = Strings.Format("status.foreignConverted", ("count", converted));
        UpdateForeignButton();
        View.Focus();
        return converted;
    }
}
