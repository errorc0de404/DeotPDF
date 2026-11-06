using Avalonia;
using Avalonia.Input.TextInput;

namespace PdfNotes.UI.Canvas;

/// <summary>
/// The first key typed on a selected text box (spec 5.4, D12; roadmap P4 A5). The WPF canvas has no IME and replays the
/// key into the editor; here the canvas itself is an IME client: the composition of that first key (a Korean initial
/// consonant, then the syllable it becomes) goes to <see cref="Preedit"/>, the window starts editing the box and shows it
/// there, and the committed text arrives as a TextInput event as usual. Only then does the keyboard move to the editor,
/// so the IME never resets in the middle of the first syllable.
/// </summary>
public sealed partial class DocumentCanvas
{
    private CanvasIme? ime;

    /// <summary>Composition text from the IME while the canvas has the keyboard (null or empty: none).</summary>
    public event EventHandler<(string? Text, int? Cursor)>? Preedit;

    /// <summary>Where the IME's candidate window goes (canvas DIPs), while a box takes the typing.</summary>
    public Func<Rect>? TypingCaret { get; set; }

    private void InitializeIme() => AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = ime ??= new CanvasIme(this));

    /// <summary>The candidate window should follow the caret again (the box was opened for editing).</summary>
    public void TypingCaretMoved() => ime?.Moved();

    /// <summary>Ends a composition no box takes (a letter typed with nothing to type into): the WPF canvas has no IME at all,
    /// and a pending composition would swallow the next Space, Esc or Delete.</summary>
    public void DropComposition() => Avalonia.Threading.Dispatcher.UIThread.Post(() => ime?.Reset());

    /// <summary>For tests: what the IME would send (a composition, then the commit comes as text input).</summary>
    public void ProbePreedit(string? text) => Preedit?.Invoke(this, (text, null));

    private sealed class CanvasIme(DocumentCanvas canvas) : TextInputMethodClient
    {
        public override Visual TextViewVisual => canvas;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => "";
        public override Rect CursorRectangle => canvas.TypingCaret?.Invoke() ?? default;
        public override TextSelection Selection { get => default; set { } }
        public override void SetPreeditText(string? preeditText) => canvas.Preedit?.Invoke(canvas, (preeditText, null));
        public override void SetPreeditText(string? preeditText, int? cursorPos) => canvas.Preedit?.Invoke(canvas, (preeditText, cursorPos));
        public void Moved() => RaiseCursorRectangleChanged();
        public void Reset() => RequestReset();
    }
}
