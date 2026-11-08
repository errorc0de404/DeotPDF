using System.Diagnostics;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Layout;
using PdfNotes.Pdf.Reading;
using PdfNotes.Pdf.Rendering;

namespace PdfNotes.Tests.Editor;

/// <summary>
/// Switching documents disposes the previous render queue on the UI thread: that must not wait for the page being
/// rendered, and a bitmap that finishes afterwards is released instead of leaking.
/// </summary>
public sealed class PageRenderQueueTests
{
    private sealed class Bitmap : IDisposable
    {
        public volatile bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private static RenderKey Key() => new(Guid.NewGuid(), NewPageSpec.Default.ToPageSetup(), 0.25, null);

    [Fact]
    public void DisposeReturnsWhileARenderIsRunning()
    {
        using var renderer = new PageRenderer((PdfSourceDocument?)null);
        using var started = new ManualResetEventSlim();
        var queue = new PageRenderQueue<Bitmap>(renderer, _ => { started.Set(); Thread.Sleep(500); return new Bitmap(); }, _ => { });
        queue.Request([Key()]);
        Assert.True(started.Wait(5000));
        var watch = Stopwatch.StartNew();
        queue.Dispose();
        Assert.True(watch.ElapsedMilliseconds < 50, $"Dispose took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ABitmapFinishedAfterDisposeIsReleased()
    {
        using var renderer = new PageRenderer((PdfSourceDocument?)null);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var made = new Bitmap();
        var queue = new PageRenderQueue<Bitmap>(renderer, _ => { started.Set(); release.Wait(5000); return made; }, action => action());
        queue.Request([Key()]);
        Assert.True(started.Wait(5000));
        queue.Dispose();
        release.Set();
        var watch = Stopwatch.StartNew();
        while (!made.Disposed && watch.ElapsedMilliseconds < 5000) Thread.Sleep(10);
        Assert.True(made.Disposed);
    }
}
