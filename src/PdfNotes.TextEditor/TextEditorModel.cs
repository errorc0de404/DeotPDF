using System.Collections.Immutable;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;

namespace PdfNotes.TextEditor;

/// <summary>
/// The state of the one text box being edited (roadmap P2 S4): the box, caret, selection anchor, typing style and the
/// editor's own Undo. It takes neutral input — typed text, editing keys, pointer positions in the box's local points —
/// and lays the box out through <see cref="TextLayout"/> (or the measure given). Platform controls draw it and feed it
/// (S6 Avalonia, P4 app); nothing here touches a window, a clipboard or the document.
/// </summary>
/// <remarks>
/// Offsets are UTF-16 indexes into <see cref="Box"/>.Text and always on grapheme boundaries. The members of the text
/// bar's <c>ITextEditorSession</c> (P1, PdfNotes.ViewModels) are here with the same signatures, so P4 connects the bar
/// with a one-line adapter. Undo while editing touches only this box's characters and stops at the start (spec 13.2).
/// </remarks>
public sealed class TextEditorModel
{
    private readonly Func<TextBoxAnnotation, TextLayout.Layout> measure;
    private TextBoxAnnotation box;
    private int anchor, caret;
    private bool upstream; // at a soft line wrap the caret stands at the end of the upper line (after End), not the lower
    private double? goalX; // Up/Down keep the x where the vertical move began
    private TextRun typingStyle;
    private bool typingOverride; // the typing style was set by Format with no selection: kept until the caret moves

    /// <summary>An Undo step: the box and selection before it and after it (coalesced changes move After).</summary>
    private sealed class Step(TextBoxAnnotation beforeBox, int beforeAnchor, int beforeCaret)
    {
        public (TextBoxAnnotation Box, int Anchor, int Caret) Before { get; } = (beforeBox, beforeAnchor, beforeCaret);
        public (TextBoxAnnotation Box, int Anchor, int Caret) After { get; set; }
    }

    private readonly List<Step> undo = [], redo = [];
    private TextEditKind? lastKind;
    private int lastEnd = -1; // where the last coalesced typing ended, or where the last deletion happened
    private bool sealedGroup = true;
    private bool typedSpaceLast; // the text typed in the current step ended with white space: a word typed next is a new step

    private string composition = "";
    private int? compositionCursor;
    private bool backspaceGuard; // a composition was just emptied: the IME's own Backspace may arrive once more as a key

    private enum DragUnit { None, Character, Word }
    private DragUnit drag;
    private (int Start, int End) dragOrigin;

    /// <param name="box">The box to edit (a new box may be empty).</param>
    /// <param name="typingStyle">The style typed characters get in an empty box (the default style for a new box).</param>
    /// <param name="measure">The layout of a box; <see cref="TextLayout.Measure"/> when null.</param>
    public TextEditorModel(TextBoxAnnotation box, TextRun? typingStyle = null, Func<TextBoxAnnotation, TextLayout.Layout>? measure = null)
    {
        this.box = box;
        this.measure = measure ?? TextLayout.Measure;
        this.typingStyle = (typingStyle ?? TextEditing.StyleAt(box, 0)) with { Start = 0, Length = 0 };
        caret = anchor = box.Text.Length;
        if (box.Text.Length > 0) this.typingStyle = TextEditing.StyleAt(box, box.Text.Length - 1) with { Start = 0, Length = 0 };
    }

    /// <summary>The box changed (characters, runs, highlights or box properties).</summary>
    public event EventHandler<TextEditedEventArgs>? Edited;

    /// <summary>The caret or the selection moved.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>The IME composition (<see cref="Composition"/>) began, changed or ended.</summary>
    public event EventHandler? CompositionChanged;

    // ----- ITextEditorSession members (same signatures) -----

    /// <summary>Always true: a model exists only while its box is being edited.</summary>
    public bool IsEditing => true;

    /// <summary>The edited box with its current text, runs and highlights.</summary>
    public TextBoxAnnotation Box => box;

    /// <summary>The selected characters: start and length, on grapheme boundaries.</summary>
    public (int Start, int Length) Range => (Math.Min(anchor, caret), Math.Abs(caret - anchor));

    /// <summary>The style the next typed character gets.</summary>
    public TextRun TypingStyle => typingStyle;

