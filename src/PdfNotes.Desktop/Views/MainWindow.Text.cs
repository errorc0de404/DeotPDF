using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Input;
using PdfNotes.Editor.Tools;
using PdfNotes.Text;
using PdfNotes.TextEditor;
using PdfNotes.TextEditor.Avalonia;
using PdfNotes.ViewModels;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// Text boxes (roadmap P4 A4): the P2 editor control (<see cref="TextBoxEditor"/>: Skia layout, keys, IME, clipboard with
/// formatting) placed over the canvas at the box's zoom and rotation, and kept in the document by
/// <see cref="TextEditSession"/> — one edit is one document step (spec 13.2). The canvas does not draw the box being
/// edited; the editor does.
/// </summary>
public sealed partial class MainWindow
{
    private TextBoxEditor? textEditor;
    private TextEditSession? editSession;
    private Guid editingPageId;

    /// <summary>The box being edited (tests), or null.</summary>
    public TextBoxAnnotation? EditingText => editSession?.Model.Box;

    public TextBoxEditor? ActiveTextEditor => textEditor;

    public bool IsEditingText => editSession is not null;

    private Guid? EditingTextId => editSession?.Model.Box.Id;

    private void InitializeText()
    {
        Canvas.ViewChanged += (_, _) => PositionTextEditor();
        // A press anywhere on the canvas ends the edit first (spec 6.1); the press then goes on to the tool.
        Canvas.AddHandler(PointerPressedEvent, OnCanvasPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // A key typed with one text box selected starts editing it with that key (spec 5.4, D12); an IME composition of
        // that first key goes through the canvas (DocumentCanvas.Ime) until it is committed.
        AddHandler(TextInputEvent, OnWindowTextInput, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Canvas.Preedit += (_, p) => CanvasPreedit(p.Text, p.Cursor);
        // The canvas lost the keyboard in the middle of the first syllable (a click on a bar field): the syllable stays in the
        // box and the IME lets go of it, so the field's typing is its own.
        Canvas.LostFocus += (_, _) =>
        {
            if (!composingFromCanvas) return;
            composingFromCanvas = false;
            editSession?.Model.CompleteInput();
            Canvas.DropComposition();
        };
        Canvas.BarsChanged += (_, room) => { TextLayer.Margin = room; NoteLayer.Margin = room; };
        Canvas.TypingCaret = () =>
        {
            if (editSession is null || textEditor is null || Document is not { } doc || doc.Session.Current.IndexOfPage(editingPageId) is not (>= 0 and var index)) return default;
            var r = editSession.Model.CompositionCaretRect();
            var at = editSession.Model.Box.Transform.Then(Canvas.PageTransform(index).PageToDip).Transform(new Point2(r.X, r.Y));
            return new Rect(at.X, at.Y, 1, r.Height * textEditor.Zoom);
        };
    }

    /// <summary>The first key's composition is shown in the box (not yet in the document) while the canvas keeps the keyboard.</summary>
    private bool composingFromCanvas;

    /// <summary>The box the first typed key edits: one unlocked text box selected, nothing edited.</summary>
    /// <remarks>Only during a visit from the text tool and with the canvas having the keyboard (spec 5.4 "입력 시작은 자동 전환된 동안만").</remarks>
    private TextBoxAnnotation? TypingTarget => IsEditingText || tools.Current != ToolKind.Select || tools.Return != ToolKind.Text || !Canvas.IsFocused ||
        selectTool.SelectedAnnotations is not [TextBoxAnnotation { Locked: false } box] ? null : box;

    private void CanvasPreedit(string? text, int? cursor)
    {
        if (!composingFromCanvas)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (TypingTarget is not { } box) { Canvas.DropComposition(); return; }
            BeginTextEdit(box.Id, focus: false);
            if (editSession is null) return;
            composingFromCanvas = true;
            Canvas.TypingCaretMoved();
        }
        editSession?.Model.SetComposition(text ?? "", cursor);
        if (string.IsNullOrEmpty(text)) HandKeyboardToEditor(); // the composition was cancelled
    }

    /// <summary>After the first syllable: the editor takes the keyboard (and its own IME client) from here on.</summary>
    private void HandKeyboardToEditor()
    {
        composingFromCanvas = false;
        textEditor?.Focus();
    }

    /// <summary>
    /// A new box from a click (text tool): the caret starts where the user clicked — the box moves left by its padding and
    /// up by the padding plus half a line of the typing style (the WPF rule, 2026-10-15).
    /// </summary>
    public void BeginNewTextAtClick(int pageIndex, Point2 position, double width = 180)
    {
        var style = settings.DefaultText;
        var probe = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Identity, "가", [style with { Start = 0, Length = 1 }], PdfNotes.Core.Models.TextAlignment.Left, 200, TextBoxStyle.Default, []); // font metrics sample, not UI text (D14e)
        double line = Math.Max(0, TextLayout.Measure(probe).Height - 2 * TextLayout.PaddingPt);
        BeginNewText(pageIndex, new(position.X - TextLayout.PaddingPt, position.Y - TextLayout.PaddingPt - line / 2), width);
    }

