using System.Globalization;

namespace PdfNotes.Editor.Layout;

/// <summary>Zoom presets and parsing of the zoom box (spec 4.4, 24.2, D14d).</summary>
public static class ZoomSteps
{
    /// <summary>Zoom steps of Ctrl+= / Ctrl+- (spec 24.2): the zoom box presets, down to 10%.</summary>
    public static readonly int[] StepPercents = [10, 25, 50, 75, 100, 125, 150, 200, 300, 400, 800];

    /// <summary>Zoom presets of the status bar menu (spec 4.4, D14g): the same steps as the shortcuts, 25–800%.</summary>
    public static readonly int[] MenuPercents = [.. StepPercents.Where(p => p >= 25)];

    /// <summary>The next (<paramref name="direction"/> 1) or previous (-1) step from <paramref name="zoom"/>, as a zoom factor.</summary>
    public static double Step(double zoom, int direction)
    {
        double percent = zoom * 100;
        return (direction > 0 ? StepPercents.FirstOrDefault(p => p > percent + 0.5, StepPercents[^1])
            : StepPercents.LastOrDefault(p => p < percent - 0.5, StepPercents[0])) / 100.0;
    }

    public static string Format(double zoom, CultureInfo culture) => (zoom * 100).ToString("0", culture) + "%";

    /// <summary>Reads "125", "125%" or "125 %" as a positive zoom factor.</summary>
    public static bool TryParse(string text, CultureInfo culture, out double zoom)
    {
        bool ok = double.TryParse(text.Trim().TrimEnd('%').Trim(), NumberStyles.Float, culture, out double percent);
        zoom = percent / 100;
        return ok && double.IsFinite(zoom) && zoom > 0;
    }
}
