using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;

namespace PdfNotes.Text;

/// <summary>
/// Auto width text boxes (1.4, plan 1.1): the width follows the longest line, up to the page edge measured from the
/// anchor along the box's own horizontal axis. The anchor is the left end, center or right end (by alignment) of the
/// box's top edge and stays where it is. Pure: callers (editor, CLI) apply the result in their own edit.
/// </summary>
public static class TextAutoWidth
{
    /// <summary>
    /// The cap when the anchor is off the page (the minimum width when that is larger).
    /// </summary>
    public const double DefaultCapPt = 180;

    // The engine lays a box out at least this wide (SkiaTextEngine.MinimumWidth) whatever the layout scale.
    private const double EngineMinimumPt = 16;

    // Wide enough that no line wraps when measuring the natural width.
    private const double Unwrapped = 100_000;

    /// <summary>
    /// The longest line laid out without wrapping (tabs and spaces as measured) plus left and right padding, never below
    /// the minimum width (twice the first run's font size plus padding). Padding follows <see cref="TextBoxAnnotation.LayoutScale"/>.
    /// </summary>
    public static double NaturalWidth(TextBoxAnnotation box)
    {
        var layout = TextLayout.Measure(box with { WidthPt = Unwrapped, Alignment = TextAlignment.Left });
        // Character boxes already start after the left padding; add the right one.
        double right = layout.Characters.Length == 0 ? 0 : layout.Characters.Max(c => c.Bounds.X + c.Bounds.Width);
        return Math.Max(MinimumWidth(box), right + TextLayout.PaddingPt * box.LayoutScale);
    }

    /// <summary>
    /// The room from the anchor to the page rectangle (0, 0, <paramref name="pageWidthPt"/>, <paramref name="pageHeightPt"/>,
    /// canonical page points) along the box's horizontal axis: forward for left alignment, backward for right, twice the
    /// shorter side for center, never below the minimum width. <see cref="DefaultCapPt"/>, or the minimum width when that is
    /// larger, when the anchor is off the page.
    /// </summary>
    public static double Cap(TextBoxAnnotation box, double pageWidthPt, double pageHeightPt)
    {
        double minimum = MinimumWidth(box), fallback = Math.Max(DefaultCapPt, minimum);
        var anchor = box.Transform.Transform(new(AnchorX(box.Alignment, box.WidthPt), 0));
        if (!(anchor.X >= 0 && anchor.X <= pageWidthPt && anchor.Y >= 0 && anchor.Y <= pageHeightPt)) return fallback;
        var axis = box.Transform.TransformVector(new(1, 0));
        var back = new Point2(-axis.X, -axis.Y);
        double cap = box.Alignment switch
        {
            TextAlignment.Center => 2 * Math.Min(Room(anchor, axis, pageWidthPt, pageHeightPt), Room(anchor, back, pageWidthPt, pageHeightPt)),
            TextAlignment.Right => Room(anchor, back, pageWidthPt, pageHeightPt),
            _ => Room(anchor, axis, pageWidthPt, pageHeightPt),
        };
        return double.IsFinite(cap) ? Math.Max(cap, minimum) : fallback;
    }

    /// <summary>
    /// The same instance for a fixed width box or one already fit. Otherwise WidthPt = min(<see cref="NaturalWidth"/>,
    /// <see cref="Cap"/>) and the transform moves along the box's horizontal axis so the anchor stays put.
    /// </summary>
    public static TextBoxAnnotation Fit(TextBoxAnnotation box, double pageWidthPt, double pageHeightPt)
    {
        if (!box.AutoWidth) return box;
        double w0 = box.WidthPt, w1 = Math.Min(NaturalWidth(box), Cap(box, pageWidthPt, pageHeightPt));
        if (w1 == w0) return box;
        double shift = AnchorX(box.Alignment, w0) - AnchorX(box.Alignment, w1);
        var transform = shift == 0 ? box.Transform : Affine.Translation(shift, 0).Then(box.Transform);
        return box with { WidthPt = w1, Transform = transform };
    }

    private static double AnchorX(TextAlignment alignment, double width) => alignment switch
    {
        TextAlignment.Center => width / 2,
        TextAlignment.Right => width,
        _ => 0,
    };

    private static double Padding(TextBoxAnnotation box) => 2 * TextLayout.PaddingPt * box.LayoutScale;

    // Run sizes are stored at the box's layout scale already; the typing default is at scale 1. Never below the engine's
    // minimum box width, so the stored width is the width the engine lays out.
    private static double MinimumWidth(TextBoxAnnotation box) => Math.Max(EngineMinimumPt,
        2 * (box.Runs.Length > 0 ? box.Runs[0].SizePt : TextEditing.Default.SizePt * box.LayoutScale) + Padding(box));

    /// <summary>
    /// How far, in box-local units, a ray from <paramref name="from"/> (inside the page) along <paramref name="step"/>
    /// (the page vector of one local unit) goes before it leaves the page.
    /// </summary>
    private static double Room(Point2 from, Point2 step, double width, double height)
    {
        double room = double.PositiveInfinity;
        if (step.X > 0) room = Math.Min(room, (width - from.X) / step.X);
        else if (step.X < 0) room = Math.Min(room, -from.X / step.X);
        if (step.Y > 0) room = Math.Min(room, (height - from.Y) / step.Y);
        else if (step.Y < 0) room = Math.Min(room, -from.Y / step.Y);
        return room;
    }
}
