using System.Globalization;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;
using PdfNotes.Text;
using Opacity = PdfNotes.Core.Models.Opacity;

namespace PdfNotes.ViewModels;

/// <summary>What the text bar's palette and transparency edit (spec 3.2, 2026-10-13): the characters, the box fill or the box border.</summary>
public enum TextColorTarget { Text, Fill, Border }

/// <summary>
/// The text box being edited, as the text bar sees it (P1 V8). The WPF window implements it over its RichTextBox; the
/// P2 editor implements the same. Editing itself (IME, caret, keys) stays with the implementation.
/// </summary>
public interface ITextEditorSession
{
    /// <summary>True while a text box is being edited.</summary>
    bool IsEditing { get; }

    /// <summary>The edited box with its current text and runs (only while editing).</summary>
    TextBoxAnnotation Box { get; }

    /// <summary>The selected characters of the edited box (cluster boundaries).</summary>
    (int Start, int Length) Range { get; }

    /// <summary>The style the next typed character gets.</summary>
    TextRun TypingStyle { get; }

    /// <summary>Finishes an IME composition so its characters are in <see cref="Box"/> (before any format change).</summary>
    void CompleteInput();

    /// <summary>Changes the format of the selected characters, or of the next typed ones when nothing is selected.</summary>
    void Format(Func<TextRun, TextRun> change);

    /// <summary>Changes the edited box (width, alignment, fill, border, highlights), keeping the selection.</summary>
    void Change(Func<TextBoxAnnotation, TextBoxAnnotation> change);
}

/// <summary>What the text bar shows (spec 3.2, 6): the view only copies it into its controls.</summary>
/// <param name="Color">The current target's color, or null when the box has no fill or border to show.</param>
/// <param name="Transparency">The transparency box text (empty for the characters, which have none).</param>
public sealed record TextBarState(
    bool Visible, bool Enabled, TextRun Style, bool Highlighted, TextAlignment Alignment, TextColorTarget Target, RgbColor? Color,
    bool TransparencyEnabled, string Transparency)
{
    public string Size => Style.SizePt.ToString("0.##", CultureInfo.CurrentCulture);
}

/// <summary>
/// The text bar (spec 3.2, 6, 9.4; P1 V8): the character format, the box fill and border, glyph highlights in boxes and the
/// saved styles, applied to the box being edited (<see cref="ITextEditorSession"/>) or to every selected box (one Undo
/// unit). A new box starts with the fill and border set last. <see cref="Changed"/> asks the view to show the bar again.
/// </summary>
public sealed class TextStyleViewModel(MainViewModel owner, IEditorDialogs dialogs) : ObservableObject
{
    private Texts texts => owner.Texts;

    private ITextEditorSession? editor;
    private SelectTool? selection;
    private TextColorTarget target = TextColorTarget.Text;

    /// <summary>The bar should show the current state again.</summary>
    public event EventHandler? Changed;

    private void Show() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>The edited box changed (typing, the caret moved): the bar shows the new state.</summary>
    public void Refresh() => Show();

    /// <summary>The view's text editor and its selection tool.</summary>
    public void Attach(ITextEditorSession session, SelectTool tool)
    {
        editor = session;
        selection = tool;
    }

    private bool Editing => editor?.IsEditing == true;
    private OpenDocument? Document => owner.Document;
    private TextBoxAnnotation[] SelectedBoxes => selection?.SelectedAnnotations.OfType<TextBoxAnnotation>().ToArray() ?? [];

    /// <summary>The box the bar edits: the one being edited, else the first selected box.</summary>
    public TextBoxAnnotation? Target => Editing ? editor!.Box : SelectedBoxes.FirstOrDefault();

    /// <summary>The character format the bar shows: of the selected characters (or the caret) while editing, else of the box's start.</summary>
    public TextRun CurrentStyle
    {
        get
        {
            if (Editing) { var (start, length) = editor!.Range; return length > 0 ? TextEditing.StyleAt(editor.Box, start) : editor.TypingStyle; }
            return Target is { } box ? TextEditing.StyleAt(box, 0) : owner.Settings.DefaultText;
        }
    }

    /// <summary>Box fill/border a new text box starts with: the last one set in the text bar (spec 6.2).</summary>
    public TextBoxStyle NewBoxStyle => owner.Settings.TextBox.ToStyle();

    /// <summary>The character style a new box starts with (spec 6.3).</summary>
    public TextRun DefaultStyle => owner.Settings.DefaultText;

