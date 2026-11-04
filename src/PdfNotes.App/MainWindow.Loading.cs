using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PdfNotes.App.Canvas;

namespace PdfNotes.App;

/// <summary>
/// The loading card while a file opens. Its timing is <see cref="LoadingViewModel"/>; the window shows the card and tells
/// the view model when the first pages are drawn.
/// </summary>
public partial class MainWindow
{
    private PageRenderScheduler? loadingScheduler;

    /// <summary>True while the loading card is shown.</summary>
    public bool LoadingShown => viewModel.Loading.IsShown;

    private void InitializeLoading() => viewModel.Loading.PropertyChanged += (_, e) =>
    {
        var loading = viewModel.Loading;
        switch (e.PropertyName)
        {
            case nameof(LoadingViewModel.BlocksInput):
                LoadingOverlay.IsHitTestVisible = loading.BlocksInput;
                break;
            case nameof(LoadingViewModel.IsShown):
                LoadingOverlay.Visibility = loading.IsShown ? Visibility.Visible : Visibility.Collapsed;
                LoadingSpinner.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, loading.IsShown
                    ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever } : null);
                break;
            case nameof(LoadingViewModel.WaitsForPages) when !loading.WaitsForPages && loadingScheduler is not null:
                loadingScheduler.Idle -= LoadingSchedulerIdle;
                loadingScheduler = null;
                break;
        }
    };

    void IMainView.WaitForFirstPages()
    {
        if (scheduler is not { } pending) { viewModel.Loading.PagesDrawn(); return; }
        loadingScheduler = pending;
        pending.Idle += LoadingSchedulerIdle;
        // The view asks for its pages at layout; nothing asked (or all cached) by then means nothing to wait for.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => { if (ReferenceEquals(loadingScheduler, pending) && pending.IsIdle) viewModel.Loading.PagesDrawn(); });
    }

    private void LoadingSchedulerIdle() => viewModel.Loading.PagesDrawn();
}
