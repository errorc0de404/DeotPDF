using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    /// <summary>
    /// Plan C5: the import window draws the thumbnails of the rows in view without a pass on every layout, and after a
    /// scroll the rows that came into view.
    /// </summary>
    [Fact]
    public void TheImportWindowDrawsTheRowsInView() => Headless.Run(() =>
    {
        string pdf = System.IO.Path.Combine(Headless.TempFolder(), "many.pdf");
        PdfNotes.Pdf.Prototype.ProbeSamplePdf.Create(pdf, 40);
        using var file = new PdfNotes.Editor.Services.PdfPreview(new PdfNotes.Pdf.Engine.PdfiumDocument(pdf));
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        var dialog = new PdfImportDialog("many.pdf", file);
        var shown = DesktopDialogs.Wrap(dialog).ShowDialog<bool>(owner);
        var thumbs = (ImportThumb[])dialog.PageList.ItemsSource!;
        bool InViewDrawn() => dialog.PageList.GetRealizedContainers().Any() &&
            dialog.PageList.GetRealizedContainers().All(row => row.DataContext is ImportThumb { Image: not null });
        void Settle(Func<bool> done)
        {
            Headless.Pump(() => { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); return done(); }, 10000);
        }
        Settle(InViewDrawn);
        Assert.Null(thumbs[^1].Image); // far below the view: not drawn
        var scroller = Avalonia.VisualTree.VisualExtensions.FindDescendantOfType<ScrollViewer>(dialog.PageList)!;
        scroller.Offset = new Avalonia.Vector(0, scroller.Extent.Height / 2); // the rows there come into view
        Settle(() => dialog.PageList.GetRealizedContainers().All(row => row.DataContext is ImportThumb { Index: > 5 }) && InViewDrawn());
        Assert.Contains(thumbs.Take(5), t => t.Image is not null); // the first rows were drawn before the scroll
        dialog.Abandon();
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

    /// <summary>Review 2026-11-08 D6: the merge window's rows are bound to their view models (no rows built in code).</summary>
    [Fact]
    public void TheMergeWindowsRowsMoveAndCheckTheirRanges() => Headless.Run(() =>
    {
        var folder = Headless.TempFolder();
        PdfNotes.ViewModels.MergeFile Open(string name, int pages)
        {
            string pdf = System.IO.Path.Combine(folder, name);
            PdfNotes.Pdf.Prototype.ProbeSamplePdf.Create(pdf, pages);
            return new(name, PdfNotes.Pdf.Reading.PdfSourceDocument.Open(pdf), new PdfNotes.Editor.Services.PdfPreview(new PdfNotes.Pdf.Engine.PdfiumDocument(pdf)));
        }
        var files = new[] { Open("a.pdf", 2), Open("b.pdf", 3) };
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        var dialog = new PdfMergeDialog(files, insert: false);
        var shown = DesktopDialogs.Wrap(dialog).ShowDialog<bool>(owner);
        Dispatcher.UIThread.RunJobs();
        var rows = dialog.GetVisualDescendants().OfType<MergeRowView>().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.True(dialog.CanAccept);
        Assert.Contains("5", dialog.TotalText);
        var first = (PdfNotes.ViewModels.MergeRowViewModel)rows[0].DataContext!;
        Assert.False(first.UpCommand.CanExecute());
        first.DownCommand.Execute();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["b.pdf", "a.pdf"], dialog.Files.Select(f => f.Name));
        var range = dialog.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "RangeBox");
        range.Text = "9";
        Dispatcher.UIThread.RunJobs();
        Assert.False(dialog.CanAccept); // page 9 of a 3-page file
        dialog.SetRange(0, "1-2");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("1-2", range.Text);
        Assert.True(dialog.CanAccept);
        owner.Close();
        foreach (var file in files) { file.Shown.Dispose(); file.File.Dispose(); }
    });
}
