using PdfNotes.Editor.Localization;

namespace PdfNotes.ViewModels;

/// <summary>
/// The loading card while a file opens (P1 V2): it appears only when opening takes longer than <see cref="Delay"/>,
/// blocks the canvas while the file is read, and stays until the first pages are drawn (at most <see cref="Linger"/>).
/// </summary>
public sealed class LoadingViewModel : ObservableObject
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(2);

    private readonly Func<TimeSpan, Action, IDisposable> after;
    private IDisposable? timer;
    private string text = "";
    private bool isShown, blocksInput, waitsForPages;

    /// <param name="after">Runs an action once on the UI thread after a delay; disposing cancels it.</param>
    public LoadingViewModel(Func<TimeSpan, Action, IDisposable> after) => this.after = after;

    public string Text { get => text; private set => SetProperty(ref text, value); }

    /// <summary>True while the card is shown.</summary>
    public bool IsShown { get => isShown; private set => SetProperty(ref isShown, value); }

    /// <summary>True while the file is read: the card takes the input. While it waits for the pages it only covers them.</summary>
    public bool BlocksInput { get => blocksInput; private set => SetProperty(ref blocksInput, value); }

    /// <summary>True while a shown card waits for the first pages (<see cref="PagesDrawn"/>).</summary>
    public bool WaitsForPages { get => waitsForPages; private set => SetProperty(ref waitsForPages, value); }

    public void Begin(string name)
    {
        Stop();
        Text = Strings.Format("status.openingFile", ("name", name));
        Schedule(Delay, () => { IsShown = true; BlocksInput = true; });
    }

    /// <summary>
    /// The file is read (or failed): a card not shown yet never appears. Returns true when a shown card now waits for the
    /// first pages; the view then calls <see cref="PagesDrawn"/>.
    /// </summary>
    public bool End()
    {
        if (WaitsForPages) return false; // already waiting
        if (!IsShown) { Stop(); return false; }
        BlocksInput = false; // the document is usable already; the card only covers the blank pages
        WaitsForPages = true;
        Schedule(Linger, Stop);
        return true;
    }

    /// <summary>The first pages are drawn (or nothing was asked): the card goes.</summary>
    public void PagesDrawn() => Stop();

    public void Stop()
    {
        timer?.Dispose();
        timer = null;
        WaitsForPages = false;
        BlocksInput = false;
        IsShown = false;
    }

    private void Schedule(TimeSpan delay, Action action)
    {
        timer?.Dispose();
        IDisposable? mine = null;
        mine = timer = after(delay, () =>
        {
            if (!ReferenceEquals(timer, mine)) return; // stopped or replaced meanwhile
            timer = null;
            action();
        });
    }
}
