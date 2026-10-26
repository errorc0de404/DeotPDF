using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using PdfNotes.App.Tools;
using PdfNotes.App.Views;

namespace PdfNotes.Tests.App;

/// <summary>D13e: per-monitor DPI and small high-scale screens.</summary>
public sealed class DpiTests
{
    [Fact]
    public void AppManifestAsksForPerMonitorV2()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "PdfNotes.App"))) dir = dir.Parent;
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "src", "PdfNotes.App", "app.manifest"));
        Assert.Contains("PerMonitorV2", text);
        Assert.Contains("true/pm", text);
        var built = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(typeof(PdfNotes.App.MainWindow).Assembly.Location));
        Assert.Contains("PerMonitorV2", built); // embedded in the built app, not only in the source file
    }

    [Theory]
    [InlineData(1.0, 32)]
    [InlineData(1.25, 40)]
    [InlineData(1.5, 48)]
    [InlineData(2.0, 64)]
    [InlineData(0.0, 32)]
    public void RotationCursorGrowsWithTheScale(double scale, int pixels) => Assert.Equal(pixels, SelectionCursors.RotatePixels(scale));

    [Fact]
    public void RotationCursorIsBuiltOncePerScale() => Sta(() =>
    {
        Assert.Same(SelectionCursors.RotateFor(1.5), SelectionCursors.RotateFor(1.5));
        Assert.NotSame(SelectionCursors.RotateFor(1), SelectionCursors.RotateFor(2));
    });

    [Fact]
    public void DialogsShrinkToTheWorkAreaAndKeepTheirMinimumBelowIt() => Sta(() =>
    {
        try
        {
            var dialog = new Window();
            ScreenArea.Override = new Size(853, 501); // 1280×800 at 150 %
            ScreenArea.Fit(dialog, 930, 760, 760, 560);
            Assert.Equal(813, dialog.Width);
            Assert.Equal(461, dialog.Height);
            Assert.Equal(760, dialog.MinWidth);
            Assert.Equal(461, dialog.MinHeight);
            ScreenArea.Override = new Size(2194, 1207); // 4K at 175 %: the preferred size
            ScreenArea.Fit(dialog, 930, 760, 760, 560);
            Assert.Equal((930.0, 760.0, 760.0, 560.0), (dialog.Width, dialog.Height, dialog.MinWidth, dialog.MinHeight));
        }
        finally { ScreenArea.Override = null; }
    });

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