    /// <summary>
    /// Finishes an IME composition so its characters are in <see cref="Box"/>: the composition text is typed as if
    /// the IME had committed it. The platform also ends the IME's composition so the IME does not commit it again.
    /// </summary>
    public void CompleteInput()
    {
        if (composition.Length == 0) return;
        CommitComposition(composition);
    }

    /// <summary>
    /// Changes the format of the selected characters (one Undo step), or of the next typed ones when nothing is
    /// selected — the typing style then stays until the caret moves.
    /// </summary>
    public void Format(Func<TextRun, TextRun> change)
    {
        CompleteInput();
        var (start, length) = Range;
        var current = length > 0 ? TextEditing.StyleAt(box, start) : typingStyle;
        typingStyle = change(current) with { Start = 0, Length = 0 };
        if (length > 0)
        {
            typingOverride = false;
            Commit(TextEditing.Format(box, start, length, change), anchor, caret, TextEditKind.Formatting, coalesce: false);
        }
        else
        {
            typingOverride = true;
            Seal();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Changes the edited box (width, alignment, fill, border, highlights) as one Undo step, keeping the selection.</summary>
    public void Change(Func<TextBoxAnnotation, TextBoxAnnotation> change)
    {
        CompleteInput();
        var after = change(box);
        if (after.Id != box.Id) throw new InvalidOperationException("The edited box cannot become another box.");
        int a = TextUnits.Snap(after.Text, anchor), c = TextUnits.Snap(after.Text, caret);
        Commit(after, a, c, TextEditKind.BoxChange, coalesce: false);
    }

    // ----- state -----

    /// <summary>The caret offset (the moving end of the selection).</summary>
    public int Caret => caret;

    /// <summary>The fixed end of the selection.</summary>
    public int Anchor => anchor;

    public bool HasSelection => anchor != caret;

    /// <summary>How many Undo steps lie behind the current state.</summary>
    public int Depth => undo.Count;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>The selected text (empty when nothing is selected).</summary>
    public string SelectedText { get { var (s, l) = Range; return box.Text.Substring(s, l); } }

    public TextLayout.Layout Layout => measure(box);

    // ----- typing and deleting -----

    /// <summary>
    /// Types <paramref name="text"/> at the caret, replacing the selection, in the typing style (a selection being
    /// replaced gives its first character's style unless the typing style was just set). Line ends become line feeds.
    /// </summary>
    public void Type(string text)
    {
        backspaceGuard = false;
        CompleteInput();
        text = Normalize(text);
        if (text.Length == 0) return;
        var (start, length) = Range;
        var style = length > 0 && !typingOverride ? TextEditing.StyleAt(box, start) with { Start = 0, Length = 0 } : typingStyle;
        // A word typed after white space starts a new Undo step (spec 13.2: steps split at meaningful boundaries).
        bool newWord = typedSpaceLast && !char.IsWhiteSpace(text[0]);
        bool coalesce = length == 0 && lastKind == TextEditKind.Typing && !sealedGroup && start == lastEnd && !newWord;
        Replace(start, start + length, text, style, TextEditKind.Typing, coalesce);
        typingStyle = style;
        lastEnd = caret;
        typedSpaceLast = char.IsWhiteSpace(text[^1]);
    }

    /// <summary>An editing key: caret moves (Shift extends the selection, Ctrl moves by words or to the box ends), deletion, Enter and Tab.</summary>
    public void Key(EditorKey key, EditorModifiers modifiers = EditorModifiers.None)
    {
        bool shift = modifiers.HasFlag(EditorModifiers.Shift), control = modifiers.HasFlag(EditorModifiers.Control);
        // The IME deleted the last character of its composition and the same Backspace comes again as a key (1.1 IME
        // trial): it would delete a committed character, so it is dropped once.
        if (key == EditorKey.Backspace && backspaceGuard) { backspaceGuard = false; return; }
        backspaceGuard = false;
        if (composition.Length > 0)
        {
            if (key is EditorKey.Backspace or EditorKey.Delete) return; // the IME edits its composition itself
            CompleteInput(); // Enter, Tab and caret moves commit the composition first
        }
        switch (key)
        {
            case EditorKey.Enter: Type("\n"); return;
            case EditorKey.Tab: Type("\t"); return;
            case EditorKey.Backspace: Delete(forward: false, control); return;
            case EditorKey.Delete: Delete(forward: true, control); return;
        }
        string text = box.Text;
        var (start, length) = Range;
        int target;
        bool toUpstream = false;
        double? keepGoal = null;
        switch (key)
        {
            case EditorKey.Left:
                target = length > 0 && !shift ? start : control ? TextUnits.PreviousWordStart(text, caret) : TextUnits.PreviousGrapheme(text, caret);
                break;
            case EditorKey.Right:
                target = length > 0 && !shift ? start + length : control ? TextUnits.NextWordStart(text, caret) : TextUnits.NextGrapheme(text, caret);
                break;
            case EditorKey.Home:
                target = control ? 0 : LineOf(caret, upstream).Start;
                break;
            case EditorKey.End:
                if (control) target = text.Length;
                else
                {
                    var line = LineOf(caret, upstream);
                    target = line.End;
                    toUpstream = IsSoftWrapped(line, text);
                }
                break;
            case EditorKey.Up:
            case EditorKey.Down:
            {
                int from = length > 0 && !shift ? (key == EditorKey.Up ? start : start + length) : caret;
                bool fromUpstream = length > 0 && !shift ? false : upstream;
                double x = goalX ?? CaretRect(from, fromUpstream).X;
                var lines = Layout.Lines;
                int index = LineIndex(lines, from, fromUpstream);
                int next = key == EditorKey.Up ? index - 1 : index + 1;
                if (next < 0) target = 0;
                else if (next >= lines.Length) target = text.Length;
                else (target, toUpstream) = HitLine(lines[next], x);
                keepGoal = x;
                break;
            }
            default: return;
        }
        MoveTo(target, shift, toUpstream);
        goalX = keepGoal;
    }

    private void Delete(bool forward, bool word)
    {
        var (start, length) = Range;
        string text = box.Text;
        if (length > 0) { Replace(start, start + length, "", typingStyle, TextEditKind.Deleting, coalesce: false); Seal(); return; }
        int a, z;
        if (forward) { a = caret; z = word ? TextUnits.NextWordStart(text, caret) : TextUnits.NextGrapheme(text, caret); }
        else { z = caret; a = word ? TextUnits.PreviousWordStart(text, caret) : TextUnits.PreviousGrapheme(text, caret); }
        if (z <= a) return;
        bool coalesce = !word && lastKind == TextEditKind.Deleting && !sealedGroup && (forward ? a == lastEnd : z == lastEnd);
        Replace(a, z, "", typingStyle, TextEditKind.Deleting, coalesce);
        typedSpaceLast = false;
        lastEnd = caret;
        if (word) Seal();
    }

    /// <summary>Replaces [a, z) with <paramref name="text"/> in <paramref name="style"/>; the caret ends after it.</summary>
    private void Replace(int a, int z, string text, TextRun style, TextEditKind kind, bool coalesce,
        ImmutableArray<TextRun>? insertRuns = null, ImmutableArray<TextHighlightRange>? insertHighlights = null)
    {
        string before = box.Text;
        // A box holds at most ModelLimits.MaxTextLength characters (the document refuses more): what does not fit is
        // left out, cut at a grapheme boundary.
        int room = ModelLimits.MaxTextLength - (before.Length - (z - a));
        if (text.Length > room)
        {
            if (room <= 0) return;
            text = text[..TextUnits.Snap(text, room)];
            if (text.Length == 0) return;
        }
        string after = string.Concat(before.AsSpan(0, a), text, before.AsSpan(z));
        int end = a + text.Length;
        var runs = Splice(box.Runs, a, z, text.Length, style, insertRuns, after);
        var highlights = TextHighlights.Remap(before, after, box.Highlights, end);
        if (insertHighlights is { Length: > 0 } added)
        {
            var updated = box with { Text = after, Runs = runs, Highlights = highlights };
            foreach (var h in added) updated = TextHighlights.Add(updated, a + h.Start, h.Length, h);
            highlights = updated.Highlights;
        }
        int caretAfter = TextUnits.Snap(after, end);
        Commit(box with { Text = after, Runs = runs, Highlights = highlights }, caretAfter, caretAfter, kind, coalesce);
    }

    /// <summary>
    /// Runs after replacing [a, z) of the text with <paramref name="inserted"/> characters: the runs before a, the new
    /// characters in <paramref name="style"/> (or their own runs, offsets from 0), the runs after z moved. Each grapheme
    /// of the result takes the style of its first character, so runs stay on grapheme boundaries.
    /// </summary>
    private static ImmutableArray<TextRun> Splice(ImmutableArray<TextRun> runs, int a, int z, int inserted, TextRun style, ImmutableArray<TextRun>? own, string text)
    {
        if (text.Length == 0) return [];
        var styles = new TextRun?[text.Length];
        foreach (var r in runs)
            for (int i = r.Start; i < r.Start + r.Length; i++)
            {
                if (i < a) styles[i] = r;
                else if (i >= z) styles[i - z + a + inserted] = r;
            }
        for (int i = 0; i < inserted; i++) styles[a + i] = style;
        if (own is { } given)
            foreach (var r in given)
                for (int i = r.Start; i < r.Start + r.Length && i < inserted; i++) styles[a + i] = r;
        var starts = TextUnits.GraphemeStarts(text);
        var result = new List<TextRun>();
        TextRun last = style;
        for (int i = 0; i < text.Length;)
        {
            int next = i + 1;
            while (!starts[next]) next++;
            var s = styles[i] ?? last;
            TextEditing.Add(result, s with { Start = i, Length = next - i });
            last = s;
            i = next;
        }
        return [.. result];
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    // ----- IME composition (S5) -----

    /// <summary>
    /// The text the IME is composing. It is not in <see cref="Box"/> (nor in the document or the Undo steps) until it
    /// is committed; <see cref="DisplayBox"/> shows it at the caret.
    /// </summary>
    public string Composition => composition;

    /// <summary>The IME's cursor inside the composition (UTF-16), or null at its end.</summary>
    public int? CompositionCursor => compositionCursor;

    public bool IsComposing => composition.Length > 0;

    /// <summary>
    /// The IME's composition changed (Avalonia <c>SetPreeditText</c>, Android's composing text). Starting one over a
    /// selection deletes the selection; the committed text then joins that step. An empty text ends the composition.
    /// </summary>
    public void SetComposition(string text, int? cursor = null)
    {
        text = Normalize(text);
        if (text.Length > 0)
        {
            backspaceGuard = false;
            if (composition.Length == 0 && HasSelection)
            {
                var (start, length) = Range;
                if (!typingOverride) typingStyle = TextEditing.StyleAt(box, start) with { Start = 0, Length = 0 };
                var style = typingStyle;
                Replace(start, start + length, "", style, TextEditKind.Typing, coalesce: false);
                typingStyle = style;
                lastEnd = caret;
                typedSpaceLast = false;
            }
        }
        else if (composition.Length > 0) backspaceGuard = true;
        if (text == composition && cursor == compositionCursor) return;
        composition = text;
        compositionCursor = text.Length == 0 || cursor is not { } c ? null : Math.Clamp(c, 0, text.Length);
        CompositionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The IME committed <paramref name="text"/>: it is typed at the caret (Undo counts committed text, not compositions).</summary>
    public void CommitComposition(string text)
    {
        bool had = composition.Length > 0;
        composition = ""; compositionCursor = null;
        backspaceGuard = false;
        if (had) CompositionChanged?.Invoke(this, EventArgs.Empty);
        Type(text);
    }

    /// <summary>The IME dropped its composition (Esc in the IME): nothing is typed.</summary>
    public void CancelComposition()
    {
        backspaceGuard = false; // the IME dropped it: no Backspace of its own follows
        if (composition.Length == 0) return;
        composition = ""; compositionCursor = null;
        CompositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private (TextBoxAnnotation Box, int Caret, string Composition, TextRun Style, TextBoxAnnotation Shown)? display;

    /// <summary>
    /// What the editor shows: <see cref="Box"/> with the composition at the caret in the typing style. The same record
    /// comes back until something changes, so layouts cached per record are reused while composing.
    /// </summary>
    public TextBoxAnnotation DisplayBox
    {
        get
        {
            if (composition.Length == 0) return box;
            if (display is { } d && ReferenceEquals(d.Box, box) && d.Caret == caret && d.Composition == composition && d.Style == typingStyle) return d.Shown;
            string text = box.Text.Insert(caret, composition);
            var shown = box with
            {
                Text = text,
                Runs = Splice(box.Runs, caret, caret, composition.Length, typingStyle, null, text),
                Highlights = TextHighlights.Remap(box.Text, text, box.Highlights, caret + composition.Length),
            };
            display = (box, caret, composition, typingStyle, shown);
            return shown;
        }
    }

    /// <summary>Where the composition is in <see cref="DisplayBox"/>'s text (the control underlines it).</summary>
    public (int Start, int Length) CompositionRange => (caret, composition.Length);

    /// <summary>
    /// The caret while composing, in <see cref="DisplayBox"/>'s layout: at the IME's cursor in the composition, else at
    /// its end. The IME places its candidate window here. Without a composition it is <see cref="CaretRect()"/>.
    /// </summary>
    public Rect2 CompositionCaretRect()
    {
        if (composition.Length == 0) return CaretRect();
        var shown = DisplayBox;
        return CaretRectIn(measure(shown), caret + (compositionCursor ?? composition.Length), false);
    }

    // ----- clipboard -----

    /// <summary>Puts the selection on the clipboard as plain and formatted text. False when nothing is selected.</summary>
    public bool Copy(ITextClipboard clipboard)
    {
        CompleteInput();
        var (start, length) = Range;
        if (length == 0) return false;
        clipboard.Set(box.Text.Substring(start, length), TextClipboardFormat.Write(box, start, start + length));
        return true;
    }

    /// <summary>Copies the selection and deletes it (one Undo step).</summary>
    public bool Cut(ITextClipboard clipboard)
    {
        if (!Copy(clipboard)) return false;
        var (start, length) = Range;
        Replace(start, start + length, "", typingStyle, TextEditKind.Cutting, coalesce: false);
        Seal();
        return true;
    }

    /// <summary>
    /// Pastes at the caret, replacing the selection (one Undo step): this app's formatted copy with its runs and
    /// highlights, otherwise plain text in the typing style (other programs' formatting is dropped, spec 6.1).
    /// </summary>
    public bool Paste(ITextClipboard clipboard)
    {
        CompleteInput();
        var (start, length) = Range;
        var style = length > 0 && !typingOverride ? TextEditing.StyleAt(box, start) with { Start = 0, Length = 0 } : typingStyle;
        if (TextClipboardFormat.Read(clipboard.GetFormatted(), style) is { } rich && rich.Text.Length > 0)
        {
            Seal();
            Replace(start, start + length, Normalize(rich.Text), style, TextEditKind.Pasting, coalesce: false,
                rich.Text.Contains('\r') ? null : rich.Runs, rich.Text.Contains('\r') ? null : rich.Highlights);
        }
        else if (clipboard.GetText() is { Length: > 0 } plain)
        {
            Seal();
            Replace(start, start + length, Normalize(plain), style, TextEditKind.Pasting, coalesce: false);
        }
        else return false;
        Seal();
        return true;
    }

    // ----- Undo while editing -----

    /// <summary>Undoes the last step of this box's editing. Nothing happens at the start (no document Undo, spec 13.2).</summary>
    public bool Undo()
    {
        CompleteInput();
        if (undo.Count == 0) return false;
        var step = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        redo.Add(step);
        Restore(step.Before, TextEditKind.Undo);
        return true;
    }

    public bool Redo()
    {
        CompleteInput();
        if (redo.Count == 0) return false;
        var step = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        undo.Add(step);
        Restore(step.After, TextEditKind.Redo);
        return true;
    }

    private void Restore((TextBoxAnnotation Box, int Anchor, int Caret) step, TextEditKind kind)
    {
        box = step.Box;
        anchor = step.Anchor; caret = step.Caret; upstream = false; goalX = null;
        Seal();
        UpdateTypingStyle();
        Edited?.Invoke(this, new(box, kind, true, undo.Count));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ends the current Undo group: the next change is a new step (caret moves, formatting, paste, IME commit).</summary>
    public void Seal() { sealedGroup = true; lastKind = null; lastEnd = -1; typedSpaceLast = false; }

    private void Commit(TextBoxAnnotation after, int newAnchor, int newCaret, TextEditKind kind, bool coalesce)
    {
        bool changed = !Same(box, after);
        bool newGroup = !(coalesce && kind == lastKind && !sealedGroup);
        if (changed)
        {
            if (newGroup) undo.Add(new Step(box, anchor, caret));
            redo.Clear();
            box = after;
        }
        anchor = newAnchor; caret = newCaret; upstream = false; goalX = null;
        if (changed)
        {
            undo[^1].After = (box, anchor, caret);
            sealedGroup = false;
            lastKind = kind;
            Edited?.Invoke(this, new(box, kind, newGroup, undo.Count));
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool Same(TextBoxAnnotation a, TextBoxAnnotation b) => ReferenceEquals(a, b) ||
        a.Text == b.Text && a.WidthPt == b.WidthPt && a.LayoutScale == b.LayoutScale && a.Alignment == b.Alignment && a.Transform == b.Transform &&
        a.BoxStyle == b.BoxStyle && a.Locked == b.Locked && a.Runs.SequenceEqual(b.Runs) && a.Highlights.SequenceEqual(b.Highlights);

    // ----- selection -----

    /// <summary>Selects [<paramref name="anchorAt"/>, <paramref name="caretAt"/>) snapped to graphemes; the caret goes to the second.</summary>
    public void Select(int anchorAt, int caretAt)
    {
        CompleteInput();
        backspaceGuard = false;
        string text = box.Text;
        anchor = TextUnits.Snap(text, anchorAt);
        caret = TextUnits.Snap(text, caretAt);
        upstream = false; goalX = null; typingOverride = false;
        Seal();
        UpdateTypingStyle();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll() => Select(0, box.Text.Length);

    private void MoveTo(int target, bool extend, bool toUpstream)
    {
        backspaceGuard = false;
        caret = TextUnits.Snap(box.Text, target);
        if (!extend) anchor = caret;
        upstream = toUpstream;
        goalX = null; typingOverride = false;
        Seal();
        UpdateTypingStyle();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>With no selection, typing continues the style of the character before the caret (the first one at the start).</summary>
    private void UpdateTypingStyle()
    {
        if (typingOverride || HasSelection || box.Runs.Length == 0) return;
        typingStyle = TextEditing.StyleAt(box, Math.Max(0, caret - 1)) with { Start = 0, Length = 0 };
    }

    // ----- pointer -----

    /// <summary>
    /// A press at <paramref name="point"/> (box-local points): one click places the caret (Shift extends the selection),
    /// two select the word there, three select all. Dragging after it extends the selection by characters or words.
    /// </summary>
    public void PointerDown(Point2 point, int clickCount = 1, bool shift = false)
    {
        CompleteInput();
        var (at, up) = HitTest(point);
        if (clickCount >= 3) { SelectAll(); drag = DragUnit.None; return; }
        if (clickCount == 2)
        {
            var (s, e) = TextUnits.WordAt(box.Text, at);
            Select(s, e);
            dragOrigin = (s, e);
            drag = DragUnit.Word;
            return;
        }
        MoveTo(at, shift, up);
        dragOrigin = (anchor, anchor);
        drag = DragUnit.Character;
    }

    public void PointerMove(Point2 point)
    {
        if (drag == DragUnit.None) return;
        var (at, up) = HitTest(point);
        if (drag == DragUnit.Word)
        {
            var (s, e) = TextUnits.WordAt(box.Text, at);
            if (at < dragOrigin.Start) { anchor = dragOrigin.End; caret = s; }
            else { anchor = dragOrigin.Start; caret = Math.Max(e, dragOrigin.End); }
        }
        else caret = TextUnits.Snap(box.Text, at);
        upstream = drag == DragUnit.Character && up;
        goalX = null; typingOverride = false;
        Seal();
        UpdateTypingStyle();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PointerUp() => drag = DragUnit.None;

    // ----- geometry (box-local points) -----

    /// <summary>The offset nearest to <paramref name="point"/>, and whether it is the end of a soft-wrapped line.</summary>
    public (int Offset, bool Upstream) HitTest(Point2 point)
    {
        var lines = Layout.Lines;
        if (lines.Length == 0) return (box.Text.Length, false);
        var line = lines[^1];
        foreach (var l in lines)
            if (point.Y < l.Top + l.Height) { line = l; break; }
        return HitLine(line, point.X);
    }

    private (int Offset, bool Upstream) HitLine(TextLayout.Line line, double x)
    {
        var characters = Layout.Characters;
        var starts = TextUnits.GraphemeStarts(box.Text);
        foreach (var (offset, left, right) in Graphemes(characters, starts, line))
            if (x < (left + right) / 2) return (offset, false);
        return (line.End, IsSoftWrapped(line, box.Text));
    }

    /// <summary>The graphemes on a line: start offset and left/right edges.</summary>
    private static IEnumerable<(int Offset, double Left, double Right)> Graphemes(ImmutableArray<TextLayout.Character> characters, bool[] starts, TextLayout.Line line)
    {
        int? offset = null;
        double left = 0, right = 0;
        foreach (var c in characters)
        {
            if (c.Offset < line.Start || c.Offset >= line.End) continue;
            if (starts[c.Offset])
            {
                if (offset is { } o) yield return (o, left, right);
                offset = c.Offset; left = c.Bounds.X; right = c.Bounds.X + c.Bounds.Width;
            }
            else right = Math.Max(right, c.Bounds.X + c.Bounds.Width);
        }
        if (offset is { } last) yield return (last, left, right);
    }

    private static bool IsSoftWrapped(TextLayout.Line line, string text) => line.End < text.Length && !TextUnits.IsLineBreak(text[line.End]) && line.End > line.Start;

    private TextLayout.Line LineOf(int offset, bool atUpstream) => LineIn(Layout, offset, atUpstream);

    private static TextLayout.Line LineIn(TextLayout.Layout layout, int offset, bool atUpstream)
    {
        var lines = layout.Lines;
        return lines.Length == 0 ? new(0, 0, TextLayout.PaddingPt, 0, TextLayout.PaddingPt, TextLayout.PaddingPt, 0) : lines[LineIndex(lines, offset, atUpstream)];
    }

    /// <summary>The line an offset stands on: at a soft wrap the lower line, or the upper one for an upstream caret.</summary>
    private static int LineIndex(ImmutableArray<TextLayout.Line> lines, int offset, bool atUpstream)
    {
        int found = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (offset < lines[i].Start) break;
            if (offset <= lines[i].End)
            {
                found = i;
                bool wrapsHere = offset == lines[i].End && i + 1 < lines.Length && lines[i + 1].Start == offset;
                if (!(wrapsHere && !atUpstream)) return i;
            }
        }
        if (found >= 0) return found;
        // A layout without empty lines (the WPF fallback): the last line that starts before the offset.
        int last = 0;
        for (int i = 0; i < lines.Length; i++) if (lines[i].Start <= offset) last = i;
        return lines.Length == 0 ? 0 : last;
    }

    /// <summary>The caret as a zero-width rectangle (x, line top, 0, line height) in box-local points.</summary>
    public Rect2 CaretRect() => CaretRect(caret, upstream);

    /// <summary>The caret rectangle of any offset (an upstream offset at a soft wrap stands at the end of the upper line).</summary>
    public Rect2 CaretRect(int offset, bool atUpstream = false) => CaretRectIn(Layout, offset, atUpstream);

    private static Rect2 CaretRectIn(TextLayout.Layout layout, int offset, bool atUpstream)
    {
        var line = LineIn(layout, offset, atUpstream);
        return new(CaretXIn(layout, line, offset), line.Top, 0, line.Height);
    }

    private double CaretX(TextLayout.Line line, int offset) => CaretXIn(Layout, line, offset);

    private static double CaretXIn(TextLayout.Layout layout, TextLayout.Line line, int offset)
    {
        double? before = null;
        foreach (var c in layout.Characters)
        {
            if (c.Offset < line.Start || c.Offset >= line.End) continue;
            if (c.Offset == offset) return c.Bounds.X;
            if (c.Offset < offset) before = Math.Max(before ?? double.MinValue, c.Bounds.X + c.Bounds.Width);
        }
        return before ?? line.Left;
    }

    /// <summary>
    /// The selection as one rectangle per line in box-local points. A selected line break shows as a narrow box after
    /// its line's text.
    /// </summary>
    public IReadOnlyList<Rect2> SelectionRects()
    {
        var (start, length) = Range;
        if (length == 0) return [];
        int end = start + length;
        var result = new List<Rect2>();
        foreach (var line in Layout.Lines)
        {
            if (line.End < start || line.Start > end || line.Start == end && line.End > line.Start) continue;
            int a = Math.Max(start, line.Start), z = Math.Min(end, line.End);
            double x0 = CaretX(line, a), x1 = CaretX(line, z);
            if (end > line.End && line.End < box.Text.Length && TextUnits.IsLineBreak(box.Text[line.End])) x1 += line.Height / 4;
            if (x1 > x0) result.Add(new(x0, line.Top, x1 - x0, line.Height));
        }
        return result;
    }

    /// <summary>Where touch handles go (P3/P4 draw them): the caret rectangles at the selection's start and end.</summary>
    public (Rect2 Start, Rect2 End) SelectionEnds()
    {
        var (start, length) = Range;
        return (CaretRect(start), CaretRect(start + length, atUpstream: length > 0));
    }
}
