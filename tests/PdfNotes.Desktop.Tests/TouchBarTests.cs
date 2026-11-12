using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Desktop.Tests;

/// <summary>2026-11-12 user report: a finger on the scroll bar of a long document shook the page.</summary>
public sealed class TouchBarTests
{
    [Fact]
    public void AFingerOnTheScrollBarIgnoresItsWobbleAndMovesThePageCalmly() => Headless.Run(async () =>
    {
        var w = new MainWindow(TestWindows.App(new AppSettings(), Headless.TempFolder()), TestWindows.Dialogs) { Width = 1200, Height = 900 };
        w.Script.PromptOverride = (_, b) => b == AskButtons.YesNo ? Answer.Yes : Answer.No;
        w.Script.MessageSink = _ => { };
        w.Show();
        string pdf = Path.Combine(Headless.TempFolder(), "long.pdf");
        ProbeSamplePdf.Create(pdf, 40);
        Assert.True(await w.View.OpenFileAsync(pdf));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        var canvas = w.View.DocumentCanvas;
        var bar = canvas.VerticalBar;
        Assert.True(bar.IsVisible);
        var finger = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);
        var touching = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
        void Move(double y) => bar.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, bar, finger, bar, new Point(6, y), 0, touching, KeyModifiers.None));

        double before = canvas.Offset.Y;
        bar.RaiseEvent(new PointerPressedEventArgs(bar, finger, bar, new Point(6, 300), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        Assert.Equal(before, canvas.Offset.Y); // landing does not jump (no repeating page button)
        foreach (double y in new[] { 301, 299.5, 300.8, 299.2, 300 }) Move(y);
        Assert.Equal(before, canvas.Offset.Y); // the wobble of a resting finger moves nothing

        double rate = canvas.TouchBarRate(bar);
        for (double y = 300; y <= 400; y += 5) Move(y);
        double moved = canvas.Offset.Y - before;
        Assert.True(moved > 0, "a drag down scrolls down");
        Assert.True(moved <= 100 * rate + 1, $"no more than the touch rate: {moved} for 100 DIP at {rate}/DIP");
        bar.RaiseEvent(new PointerReleasedEventArgs(bar, finger, bar, new Point(6, 400), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        double after = canvas.Offset.Y;
        Move(500);
        Assert.Equal(after, canvas.Offset.Y); // lifted: nothing follows
        w.Close();
    });
}
