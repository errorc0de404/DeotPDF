using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PdfNotes.Core.Models;
using PdfNotes.UI.Views;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// Roadmap P5c (R4h): the app's dialogs are content. On the desktop they are shown in a modal window around them, on
/// Android on a card inside the app's view; OK and Cancel end them either way.
/// </summary>
public sealed class DialogViewTests
{
    [Fact]
    public void ADialogInItsWindowEndsWithOkOrEsc() => Headless.Run(() =>
    {
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        var dialog = new NewNoteDialog();
        int opened = 0, closed = 0;
        dialog.Opened += (_, _) => opened++;
        dialog.Closed += (_, _) => closed++;
        var window = DesktopDialogs.Wrap(dialog);
        Assert.Same(dialog, window.Content);
        Assert.Equal(dialog.Title, window.Title);
        var shown = window.ShowDialog<bool>(owner);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, opened);
        dialog.OkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Headless.Pump(() => shown.IsCompleted, 5000);
        Assert.True(shown.Result);
        Assert.Equal(1, closed);
        Assert.NotNull(dialog.Result);

        var again = new NewNoteDialog();
        var second = DesktopDialogs.Wrap(again).ShowDialog<bool>(owner);
        Dispatcher.UIThread.RunJobs();
        again.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Headless.Pump(() => second.IsCompleted, 5000);
        Assert.False(second.Result);
        owner.Close();
    });

    [Fact]
    public void ADialogOnACardEndsWithOkOrBack() => Headless.Run(() =>
    {
        var window = new MainWindow(TestWindows.App(), TestWindows.Dialogs) { Width = 1200, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var view = window.View;
        var dialog = new NewNoteDialog();
        var shown = view.ShowInViewAsync(dialog);
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.HasDialogCard);
        Assert.True(window.View.FindControl<Panel>("DialogLayer")!.IsVisible);
        dialog.OkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Headless.Pump(() => shown.IsCompleted, 5000);
        Assert.True(shown.Result);
        Assert.False(view.HasDialogCard);
        Assert.False(window.View.FindControl<Panel>("DialogLayer")!.IsVisible);

        // Cards stack; Esc on a card cancels it.
        var first = view.ShowInViewAsync(new NewNoteDialog());
        var top = new NewNoteDialog();
        var second = view.ShowInViewAsync(top);
        Dispatcher.UIThread.RunJobs();
        top.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }); // not the canvas's Esc (P5c)
        Headless.Pump(() => second.IsCompleted, 5000);
        Assert.False(second.Result);
        Assert.True(view.HasDialogCard); // the first is still open
        window.Close();
    });
}
