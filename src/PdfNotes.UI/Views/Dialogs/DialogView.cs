using Avalonia.Controls;

namespace PdfNotes.UI.Views;

/// <summary>
/// One of the app's dialogs as content (roadmap R4h/P5c): the desktop shows it in a modal window
/// (<see cref="DesktopDialogs"/>), Android on a card inside the app's view (<see cref="MainView.ShowInViewAsync"/>),
/// because Android has no windows. OK and Cancel end it through <see cref="Finish"/>; the host sets
/// <see cref="Finished"/> and raises <see cref="Opened"/> and <see cref="Closed"/>.
/// </summary>
public abstract class DialogView : UserControl, IAcceptDialog
{
    /// <summary>The window's title, or the card's.</summary>
    public string Title { get; set; } = "";

    /// <summary>A window of this size (clamped to the screen), or null: as large as the content.</summary>
    public (double Width, double Height, double MinWidth, double MinHeight)? PreferredSize { get; protected set; }

    /// <summary>The window of <see cref="PreferredSize"/> can be resized (the default).</summary>
    public bool Resizable { get; protected set; } = true;

    /// <summary>Set by the host: called with true for OK, false for Cancel.</summary>
    public Action<bool>? Finished { get; set; }

    /// <summary>False while the dialog must stay (files are being added): Esc and the window's X wait.</summary>
    public virtual bool CanClose => true;

    /// <summary>The dialog is shown (focus a field).</summary>
    public event EventHandler? Opened;

    /// <summary>The dialog has ended (stop background work).</summary>
    public event EventHandler? Closed;

    public void RaiseOpened() => Opened?.Invoke(this, EventArgs.Empty);
    public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);

    /// <summary>OK (true) or Cancel.</summary>
    protected void Finish(bool ok)
    {
        if (!ok && !CanClose) return;
        Finished?.Invoke(ok);
    }

    public abstract bool Accept();

    /// <summary>Device pixels per DIP where the dialog is shown (thumbnails at the screen's pixels).</summary>
    protected double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    /// <summary>Opens files and web pages with the system (the backup folder, the release page).</summary>
    protected Avalonia.Platform.Storage.ILauncher? Launcher => TopLevel.GetTopLevel(this)?.Launcher;

    /// <summary>The storage of the window or screen the dialog is shown in (folder pickers).</summary>
    protected Avalonia.Platform.Storage.IStorageProvider? StorageProvider => TopLevel.GetTopLevel(this)?.StorageProvider;
}
