using PdfNotes.Core.Models;
using PdfNotes.Editor.Tools;
using PdfNotes.Text;

namespace PdfNotes.ViewModels;

/// <summary>
/// The text bar as the view binds it (roadmap P4.5 R4d): [target 글자/채우기/테두리 | palette, user colors, + |
/// transparency | font and size | B I U S highlight | alignment], each a property or a command.
/// </summary>
public sealed partial class TextStyleViewModel
{
    private bool showing;
    private IReadOnlyList<SwatchViewModel>? basicSwatches;
    private IReadOnlyList<SwatchViewModel> userSwatches = [];

    public bool TextBarShown => Bar.Visible;

    /// <summary>On only while a box is edited or selected.</summary>
    public bool TextBarEnabled => Bar.Enabled;

    // ----- target and colors -----

    public bool IsTargetText => target == TextColorTarget.Text;
    public bool IsTargetFill => target == TextColorTarget.Fill;
    public bool IsTargetBorder => target == TextColorTarget.Border;

    /// <summary>`Text`, `Fill` or `Border`.</summary>
    public Command<string> TargetCommand => field ??= new(tag => { if (Enum.TryParse<TextColorTarget>(tag, out var t)) SetColorTarget(t); });

    public IReadOnlyList<SwatchViewModel> BasicSwatches => basicSwatches ??= [.. ColorPalette.Basic.Select(c => new SwatchViewModel(c, ToolStylesViewModel.ColorName(texts, c), SetColor))];

    public IReadOnlyList<SwatchViewModel> UserSwatches => userSwatches;

    public RgbColor CustomColorStart => TargetColor ?? RgbColor.Black;

    public bool CanAddColor => owner.ToolStyles.UserColors.Count < ColorPalette.MaxUser;

    public Command<RgbColor> PickColorCommand => field ??= new(SetColor);

    // ----- transparency, font, size -----

    public InputField TransparencyField { get; } = new();
    public bool TransparencyEnabled => Bar.TransparencyEnabled;

    /// <summary>Leaving the transparency box applies a number; anything else shows the value again.</summary>
    public void CommitTransparency()
    {
        if (TransparencyField.Edited && ToolStylesViewModel.TryParseNumber(TransparencyField.Text, out double percent)) SetTransparency(percent);
        TransparencyField.Reset();
    }

    /// <summary>The fonts of this PC (none before the text engine is set up).</summary>
    public IReadOnlyList<string> FontFamilies => field ??= TextLayout.HasEngine ? [.. TextLayout.FontFamilies()] : [];

    public InputField FontField { get; } = new();

    /// <summary>A font picked from the list or typed, applied once.</summary>
    public void CommitFont()
    {
        if (FontField.Edited && FontField.Text is { Length: > 0 } family && family != CurrentStyle.FontFamily) SetFont(family);
        FontField.Reset();
    }

    public InputField SizeField { get; } = new();

    public void CommitSize()
    {
        if (SizeField.Edited) SetSize(SizeField.Text);
        SizeField.Reset();
    }

    // ----- B I U S, highlight, alignment -----

    public bool IsBold => Bar.Style.Bold;
    public bool IsItalic => Bar.Style.Italic;
    public bool IsUnderline => Bar.Style.Underline;
    public bool IsStrikeOut => Bar.Style.Strikethrough;

    /// <summary>`B`, `I`, `U` or `S`: turns it the other way from what the bar shows.</summary>
    public Command<string> FlagCommand => field ??= new(flag => ToggleFlag(flag[0]), flag => flag is ['B' or 'I' or 'U' or 'S']);

    public bool IsHighlighted => Bar.Highlighted;
    public Command HighlightCommand => field ??= new(ToggleHighlight);

    /// <summary>The alignment box's icons (names, so the open list and the box can both show one).</summary>
    public IReadOnlyList<string> AlignmentGlyphs { get; } = ["AlignLeft", "AlignCenter", "AlignRight"];

    public int AlignmentIndex
    {
        get => (int)Bar.Alignment;
        set { if (!showing && value >= 0 && value != (int)Bar.Alignment) SetAlignment((TextAlignment)value); }
    }

    /// <summary>Every bound value is read again.</summary>
    internal void ShowBar()
    {
        var bar = Bar;
        FontField.Show(bar.Style.FontFamily);
        SizeField.Show(bar.Size);
        TransparencyField.Show(bar.Transparency);
        userSwatches = SwatchViewModel.Update(userSwatches, owner.ToolStyles.UserColors, c => new SwatchViewModel(c, c.ToHex(), SetColor, owner.ToolStyles.RemoveUserColor));
        SwatchViewModel.Mark(BasicSwatches, bar.Color);
        SwatchViewModel.Mark(userSwatches, bar.Color);
        showing = true;
        try { OnPropertyChanged(string.Empty); }
        finally { showing = false; }
    }
}
