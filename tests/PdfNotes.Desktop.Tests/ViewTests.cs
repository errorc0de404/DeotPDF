using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PdfNotes.Core.Geometry;
using PdfNotes.UI.Views;
using PdfNotes.Editor.Layout;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Desktop.Tests;

/// <summary>Roadmap P4 A1: a PDF shown by the Avalonia canvas — pixels, zoom, scrolling and pages.</summary>
public sealed class ViewTests
{
    private static async Task<(MainWindow Window, string Pdf)> Open(int pages = 3)
    {
        string pdf = Path.Combine(Headless.TempFolder(), "sample.pdf");
        ProbeSamplePdf.Create(pdf, pages);
        var window = new MainWindow(TestWindows.App(new AppSettings(), Headless.TempFolder())) { Width = 1000, Height = 760 };
        window.Show();
        Assert.True(await window.View.OpenFileAsync(pdf));
        window.View.DocumentCanvas.PageNumberSize = 0; // compared with PDFium, which draws no page numbers
        Settle(window);
        return (window, pdf);
    }

    /// <summary>Waits until the planned renders are in and drawn.</summary>
    private static void Settle(MainWindow window)
    {
        var canvas = window.View.DocumentCanvas;
        canvas.PlanRendersNow();
        Headless.Pump(() => canvas.Queue is { IsIdle: true });
        canvas.PlanRendersNow();
        Headless.Pump(() => canvas.Queue is { IsIdle: true });
    }

    private static WriteableBitmap Frame(MainWindow window) => (WriteableBitmap)window.CaptureRenderedFrame()!;

    /// <summary>BGRA of the frame at a window pixel.</summary>
    private static (byte B, byte G, byte R) At(ILockedFramebuffer frame, int x, int y)
    {
        unsafe
        {
            byte* p = (byte*)frame.Address + y * frame.RowBytes + x * 4;
            return (p[0], p[1], p[2]);
        }
    }

    [Fact]
    public void APageLooksAsPdfiumRendersIt() => Headless.Run(async () =>
    {
        var (window, pdf) = await Open();
        var canvas = window.View.DocumentCanvas;
        canvas.SetZoom(1);
        canvas.GoToPage(0);
        Settle(window);
        var t = canvas.PageTransform(0);
        var origin = canvas.TranslatePoint(new Point(t.PageOriginDip.X, t.PageOriginDip.Y), window)!.Value;
        double ppp = RenderPlanner.TargetPixelsPerPoint(1, 1);
        using var engine = new PdfiumDocument(File.ReadAllBytes(pdf));
        var expected = engine.Render(0, ppp, annotations: true);
        using var frame = Frame(window).Lock();
        int width = (int)Math.Min(expected.Width, canvas.ViewportWidth - t.PageOriginDip.X) - 2, height = (int)Math.Min(expected.Height, canvas.ViewportHeight - t.PageOriginDip.Y) - 2;
        Assert.True(width > 200 && height > 200, $"{width}×{height} of the page in view");
        long total = 0, far = 0, count = 0;
        for (int y = 2; y < height; y += 2)
            for (int x = 2; x < width; x += 2)
            {
                var got = At(frame, (int)origin.X + x, (int)origin.Y + y);
                int i = y * expected.Stride + x * 4;
                int d = Math.Abs(got.B - expected.Bgra[i]) + Math.Abs(got.G - expected.Bgra[i + 1]) + Math.Abs(got.R - expected.Bgra[i + 2]);
                total += d; count++;
                if (d > 60) far++;
            }
        Assert.True(total / (double)count < 3, $"mean difference {total / (double)count:0.00}");
        Assert.True(far < count / 200, $"{far} of {count} pixels far off");
        window.Close();
    });

    [Fact]
    public void ZoomKeepsThePointUnderTheAnchorAndScrollingMovesThePages() => Headless.Run(async () =>
    {
        var (window, _) = await Open();
        var canvas = window.View.DocumentCanvas;
        canvas.SetZoom(1);
        var center = new Point2(canvas.ViewportWidth / 2, canvas.ViewportHeight / 2);
        var before = canvas.HitTestPage(center)!.Value;
        canvas.SetZoom(2, center);
        var after = canvas.HitTestPage(center)!.Value;
        Assert.Equal(before.PageIndex, after.PageIndex);
        Assert.True(before.PagePoint.DistanceTo(after.PagePoint) < 1, $"{before.PagePoint} → {after.PagePoint}");
        Assert.Equal(2, canvas.Zoom, 9);

        // Ctrl+wheel: ×1.1 per notch around the pointer.
        window.MouseWheel(new Point(300, 300), new Vector(0, 1), RawInputModifiers.Control);
        Assert.Equal(2.2, canvas.Zoom, 6);
        // The wheel scrolls; the scroll bar follows.
        double y = canvas.Offset.Y;
        window.MouseWheel(new Point(300, 300), new Vector(0, -2), RawInputModifiers.None);
        Assert.True(canvas.Offset.Y > y);

        canvas.GoToPage(2);
        Assert.Equal(2, canvas.CurrentPageIndex);
        canvas.SetMode(ViewMode.SinglePage);
        Assert.Equal(2, canvas.CurrentPageIndex);
        canvas.PreviousPage();
        Assert.Equal(1, canvas.CurrentPageIndex);
        canvas.FitWidth();
        Assert.Equal(DocumentLayout.FitWidthZoom(window.View.Document!.Session.Current.Pages[1].Setup.Frame, canvas.ViewportWidth), canvas.Zoom, 6);
        window.Close();
    });

    [Fact]
    public void DarkPagesInvertThePaper() => Headless.Run(async () =>
    {
        var (window, _) = await Open(1);
        var canvas = window.View.DocumentCanvas;
        canvas.SetZoom(1);
        Settle(window);
        var t = canvas.PageTransform(0);
        var corner = canvas.TranslatePoint(new Point(t.PageOriginDip.X + 6, t.PageOriginDip.Y + 6), window)!.Value;
        using (var light = Frame(window).Lock()) Assert.True(At(light, (int)corner.X, (int)corner.Y).R > 240);
        canvas.DarkPages = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        using (var dark = Frame(window).Lock()) Assert.True(At(dark, (int)corner.X, (int)corner.Y).R < 15);
        window.Close();
    });
}
