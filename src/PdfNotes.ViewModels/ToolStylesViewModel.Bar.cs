using System.Globalization;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Tools;
using DashStyle = PdfNotes.Core.Models.DashStyle;

namespace PdfNotes.ViewModels;

/// <summary>
/// The style bar and the eraser bar as the view binds them (roadmap P4.5 R4d): which bar is up, the palette, width,
/// transparency and the kind options, each a property; the view has no code that copies values into controls.
/// </summary>
public sealed partial class ToolStylesViewModel
{
    private bool showing;
    private IReadOnlyList<SwatchViewModel>? basicSwatches;
    private IReadOnlyList<SwatchViewModel> userSwatches = [];
    private string[] lineShapes = [];

    // ----- which bar -----

    /// <summary>The style bar is up: not the eraser bar, not the text bar.</summary>
    public bool StyleBarShown => !Bar.Eraser && !owner.TextStyle.Bar.Visible;

    public bool EraserBarShown => Bar.Eraser;

    /// <summary>The bar is on only with something to style (the selection tool with nothing selected has nothing).</summary>
    public bool StyleBarEnabled => Bar.Target is not null;

    public double StyleBarOpacity => StyleBarEnabled ? 1 : 0.55;

    // ----- palette -----

    /// <summary>The ten basic colors.</summary>
    public IReadOnlyList<SwatchViewModel> BasicSwatches => basicSwatches ??= [.. ColorPalette.Basic.Select(c => new SwatchViewModel(c, ColorName(owner.Texts, c), SetColor))];

    /// <summary>The user colors every palette shares (right-click 제거).</summary>
    public IReadOnlyList<SwatchViewModel> UserSwatches => userSwatches;

    /// <summary>The color the + window opens with.</summary>
    public RgbColor CustomColorStart => StyleTarget?.Style.Color ?? Pen.Color;

    /// <summary>The + window can add one more user color (up to <see cref="ColorPalette.MaxUser"/>).</summary>
    public bool CanAddColor => UserColors.Count < ColorPalette.MaxUser;

    public Command<RgbColor> PickColorCommand => field ??= new(SetColor);

    // ----- width and transparency -----

    public double WidthMinimum => StrokeStyle.WidthRange(Bar.Shown(Pen).Kind).Min;
    public double WidthMaximum => StrokeStyle.WidthRange(Bar.Shown(Pen).Kind).Max;

    /// <summary>The width slider: a drag changes the width at once, one Undo unit until <see cref="Commit"/>.</summary>
    public double WidthSlider
    {
        get => Math.Clamp(Bar.Shown(Pen).Style.WidthPt, WidthMinimum, WidthMaximum);
        set { if (!showing) SetWidth(Math.Round(value * 2) / 2, continuous: true); }
    }

    public InputField WidthField { get; } = new();
    public bool WidthEnabled => Bar.WidthEnabled;

    /// <summary>Leaving the width box applies what was typed once.</summary>
    public void CommitWidth()
    {
        if (WidthField.Edited && TryParseNumber(WidthField.Text, out double value)) SetWidth(value);
        WidthField.Reset();
    }

    public double TransparencySlider
    {
        get => Bar.Shown(Pen).Style.TransparencyPercent;
        set { if (!showing) SetTransparency(Math.Round(value), continuous: true); }
    }

    public InputField TransparencyField { get; } = new();
    public bool TransparencyEnabled => Bar.TransparencyEnabled;

    public void CommitTransparency()
    {
        if (TransparencyField.Edited && TryParseNumber(TransparencyField.Text, out double value)) SetTransparency(value);
        TransparencyField.Reset();
    }

    // ----- the highlighter (spec 9.1) -----

    public bool MarkKindShown => Bar.MarkKindShown;
    public bool IsMarkHighlight => markKind is null;
    public bool IsMarkUnderline => markKind == TextMarkupKind.Underline;
    public bool IsMarkStrikeOut => markKind == TextMarkupKind.StrikeOut;

    /// <summary>`Highlight`, `Underline` or `StrikeOut`.</summary>
    public Command<string> MarkKindCommand => field ??= new(tag => SetMarkKind(tag switch { "Underline" => TextMarkupKind.Underline, "StrikeOut" => TextMarkupKind.StrikeOut, _ => null }));

    public bool HighlighterSnapShown => Bar.Highlighter;
    public bool HighlighterSnapOn => Settings.HighlighterSnap;

    /// <summary>Underline and strikeout always go on text (spec 9.1): the toggle is off for them.</summary>
    public bool HighlighterSnapEnabled => Bar.SnapEnabled;

    public Command HighlighterSnapCommand => field ??= new(() => SetHighlighterSnap(!Settings.HighlighterSnap));

    public bool HighlightAutoShown => Bar.HighlightAutoShown;
    public bool HighlightAutoOn => Bar.HighlightAutoChecked;
    public Command HighlightAutoCommand => field ??= new(() => SetHighlightAuto(!Bar.HighlightAutoChecked));

    // ----- shapes (spec 8 preface, D11) -----

    public bool ShapeOptionsShown => Bar.ShapeOptionsShown;
    public bool ShapeBorderTarget => !shapeFillTarget;