    public void BeginNewText(int pageIndex, Point2 position, double width = 180)
    {
        FinishTextEditing();
        if (Document is not { } doc) return;
        SetTool(ToolKind.Text); // also from the quick menu or a paste: the text tool and the 필기 tab follow (the WPF window)
        var box = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Translation(position.X, position.Y), "", [], PdfNotes.Core.Models.TextAlignment.Left,
            Math.Clamp(width, 16, 100000), viewModel.TextStyle.NewBoxStyle, []);
        Attach(doc, doc.Session.Current.Pages[pageIndex].Id, TextBoxEditor.Edit(box, settings.DefaultText));
    }

    /// <summary>Starts editing a box; with <paramref name="at"/> (a canvas point) the caret goes there, else to the end.</summary>
    public void BeginTextEdit(Guid id, Point2? at = null, bool focus = true)
    {
        FinishTextEditing();
        if (Document is not { } doc || doc.Session.Current.FindAnnotation(id) is not { Annotation: TextBoxAnnotation { Locked: false } box } location) return;
        // The box stays selected while it is edited and after (spec 5.4, D14g); a visit from the text tool goes back to it
        // only when the box went away.
        editReturnsToText = tools.Current == ToolKind.Select && tools.Return == ToolKind.Text;
        viewModel.Ribbon.Auto(RibbonTab.Home);
        var model = TextBoxEditor.Edit(box, PdfNotes.Core.Commands.TextEditing.StyleAt(box, 0));
        if (at is { } point && Canvas.HitTestPage(point) is { } anchor) model.PointerDown(box.Transform.Invert().Transform(anchor.PagePoint));
        else model.Select(box.Text.Length, box.Text.Length);
        Attach(doc, doc.Session.Current.Pages[location.PageIndex].Id, model, focus);
    }

    private void Attach(PdfNotes.Editor.Services.OpenDocument doc, Guid pageId, TextEditorModel model, bool focus = true)
    {
        editingPageId = pageId;
        editSession = new TextEditSession(doc.Session, pageId, model);
        textEditor = new TextBoxEditor { Model = model, Name = "TextEditor" };
        textEditor.Finished += (_, _) => { FinishTextEditing(); Canvas.Focus(); };
        // The text bar shows the format at the caret (spec 3.2) and follows every edit.
        model.SelectionChanged += (_, _) => textStyle.Refresh();
        model.Edited += (_, _) => { textStyle.Refresh(); Canvas.RefreshOverlays(); }; // the edit frame grows with the text
        textEditor.ContextRequested += (_, e) => { TextContextMenu().Open(textEditor); e.Handled = true; };
        TextLayer.Children.Add(textEditor);
        PositionTextEditor();
        Canvas.RefreshOverlays();
        textStyle.Refresh();
        UpdateStyleBar();
        if (!focus) return; // the canvas keeps the keyboard until the first composition is committed
        textEditor.Focus();
        Dispatcher.UIThread.Post(() =>
        {
            textEditor?.Focus();
            // Started with a finger tap: bring up the touch keyboard (spec 7.6, as the WPF window).
            if (textEditor is not null && Canvas.LastPressDevice == PointerDevice.Touch)
                TouchKeyboardShown = Platform.TouchKeyboard.Show(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        }, DispatcherPriority.Input);
    }

    /// <summary>Whether the last finger-started text edit asked the touch keyboard to show and it agreed (null: not asked).</summary>
    public bool? TouchKeyboardShown { get; private set; }

    /// <summary>Puts the editor over its box: the box's origin, rotation and the page zoom (points → DIPs).</summary>
    private void PositionTextEditor()
    {
        if (textEditor is null || editSession is null || Document is not { } doc) return;
        int index = doc.Session.Current.IndexOfPage(editingPageId);
        if (index < 0 || Canvas.Layout is not { } layout || !layout.IsLaidOut(index)) { textEditor.IsVisible = false; return; }
        var m = editSession.Model.Box.Transform.Then(Canvas.PageTransform(index).PageToDip);
        double scale = Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
        textEditor.IsVisible = true;
        textEditor.Zoom = scale;
        Avalonia.Controls.Canvas.SetLeft(textEditor, m.OffsetX);
        Avalonia.Controls.Canvas.SetTop(textEditor, m.OffsetY);
        textEditor.RenderTransformOrigin = RelativePoint.TopLeft;
        textEditor.RenderTransform = new RotateTransform(Math.Atan2(m.M12, m.M11) * 180 / Math.PI);
    }

    /// <summary>Ends editing: the composition is completed and a blank box removed in the edit's one step (spec 6.1, 13.2).</summary>
    private bool editReturnsToText;

    public void FinishTextEditing()
    {
        CloseNote(); // an open sticky note is finished like a text box (spec 24.9): before a save, a tool change, another document
        // The model commits the first key's composition itself: the OS IME must drop its copy, or the next key goes on from it.
        if (composingFromCanvas) Canvas.DropComposition();
        composingFromCanvas = false;
        if (editSession is not { } session) return;
        editSession = null;
        session.Finish();
        if (textEditor is { } editor)
        {
            bool focused = editor.IsFocused;
            textEditor = null;
            editor.Model = null;
            TextLayer.Children.Remove(editor);
            if (focused) Canvas.Focus(); // keys go back to the canvas (the WPF window does the same)
        }
        Canvas.RefreshOverlays();
        UpdateStyleBar();
        if (editReturnsToText)
        {
            editReturnsToText = false;
            selectTool.Revalidate();
            if (selectTool.SelectedIds.Count == 0) ReturnAfterAutoSelect(); // a blank box went away: back to the text tool
        }
    }

    /// <summary>Window keys while a box is edited: saving ends the edit first; everything else is the editor's.</summary>
    private bool HandleTextKey(KeyEventArgs e)
    {
        var mods = Modifiers(e.KeyModifiers);
        if (mods == PdfNotes.Editor.Input.InputModifiers.Control && e.Key is Key.B or Key.I or Key.U) // the text bar's B I U (spec 6.1)
        { textStyle.ToggleFlag(e.Key == Key.B ? 'B' : e.Key == Key.I ? 'I' : 'U'); e.Handled = true; return true; }
        // Ctrl+Z / Ctrl+Y while the editor does not have the keyboard (a bar field, the first key's composition): the editor's own steps.
        if (textEditor is { IsFocused: false } && !IsFieldFocused && (mods == PdfNotes.Editor.Input.InputModifiers.Control && e.Key is Key.Z or Key.Y ||
            mods == (PdfNotes.Editor.Input.InputModifiers.Control | PdfNotes.Editor.Input.InputModifiers.Shift) && e.Key == Key.Z))
        { UndoTextAware(redo: e.Key == Key.Y || mods.HasFlag(PdfNotes.Editor.Input.InputModifiers.Shift)); e.Handled = true; return true; }
        return false;
    }

    /// <summary>
    /// Text tool press on an existing unlocked box (spec 5.4, D12): the first press selects it with the selection tool
    /// (a drag moves it), a press on the box already selected that way edits it. False: not on a box.
    /// </summary>
    private bool SelectTextAt(CanvasPointer pointer)
    {
        if (Document is null || Canvas.HitTestPage(pointer.Position) is not { } anchor) return false;
        var page = Document.Session.Current.Pages[anchor.PageIndex];
        double tolerance = Canvas.PageTransform(anchor.PageIndex).DipLengthToPoints(SelectTool.PickToleranceDip);
        var box = page.Annotations.OfType<TextBoxAnnotation>().LastOrDefault(t => AnnotationGeometry.Hit(t, anchor.PagePoint, tolerance, TextLayout.Bounds));
        if (box is null || box.Locked) return false;
        SetTool(ToolKind.Select);
        tools.Return = ToolKind.Text;
        selectTool.Select(box.Id);
        return selectTool.PointerDown(pointer with { Modifiers = InputModifiers.None, ClickCount = 1 });
    }

    private void OnCanvasPressed(object? sender, PointerPressedEventArgs e)
    {
        // A left press (or a pen or finger) ends the edit, as the WPF PreviewMouseLeftButtonDown; the middle button pans or
        // erases without ending it. A press on the canvas's scroll bars is not on the pages.
        if (e.Source is Visual v && v.FindAncestorOfType<Avalonia.Controls.Primitives.ScrollBar>(includeSelf: true) is not null) return;
        if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(Canvas).Properties.IsLeftButtonPressed) return;
        FinishTextEditing();
        // PowerPoint-like (spec 5.4, D12; the WPF reclickText): after the text tool selected a box, a press on that box again
        // edits it there once released without a drag. Decided at the press, so a move in between does not matter.
        var pressed = e.GetPosition(Canvas);
        reclickBox = tools.Current == ToolKind.Select && tools.Return == ToolKind.Text && e.ClickCount == 1 &&
            selectTool.SelectedAnnotations is [TextBoxAnnotation selected] && selectTool.Pick(new Point2(pressed.X, pressed.Y)) == selected.Id ? selected.Id : null;
        // A double click on the selected text box edits it at that point (the selection tool would select a word of
        // the PDF under it instead).
        if (e.ClickCount < 2 || tools.Current != ToolKind.Select || selectTool.SelectedAnnotations is not [TextBoxAnnotation box]) return;
        var at = e.GetPosition(Canvas);
        var point = new Point2(at.X, at.Y);
        if (Canvas.HitTestPage(point) is not { } anchor || Document?.Session.Current.Pages[anchor.PageIndex].Annotations.All(a => a.Id != box.Id) != false) return;
        double tolerance = Canvas.PageTransform(anchor.PageIndex).DipLengthToPoints(SelectTool.PickToleranceDip);
        if (!AnnotationGeometry.Hit(box, anchor.PagePoint, tolerance, TextLayout.Bounds)) return;
        e.Handled = true;
        BeginTextEdit(box.Id, point);
    }

    /// <summary>A second press without moving on the box selected from the text tool edits it.</summary>
    private void TextBoxReleased(object? sender, bool changed)
    {
        var again = reclickBox;
        reclickBox = null;
        if (!changed && again is { } id && selectTool.SelectedIds.SequenceEqual([id])) BeginTextEdit(id, Canvas.LastPress);
    }

    private Guid? reclickBox;

    private void OnWindowTextInput(object? sender, TextInputEventArgs e)
    {
        if (composingFromCanvas && editSession is { } session && ReferenceEquals(e.Source, Canvas))
        {
            // The first syllable, committed by the IME to the canvas: into the box, then the editor types on.
            if (!string.IsNullOrEmpty(e.Text) && !e.Text.All(char.IsControl)) session.Model.CommitComposition(e.Text);
            else session.Model.SetComposition("");
            e.Handled = true;
            HandKeyboardToEditor();
            return;
        }
        // A space is the hand for panning, not a typed character (the WPF IsTypingKey has no Space).
        if (string.IsNullOrWhiteSpace(e.Text) || TypingTarget is not { } box) return;
        BeginTextEdit(box.Id);
        editSession?.Model.Type(e.Text);
        e.Handled = true;
    }
}
