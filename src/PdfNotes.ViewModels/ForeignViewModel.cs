using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Services;

namespace PdfNotes.ViewModels;

/// <summary>
/// Other apps' annotations made editable per document (spec 14.6, D14g; P1 V2): when the open document's source PDFs
/// hold annotations of other apps that convert, the 필기 tab shows 다른 프로그램 주석 편집; running it is one Undo unit.
/// They are counted in the background after a document opens or pages are imported.
/// </summary>
public sealed class ForeignViewModel(MainViewModel owner, Action<Action> onUiThread) : ObservableObject
{
    private OpenDocument? counted;
    private int countedParts = -1;
    private int count;

    /// <summary>Convertible other-app annotations in the open document's sources (0 until they are counted).</summary>
    public int Count => ReferenceEquals(counted, owner.Document) ? count : 0;

    /// <summary>The command is shown while there are such annotations.</summary>
    public bool IsShown => Count > 0;

    /// <summary>True when some page still shows other apps' annotations that the command would convert.</summary>
    public bool CanConvert => owner.Document is { } doc && Count > 0 && !owner.IsBusy && doc.Sources.ForeignConversion(doc.Session.Current) is not null;

    /// <summary>The document, its model or the busy state changed: the command may be enabled differently.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(CanConvert));
    }

    /// <summary>
    /// Counts again when the document or its imported sources changed; tells the count once in the status line. The task
    /// ends when the count is shown (a count already running for the same sources is returned).
    /// </summary>
    public Task RecountAsync()
    {
        if (owner.Document is not { } doc) return Task.CompletedTask;
        int parts = doc.Sources.Imported.Count;
        if (ReferenceEquals(counted, doc) && countedParts == parts) { Refresh(); return counting; }
        bool first = !ReferenceEquals(counted, doc);
        counted = doc; countedParts = parts; count = 0;
        Refresh();
        return counting = CountAsync(doc, parts, first);
    }

    private Task counting = Task.CompletedTask;

    private async Task CountAsync(OpenDocument doc, int parts, bool first)
    {
        int found;
        try { found = await doc.ForeignCountAsync().ConfigureAwait(false); }
        catch (Exception) { found = 0; } // the annotations simply stay read-only
        var shown = new TaskCompletionSource();
        // Back on the UI thread: the views follow these properties.
        onUiThread(() =>
        {
            try
            {
                if (!ReferenceEquals(owner.Document, doc) || countedParts != parts) return;
                count = found;
                Refresh();
                if (first && found > 0 && !owner.IsBusy) owner.Status = Strings.Format("status.foreignFound", ("count", found)); // told once (spec 14.6)
            }
            finally { shown.TrySetResult(); }
        });
        await shown.Task;
    }

    /// <summary>다른 프로그램 주석 편집: converts and tells how many became editable. Returns that number.</summary>
    public async Task<int> ConvertAsync()
    {
        if (owner.Document is not { } doc || owner.IsBusy) return 0;
        int converted = await doc.ConvertForeignAsync();
        if (!ReferenceEquals(owner.Document, doc)) return 0;
        owner.Status = Strings.Format("status.foreignConverted", ("count", converted));
        Refresh();
        return converted;
    }
}