    public TextColorTarget ColorTarget => target;

    /// <summary>Chooses what the palette and transparency edit (not document state).</summary>
    public void SetColorTarget(TextColorTarget value)
    {
        target = value;
        Show();
    }

    /// <summary>The color of the current target, or null when the box has no fill or border to show.</summary>
    public RgbColor? TargetColor
    {
        get
        {
            var style = Target?.BoxStyle ?? NewBoxStyle;
            return target switch
            {
                TextColorTarget.Text => CurrentStyle.Color,
                TextColorTarget.Fill => style.BackgroundOpacity > 0 ? style.BackgroundColor : null,
                _ => style.BorderEnabled ? style.BorderColor : null
            };
        }
    }

    /// <summary>
    /// The bar: shown with the text tool, while editing and with a box selected; on only with something to edit.
    /// </summary>
    public TextBarState Bar
    {
        get
        {
            var boxes = SelectedBoxes;
            bool visible = owner.Tools.Current == ToolKind.Text || Editing || boxes.Length > 0;
            var box = Target;
            var style = box?.BoxStyle ?? NewBoxStyle;
            bool highlighted = Editing && box is not null && editor!.Range is { Length: > 0 } range
                ? TextHighlights.Covers(box, range.Start, range.Length) : box is not null && TextHighlights.Covers(box, 0, box.Text.Length);
            string transparency = target switch
            {
                TextColorTarget.Fill => Percent(Opacity.ToTransparencyPercent(style.BackgroundOpacity)),
                TextColorTarget.Border => style.BorderEnabled ? Percent(Opacity.ToTransparencyPercent(style.BorderOpacity)) : "100",
                _ => ""
            };
            return new(visible, Editing || boxes.Length > 0, CurrentStyle, highlighted, box?.Alignment ?? TextAlignment.Left, target, TargetColor,
                target != TextColorTarget.Text, transparency);
        }
    }

    private static string Percent(double value) => Math.Round(value, 1).ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>
    /// A palette color for the current target. Choosing a fill or border color for a box without one turns it on, opaque
    /// (like shapes). Colors outside the basic palette join the shared user colors.
    /// </summary>
    public void SetColor(RgbColor color)
    {
        owner.ToolStyles.RememberColor(color);
        owner.SaveSettings();
        switch (target)
        {
            case TextColorTarget.Text: Format(r => r with { Color = color }); break;
            case TextColorTarget.Fill: ApplyBoxStyle(s => s with { BackgroundColor = color, BackgroundOpacity = s.BackgroundOpacity > 0 ? s.BackgroundOpacity : 1 }); break;
            default: ApplyBoxStyle(s => s with { BorderColor = color, BorderOpacity = s.BorderEnabled && s.BorderOpacity > 0 ? s.BorderOpacity : 1, BorderEnabled = true }); break;
        }
        owner.ToolStyles.Refresh();
    }

    /// <summary>Transparency (0–100%) of the box fill or border; a 100% border is no border. Characters have none.</summary>
    public void SetTransparency(double percent)
    {
        if (!double.IsFinite(percent) || target == TextColorTarget.Text) return;
        percent = Math.Clamp(percent, 0, 100);
        double opacity = Opacity.FromTransparencyPercent(percent);
        if (target == TextColorTarget.Fill) ApplyBoxStyle(s => s with { BackgroundOpacity = opacity });
        else ApplyBoxStyle(s => percent >= 100 ? s with { BorderEnabled = false } : s with { BorderEnabled = true, BorderOpacity = opacity });
    }

    /// <summary>A typed size (pt, kept in the input range).</summary>
    public void SetSize(string text)
    {
        if (double.TryParse(text, out double size) && double.IsFinite(size)) Format(r => r with { SizePt = TextEditing.InputSize(size) });
    }

    /// <summary>A font name; a font this PC does not have is reported and the fallback font (TextLayout.FallbackFont) used instead.</summary>
    public void SetFont(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return;
        if (!TextLayout.FontAvailable(family)) { dialogs.Tell(texts.Get("text.fontMissing"), NoticeKind.Information); family = TextLayout.FallbackFont; }
        Format(r => r with { FontFamily = family });
    }

    /// <summary>Changes the box fill/border of the edited or selected boxes; the next new box starts with it too (2026-10-13 user request, like the shape tool).</summary>
    public void ApplyBoxStyle(Func<TextBoxStyle, TextBoxStyle> change)
    {
        var next = change(Target?.BoxStyle ?? NewBoxStyle);
        ApplyBox(b => b with { BoxStyle = change(b.BoxStyle) });
        owner.Settings.TextBox = AppSettings.TextBoxSettings.From(next);
        owner.SaveSettings();
    }

