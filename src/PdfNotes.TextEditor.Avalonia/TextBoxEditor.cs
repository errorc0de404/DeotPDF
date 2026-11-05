using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;
using SkiaSharp;
using ImeSelection = Avalonia.Input.TextInput.TextSelection;

namespace PdfNotes.TextEditor.Avalonia;

/// <summary>
/// The Avalonia text box editor (roadmap P2 S6; the P4 app uses it as is). It draws a <see cref="TextEditorModel"/>
/// with the Skia engine — the box, the selection, the IME composition underlined and a blinking caret — and turns
/// pointer, key, text and IME input into the model's neutral input. Box-local points are drawn at <see cref="Zoom"/>
/// device-independent pixels per point; the control is as large as the box.
/// </summary>
public sealed class TextBoxEditor : Control
{
    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<TextBoxEditor, double>(nameof(Zoom), 1.5);

    private static readonly SKColor SelectionColor = new(0x33, 0x99, 0xFF, 0x55), CompositionColor = new(0x46, 0x82, 0xB4);

    private TextEditorModel? model;
    private readonly ImeClient ime;
    private readonly DispatcherTimer blink = new() { Interval = TimeSpan.FromMilliseconds(530) };
    private bool caretShown = true;
    private bool fromIme; // the composition is being changed by the IME itself (no reset request back to it)

    static TextBoxEditor()
    {
        FocusableProperty.OverrideDefaultValue<TextBoxEditor>(true);
        AffectsMeasure<TextBoxEditor>(ZoomProperty);
        AffectsRender<TextBoxEditor>(ZoomProperty);
    }

