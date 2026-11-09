using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using PdfNotes.Core.Geometry;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// Mouse and key input on a test window, in page points (review 2026-11-08 D11: one copy instead of one per test file).
/// Each step lets the queued UI work run, as a real event loop would between events.
/// </summary>
internal static class Gestures
{
    /// <summary>Where page point <paramref name="page"/> of page <paramref name="index"/> is in the window.</summary>
    public static Point PageAt(this MainWindow w, Point2 page, int index = 0)
    {
        var dip = w.View.DocumentCanvas.PageTransform(index).PageToDip.Transform(page);
        return w.View.DocumentCanvas.TranslatePoint(new Point(dip.X, dip.Y), w)!.Value;
    }

    /// <summary>Press at the first point, move through the others, release at the last.</summary>
    public static void DragThrough(this MainWindow w, MouseButton button, RawInputModifiers modifiers, IReadOnlyList<Point2> points)
    {
        w.MouseMove(w.PageAt(points[0]), modifiers);
        w.MouseDown(w.PageAt(points[0]), button, modifiers);
        foreach (var p in points.Skip(1)) w.MouseMove(w.PageAt(p), modifiers);
        w.MouseUp(w.PageAt(points[^1]), button, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary><paramref name="steps"/> evenly spaced points from <paramref name="a"/> to <paramref name="b"/> after <paramref name="a"/>.</summary>
    public static Point2[] Steps(Point2 a, Point2 b, int steps) =>
        [a, .. Enumerable.Range(1, steps).Select(i => new Point2(a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps))];

    /// <summary>A click (or several in a row) at a page point; <paramref name="hover"/> moves there first, as a real mouse does.</summary>
    public static void ClickAt(this MainWindow w, Point2 p, MouseButton button = MouseButton.Left, RawInputModifiers modifiers = RawInputModifiers.None,
        int clicks = 1, bool hover = true)
    {
        if (hover) w.MouseMove(w.PageAt(p), modifiers);
        for (int i = 0; i < clicks; i++)
        {
            w.MouseDown(w.PageAt(p), button, modifiers);
            w.MouseUp(w.PageAt(p), button, modifiers);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A key pressed and released.</summary>
    public static void Tap(this MainWindow w, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        w.KeyPress(key, modifiers, PhysicalKey.None, null);
        w.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }
}