    /// <summary>`Fill` or `Border`: what the palette and transparency edit.</summary>
    public Command<string> ShapeTargetCommand => field ??= new(tag => SetShapeColorTarget(tag == "Fill"));

    public IReadOnlyList<string> DashNames => field ??= [owner.Texts.Get("dash.solid"), owner.Texts.Get("dash.dashed"), owner.Texts.Get("dash.dotted"), owner.Texts.Get("dash.dashDot")];

    /// <summary>없음, then the dashes.</summary>
    public IReadOnlyList<string> BorderNames => field ??= [owner.Texts.Get("border.none"), .. DashNames];

    /// <summary>The border box: 0 none, 1+ the dash, -1 mixed.</summary>
    public int ShapeBorderIndex
    {
        get => ShapeBar.BorderIndex;
        set { if (!showing && value >= 0 && value != ShapeBar.BorderIndex) SetShapeBorder(value == 0 ? null : (DashStyle)(value - 1)); }
    }

    // ----- lines (spec 8.1–8.2) -----

    public bool LineOptionsShown => Bar.LineOptionsShown;

    /// <summary>The dash box (-1 mixed).</summary>
    public int DashIndex
    {
        get => LineBar.DashIndex;
        set { if (!showing && value >= 0 && value != LineBar.DashIndex) SetLineDash((DashStyle)value); }
    }

    public bool StartArrowOn => LineBar.StartArrow;
    public bool EndArrowOn => LineBar.EndArrow;
    public Command StartArrowCommand => field ??= new(() => SetLineArrows(start: !LineBar.StartArrow));
    public Command EndArrowCommand => field ??= new(() => SetLineArrows(end: !LineBar.EndArrow));

    /// <summary>A selected line's straight / one / two controls, or the curve tool's automatic / one / two; none otherwise.</summary>
    public IReadOnlyList<string> LineShapes => lineShapes;

    public bool LineShapeShown => lineShapes.Length > 0;

    public int LineShapeIndex
    {
        get => LineBar.ShapeIndex;
        set { if (!showing && value >= 0 && value != LineBar.ShapeIndex) SetLineShape(value); }
    }

    // ----- the eraser bar (spec 7.3–7.4) -----

    public double EraserSlider
    {
        get => EraserSize;
        set { if (!showing && SetEraserSize(Math.Round(value))) ShowBar(); }
    }

    public InputField EraserField { get; } = new();

    /// <summary>Leaving the size box applies it; the size is an app setting, stored at once.</summary>
    public void CommitEraserSize()
    {
        if (EraserField.Edited && TryParseNumber(EraserField.Text, out double value)) SetEraserSize(value);
        EraserField.Reset();
        ShowBar();
        owner.SaveSettings();
    }

    // ----- showing -----

    /// <summary>Every bound value is read again: the boxes the user is not typing in, the swatches and the lists.</summary>
    internal void ShowBar()
    {
        var bar = Bar;
        var (style, _) = bar.Shown(Pen);
        WidthField.Show(bar.Target is null || bar.Mixed.Width ? "" : FormatNumber(style.WidthPt));
        TransparencyField.Show(bar.Target is null || bar.Mixed.Transparency ? "" : FormatNumber(style.TransparencyPercent));
        EraserField.Show(FormatNumber(EraserSize));
        var line = LineBar;
        string[] shapes = line.Shapes switch
        {
            LineShapeChoice.SelectedLine => [owner.Texts.Get("lineShape.straight"), owner.Texts.Get("lineShape.oneControl"), owner.Texts.Get("lineShape.twoControls")],
            LineShapeChoice.CurveTool => [owner.Texts.Get("common.auto"), owner.Texts.Get("lineShape.oneControl"), owner.Texts.Get("lineShape.twoControls")],
            _ => []
        };
        if (!shapes.SequenceEqual(lineShapes)) lineShapes = shapes;
        userSwatches = SwatchViewModel.Update(userSwatches, UserColors, c => new SwatchViewModel(c, c.ToHex(), SetColor, RemoveUserColor));
        RgbColor? current = bar.Target is not null && !bar.Mixed.Color ? style.Color : null;
        SwatchViewModel.Mark(BasicSwatches, current);
        SwatchViewModel.Mark(userSwatches, current);
        showing = true;
        try { OnPropertyChanged(string.Empty); } // every property: what the bar shows is computed from the state
        finally { showing = false; }
    }

    internal static string ColorName(PdfNotes.Editor.Localization.Texts texts, RgbColor color)
    {
        int index = ((IList<RgbColor>)ColorPalette.Basic).IndexOf(color);
        string[] keys = ["color.black", "color.gray", "color.red", "color.orange", "color.yellow", "color.green", "color.blue", "color.navy", "color.purple", "color.white"];
        return index >= 0 && index < keys.Length ? texts.Get(keys[index]) : color.ToHex();
    }

    internal static string FormatNumber(double value) => Math.Round(value, 1).ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>A number typed in a bar box: `3.5`, `3.5pt`, `40%`.</summary>
    internal static bool TryParseNumber(string? text, out double value) =>
        double.TryParse((text ?? "").Trim().TrimEnd('%').Replace("pt", "", StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);
}