    public TextBoxEditor()
    {
        ime = new ImeClient(this);
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = ime);
        AddHandler(TextInputEvent, OnText);
        blink.Tick += (_, _) => { caretShown = !caretShown; InvalidateVisual(); };
    }

    /// <summary>Pixels per point (device independent).</summary>
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    /// <summary>The box being edited. Set a model made with <see cref="Edit"/> (its layouts come from the Skia engine).</summary>
    public TextEditorModel? Model
    {
        get => model;
        set
        {
            if (model is not null) { model.Edited -= OnModelChanged; model.SelectionChanged -= OnModelChanged; model.CompositionChanged -= OnComposition; }
            model = value;
            if (model is not null) { model.Edited += OnModelChanged; model.SelectionChanged += OnModelChanged; model.CompositionChanged += OnComposition; }
            Refresh(measure: true);
        }
    }

    /// <summary>Esc: the host ends editing.</summary>
    public event EventHandler? Finished;

    /// <summary>A model that lays out with the Skia engine (the same layouts this control draws).</summary>
    public static TextEditorModel Edit(TextBoxAnnotation box, TextRun? typingStyle = null) => new(box, typingStyle, SkiaMeasure.Layout);

    private void OnModelChanged(object? sender, EventArgs e) => Refresh(measure: e is TextEditedEventArgs);

    private void OnComposition(object? sender, EventArgs e)
    {
        // The model committed the composition on its own (a format change, a click, Enter): the IME must drop it too,
        // or it commits the same text again.
        if (!fromIme && model is { IsComposing: false }) ime.Reset();
        Refresh(measure: true);
    }

    private void Refresh(bool measure)
    {
        caretShown = true;
        if (IsFocused) { blink.Stop(); blink.Start(); }
        if (measure) InvalidateMeasure();
        InvalidateVisual();
        ime.Moved();
    }

    // ----- layout and drawing -----

    private SkiaTextLayout? Shown => model is null ? null : SkiaMeasure.Layout(model.DisplayBox);

    protected override Size MeasureOverride(Size availableSize) =>
        model is null || Shown is not { } layout ? default : new(model.Box.WidthPt * Zoom, layout.Height * Zoom);

    public override void Render(DrawingContext context)
    {
        if (model is null || Shown is not { } layout) return;
        var selection = model.IsComposing ? [] : model.SelectionRects();
        var composition = Underlines(layout, model.CompositionRange);
        var caret = model.IsComposing ? model.CompositionCaretRect() : model.CaretRect();
        bool showCaret = IsFocused && caretShown && !model.HasSelection;
        context.Custom(new Draw(new Rect(Bounds.Size), layout, Zoom, selection, composition, showCaret ? caret : null));
    }

    /// <summary>One bar per line under the composition's characters, in box-local points.</summary>
    private static List<Rect2> Underlines(SkiaTextLayout layout, (int Start, int Length) range)
    {
        var bars = new List<Rect2>();
        if (range.Length == 0) return bars;
        foreach (var line in layout.Characters.Where(c => c.Offset >= range.Start && c.Offset < range.Start + range.Length).GroupBy(c => c.Line))
        {
            double x0 = line.Min(c => c.Bounds.X), x1 = line.Max(c => c.Bounds.X + c.Bounds.Width), y = line.Max(c => c.Bounds.Y + c.Bounds.Height);
            bars.Add(new(x0, y - 1.5, x1 - x0, 1.2));
        }
        return bars;
    }

    private sealed class Draw(Rect bounds, SkiaTextLayout layout, double zoom, IReadOnlyList<Rect2> selection, List<Rect2> composition, Rect2? caret) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            int saved = canvas.Save();
            canvas.Scale((float)zoom);
            SkiaTextRenderer.Draw(canvas, layout);
            using var paint = new SKPaint { IsAntialias = true };
            paint.Color = SelectionColor;
            foreach (var r in selection) canvas.DrawRect(R(r), paint);
            paint.Color = CompositionColor;
            foreach (var r in composition) canvas.DrawRect(R(r), paint);
            if (caret is { } c)
            {
                paint.Color = SKColors.Black;
                paint.IsAntialias = false;
                canvas.DrawRect(new SKRect((float)c.X, (float)c.Y, (float)(c.X + 1 / zoom), (float)(c.Y + c.Height)), paint);
            }
            canvas.RestoreToCount(saved);
        }

        private static SKRect R(Rect2 r) => new((float)r.X, (float)r.Y, (float)(r.X + r.Width), (float)(r.Y + r.Height));
    }

    // ----- input -----

    private Point2 Local(Point p) => new(p.X / Zoom, p.Y / Zoom);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (model is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        model.PointerDown(Local(e.GetPosition(this)), e.ClickCount, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (model is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) model.PointerMove(Local(e.GetPosition(this)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        model?.PointerUp();
        e.Pointer.Capture(null);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e) { base.OnGotFocus(e); Refresh(measure: false); }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        model?.CompleteInput();
        blink.Stop();
        InvalidateVisual();
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        if (model is null) { base.OnKeyDown(e); return; }
        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var modifiers = (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? EditorModifiers.Shift : EditorModifiers.None) |
            (command ? EditorModifiers.Control : EditorModifiers.None);
        EditorKey? key = e.Key switch
        {
            Key.Left => EditorKey.Left, Key.Right => EditorKey.Right, Key.Up => EditorKey.Up, Key.Down => EditorKey.Down,
            Key.Home => EditorKey.Home, Key.End => EditorKey.End, Key.Back => EditorKey.Backspace, Key.Delete => EditorKey.Delete,
            Key.Enter => EditorKey.Enter, Key.Tab when !command => EditorKey.Tab, _ => null,
        };
        if (key is { } k) { model.Key(k, modifiers); e.Handled = true; return; }
        if (e.Key == Key.Escape && !model.IsComposing) { e.Handled = true; Finished?.Invoke(this, EventArgs.Empty); return; }
        if (!command) { base.OnKeyDown(e); return; }
        e.Handled = true;
        switch (e.Key)
        {
            case Key.A: model.SelectAll(); break;
            case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.Y: model.Redo(); break;
            case Key.Z: model.Undo(); break;
            case Key.C: await Copy(cut: false); break;
            case Key.X: await Copy(cut: true); break;
            case Key.V: await Paste(); break;
            default: e.Handled = false; base.OnKeyDown(e); break;
        }
    }

    private void OnText(object? sender, TextInputEventArgs e)
    {
        if (model is null || string.IsNullOrEmpty(e.Text)) return;
        // Enter, Tab and Backspace come as keys; their characters here would be typed twice.
        if (e.Text.All(char.IsControl)) return;
        fromIme = true;
        try { model.CommitComposition(e.Text); }
        finally { fromIme = false; }
        e.Handled = true;
    }

    private void SetPreedit(string? text, int? cursor)
    {
        if (model is null) return;
        fromIme = true;
        try { model.SetComposition(text ?? "", cursor); }
        finally { fromIme = false; }
    }

    // ----- clipboard (Avalonia's is asynchronous: the model gets a snapshot) -----

    private static readonly DataFormat<string> Formatted = DataFormat.CreateStringApplicationFormat(TextClipboardFormat.Name);

    private sealed class Snapshot(string? plain, string? formatted) : ITextClipboard
    {
        public string? Plain { get; private set; } = plain;
        public string? FormattedText { get; private set; } = formatted;
        public void Set(string plain, string formatted) { Plain = plain; FormattedText = formatted; }
        public string? GetFormatted() => FormattedText;
        public string? GetText() => Plain;
    }

    /// <summary>The clipboard commands of the editor's context menu (the same as Ctrl+C, Ctrl+X, Ctrl+V).</summary>
    public Task CopyAsync() => Copy(cut: false);
    public Task CutAsync() => Copy(cut: true);
    public Task PasteAsync() => Paste();

    /// <summary>
    /// Copies (and for a cut then deletes) the selection. The text is deleted only after the clipboard took it; a
    /// clipboard another program holds leaves everything as it was.
    /// </summary>
    private async Task Copy(bool cut)
    {
        if (model is not { } m || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var snapshot = new Snapshot(null, null);
        if (!m.Copy(snapshot)) return;
        var item = new DataTransferItem();
        item.SetText(snapshot.Plain!);
        item.Set(Formatted, snapshot.FormattedText!);
        var data = new DataTransfer();
        data.Add(item);
        try { await clipboard.SetDataAsync(data); }
        catch (Exception) { return; } // the clipboard is busy (async void caller: nothing may escape)
        if (cut && ReferenceEquals(model, m)) m.Cut(new Snapshot(null, null));
    }

    private async Task Paste()
    {
        if (model is not { } m || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        string? formatted = null, plain;
        try { formatted = await clipboard.TryGetValueAsync(Formatted); } catch (Exception) { /* another program's data */ }
        try { plain = await clipboard.TryGetTextAsync(); }
        catch (Exception) { return; }
        if (ReferenceEquals(model, m)) m.Paste(new Snapshot(plain, formatted)); // still the box the paste was asked for
    }

    // ----- IME -----

    /// <summary>The IME side: compositions go to the model, the composition caret places the candidate window.</summary>
    private sealed class ImeClient(TextBoxEditor editor) : TextInputMethodClient
    {
        public override Visual TextViewVisual => editor;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => true;
        public override string SurroundingText => editor.model?.Box.Text ?? "";

        public override Rect CursorRectangle
        {
            get
            {
                if (editor.model is not { } m) return default;
                var r = m.CompositionCaretRect();
                return new Rect(r.X * editor.Zoom, r.Y * editor.Zoom, 1, r.Height * editor.Zoom);
            }
        }

        public override ImeSelection Selection
        {
            get => editor.model is { } m ? new(m.Anchor, m.Caret) : default;
            set => editor.model?.Select(value.Start, value.End);
        }

        public override void SetPreeditText(string? preeditText) => editor.SetPreedit(preeditText, null);
        public override void SetPreeditText(string? preeditText, int? cursorPos) => editor.SetPreedit(preeditText, cursorPos);

        public void Moved() { RaiseCursorRectangleChanged(); RaiseSelectionChanged(); RaiseSurroundingTextChanged(); }

        /// <summary>Asks the IME to drop its composition (the model already committed it).</summary>
        public void Reset() => RequestReset();
    }
}

/// <summary>Skia layouts for the editor, kept per box record (the model asks for the same layout many times per input).</summary>
public static class SkiaMeasure
{
    private static readonly ConditionalWeakTable<TextBoxAnnotation, SkiaTextLayout> Cache = new();

    public static SkiaTextLayout Layout(TextBoxAnnotation box) => Cache.GetValue(box, SkiaTextEngine.Layout);
}
