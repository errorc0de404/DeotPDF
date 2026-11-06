namespace PdfNotes.ViewModels;

/// <summary>
/// The thread the screen runs on (roadmap P4.5 R4d): timers and background work come back to it, and a few steps wait
/// until the current input is over.
/// </summary>
public interface IUiThread
{
    /// <summary>Runs <paramref name="action"/> on the UI thread (at once when called there).</summary>
    void Run(Action action);

    /// <summary>Runs <paramref name="action"/> on the UI thread after what it is doing now.</summary>
    void Post(Action action);
}

/// <summary>No UI thread (view models made without a screen, tests): everything runs at once.</summary>
public sealed class ImmediateUi : IUiThread
{
    public static readonly ImmediateUi Instance = new();
    public void Run(Action action) => action();
    public void Post(Action action) => action();
}
