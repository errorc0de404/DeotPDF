using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PdfNotes.App.Canvas;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Session;

namespace PdfNotes.App;

/// <summary>
/// The loading card while a file opens: it appears only when opening takes longer than <see cref="LoadingDelay"/>, blocks
/// the canvas while the file is read, and stays until the first pages are drawn (at most <see cref="LoadingLinger"/>).
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan LoadingDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan LoadingLinger = TimeSpan.FromSeconds(2);
    private DispatcherTimer? loadingTimer;
    private PageRenderScheduler? loadingScheduler;

    /// <summary>True while the loading card is shown.</summary>
    public bool LoadingShown => LoadingOverlay.Visibility == Visibility.Visible;

    void IDocumentHost.Loading(string? name)
    {
        if (name is not null) BeginLoading(name);
        else EndLoading();
    }

    private void BeginLoading(string name)
    {
        StopLoading();
        LoadingText.Text = Strings.Format("status.openingFile", ("name", name));
        loadingTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = LoadingDelay };
        loadingTimer.Tick += (_, _) =>
        {
            loadingTimer?.Stop();
            loadingTimer = null;
            LoadingOverlay.IsHitTestVisible = true;
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingSpinner.RenderTransform.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        };
        loadingTimer.Start();
    }

    /// <summary>The file is read (or failed): a card not shown yet never appears; a shown one waits for the first pages.</summary>
    private void EndLoading()
    {
        if (loadingScheduler is not null) return; // already waiting for the pages
        if (!LoadingShown || scheduler is not { } pending) { StopLoading(); return; }
        LoadingOverlay.IsHitTestVisible = false; // the document is usable already; the card only covers the blank pages
        loadingScheduler = pending;
        pending.Idle += LoadingSchedulerIdle;
        loadingTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = LoadingLinger };
        loadingTimer.Tick += (_, _) => StopLoading();
        loadingTimer.Start();
        // The view asks for its pages at layout; nothing asked (or all cached) by then means nothing to wait for.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => { if (ReferenceEquals(loadingScheduler, pending) && pending.IsIdle) StopLoading(); });
    }

    private void LoadingSchedulerIdle() => StopLoading();

    private void StopLoading()
    {
        loadingTimer?.Stop();
        loadingTimer = null;
        if (loadingScheduler is not null) loadingScheduler.Idle -= LoadingSchedulerIdle;
        loadingScheduler = null;
        LoadingSpinner.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, null);
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }
}