    /// <summary>Changes the character format: the edited box's selection (or caret), else every selected box (one Undo unit).</summary>
    public void Format(Func<TextRun, TextRun> change)
    {
        editor?.CompleteInput();
        Document?.Session.SealCoalescing();
        if (Editing) editor!.Format(change);
        else if (Document is { } doc && SelectedBoxes is { Length: > 0 } boxes)
            doc.Session.Execute(DocumentEdits.UpdateAnnotations(doc.Session.Current, boxes.Select(b => TextEditing.Format(b, 0, b.Text.Length, change)), "undo.textFormat"));
        Document?.Session.SealCoalescing();
        Show();
    }

    /// <summary>Changes the edited box, else every selected box (width, alignment, fill and border).</summary>
    public void ApplyBox(Func<TextBoxAnnotation, TextBoxAnnotation> change)
    {
        editor?.CompleteInput();
        Document?.Session.SealCoalescing();
        if (Editing) editor!.Change(change);
        else if (Document is { } doc) doc.Session.Execute(DocumentEdits.UpdateAnnotations(doc.Session.Current, SelectedBoxes.Select(change), "undo.textBoxStyle"));
        Document?.Session.SealCoalescing();
        Show();
    }

    public void SetAlignment(TextAlignment alignment) => ApplyBox(b => b with { Alignment = alignment });

    /// <summary>B, I, U or S of the bar (or Ctrl+B/I/U): on for all the target characters, or off when <paramref name="on"/> is false.</summary>
    public void SetFlag(char flag, bool on) => Format(r => flag switch
    {
        'B' => r with { Bold = on },
        'I' => r with { Italic = on },
        'U' => r with { Underline = on },
        'S' => r with { Strikethrough = on },
        _ => throw new ArgumentOutOfRangeException(nameof(flag))
    });

    /// <summary>Ctrl+B/I/U: turns the flag the other way from the current format.</summary>
    public void ToggleFlag(char flag)
    {
        editor?.CompleteInput();
        var r = CurrentStyle;
        SetFlag(flag, flag switch { 'B' => !r.Bold, 'I' => !r.Italic, 'U' => !r.Underline, _ => !r.Strikethrough });
    }

    /// <summary>
    /// Highlights the selected characters of the edited box with the highlighter's style, or removes the highlight when they
    /// are all highlighted already (spec 9.4). Without an editor it applies to whole selected boxes.
    /// </summary>
    public void ToggleHighlight()
    {
        editor?.CompleteInput();
        Document?.Session.SealCoalescing();
        var s = owner.ToolStyles.TextHighlightStyle;
        var style = new TextHighlightRange(0, 0, s.Mode, s.ManualHeight, s.Style.Color, s.Style.Opacity);
        TextBoxAnnotation Toggle(TextBoxAnnotation box, int start, int length) =>
            TextHighlights.Covers(box, start, length) ? TextHighlights.Remove(box, start, length) : TextHighlights.Add(box, start, length, style);
        if (Editing)
        {
            var (start, length) = editor!.Range;
            if (length > 0) editor.Change(box => Toggle(box, start, length));
        }
        else if (Document is { } doc && SelectedBoxes is { Length: > 0 } boxes)
            doc.Session.Execute(DocumentEdits.UpdateAnnotations(doc.Session.Current, boxes.Select(b => Toggle(b, 0, b.Text.Length)), "undo.textHighlight"));
        Document?.Session.SealCoalescing();
        Show();
    }

    /// <summary>Saves the current character format into a slot (0–4) or as the default for new boxes (null); app settings, no Undo.</summary>
    public void SaveStyle(int? slot)
    {
        editor?.CompleteInput();
        var style = CurrentStyle with { Start = 0, Length = 0 };
        if (slot is { } i) owner.Settings.TextSlots[i] = style; else owner.Settings.DefaultText = style;
        owner.SaveSettings();
        owner.Status = slot is { } number ? texts.Format("text.slotSaved", ("number", number + 1)) : texts.Get("text.defaultSaved");
    }

    /// <summary>Applies a saved slot's format (an empty slot does nothing).</summary>
    public void ApplySlot(int slot)
    {
        if (owner.Settings.TextSlots[slot] is { } r) Format(old => r with { Start = old.Start, Length = old.Length });
    }
}
