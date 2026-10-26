using System.Windows;
using System.Windows.Input;
using PdfNotes.Core.Geometry;

namespace PdfNotes.Probe;

/// <summary>D14c: the harness drives the editor tools with WPF points and modifiers; these convert them to editor input.</summary>
internal static class ProbeInput
{
    public static Point2 P2(Point p) => new(p.X, p.Y);

    public static CanvasPointer Ptr(Point p, ModifierKeys modifiers = ModifierKeys.None, int clicks = 1) => new(P2(p), (InputModifiers)modifiers, clicks);

    public static Guid? Pick(this SelectTool tool, Point p, bool includeLocked = false) => tool.Pick(P2(p), includeLocked);

    public static void PointerHover(this IHoverTool tool, Point? p) => tool.PointerHover(p is { } at ? P2(at) : null, (InputModifiers)Keyboard.Modifiers);

    /// <summary>The current language pack's text for a Korean UI text (D14e: the harness finds controls by their Korean text).</summary>
    public static string Ui(string korean) =>
        PdfNotes.Editor.Localization.Strings.Korean.Values.FirstOrDefault(v => v.Value == korean) is { Key: { } key } ? PdfNotes.Editor.Localization.Strings.Get(key) : korean;
}
