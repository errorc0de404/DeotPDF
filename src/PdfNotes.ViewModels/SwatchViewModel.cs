using PdfNotes.Core.Models;

namespace PdfNotes.ViewModels;

/// <summary>
/// One color of a palette (spec 3.2): a basic color or a user color, which the palette's color is now, and what a click
/// and 제거 do (roadmap P4.5 R4d).
/// </summary>
public sealed class SwatchViewModel(RgbColor color, string name, Action<RgbColor> pick, Action<RgbColor>? remove = null) : ObservableObject
{
    private bool isCurrent;

    public RgbColor Color => color;

    /// <summary>`#RRGGBB`, what the swatch is painted with.</summary>
    public string Hex => color.ToHex();

    /// <summary>The tooltip: the basic color's name, or the code of a user color.</summary>
    public string Name => name;

    /// <summary>The color the bar shows now (the swatch has a ring).</summary>
    public bool IsCurrent { get => isCurrent; set => SetProperty(ref isCurrent, value); }

    public Command Pick => field ??= new(() => pick(color));

    /// <summary>제거 of a user color (none for basic colors).</summary>
    public Command Remove => field ??= new(() => remove?.Invoke(color), () => remove is not null);

    /// <summary>The swatches of <paramref name="colors"/>, reusing <paramref name="old"/> while the colors are the same.</summary>
    public static IReadOnlyList<SwatchViewModel> Update(IReadOnlyList<SwatchViewModel> old, IReadOnlyList<RgbColor> colors, Func<RgbColor, SwatchViewModel> make) =>
        old.Select(s => s.Color).SequenceEqual(colors) ? old : [.. colors.Select(make)];

    /// <summary>Rings the swatch of <paramref name="current"/> (none when null).</summary>
    public static void Mark(IEnumerable<SwatchViewModel> swatches, RgbColor? current)
    {
        foreach (var s in swatches) s.IsCurrent = current == s.Color;
    }
}
