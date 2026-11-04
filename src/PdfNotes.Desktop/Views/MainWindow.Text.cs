using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
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
        // A key typed with one text box selected starts editing it with that key (spec 5.4, D12).
        AddHandler(TextInputEvent, OnWindowTextInput, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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
        var box = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Translation(position.X, position.Y), "", [], PdfNotes.Core.Models.TextAlignment.Left,
            Math.Clamp(width, 16, 100000), viewModel.TextStyle.NewBoxStyle, []);
        Attach(doc, doc.Session.Current.Pages[pageIndex].Id, TextBoxEditor.Edit(box, settings.DefaultText));
    }

    /// <summary>Starts editing a box; with <paramref name="at"/> (a canvas point) the caret goes there, else to the end.</summary>
    public void BeginTextEdit(Guid id, Point2? at = null)
    {
        FinishTextEditing();
        if (Document is not { } doc || doc.Session.Current.FindAnnotation(id) is not { Annotation: TextBoxAnnotation { Locked: false } box } location) return;
        selectTool.Select(null);
        var model = TextBoxEditor.Edit(box, PdfNotes.Core.Commands.TextEditing.StyleAt(box, 0));
        if (at is { } point && Canvas.HitTestPage(point) is { } anchor) model.PointerDown(box.Transform.Invert().Transform(anchor.PagePoint));
        else model.Select(box.Text.Length, box.Text.Length);
        Attach(doc, doc.Session.Current.Pages[location.PageIndex].Id, model);
    }

    private void Attach(PdfNotes.Editor.Services.OpenDocument doc, Guid pageId, TextEditorModel model)
    {
        editingPageId = pageId;
        editSession = new TextEditSession(doc.Session, pageId, model);
        textEditor = new TextBoxEditor { Model = model, Name = "TextEditor" };
        textEditor.Finished += (_, _) => { FinishTextEditing(); Canvas.Focus(); };
        TextLayer.Children.Add(textEditor);
        PositionTextEditor();
        Canvas.RefreshOverlays();
        textEditor.Focus();
        Dispatcher.UIThread.Post(() => textEditor?.Focus(), DispatcherPriority.Input);
    }

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
    public void FinishTextEditing()
    {
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
    }

    /// <summary>Window keys while a box is edited: saving ends the edit first; everything else is the editor's.</summary>
    private bool HandleTextKey(KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.S) { FinishTextEditing(); _ = SaveAsync(saveAs: false); e.Handled = true; return true; }
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
        FinishTextEditing();
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
        if (changed || selectTool.SelectedAnnotations is not [TextBoxAnnotation box]) { lastBoxPress = null; return; }
        if (lastBoxPress == box.Id && (tools.Return == ToolKind.Text || Canvas.LastClickCount >= 2)) { lastBoxPress = null; BeginTextEdit(box.Id, Canvas.LastPress); return; }
        lastBoxPress = box.Id;
    }
    private Guid? lastBoxPress;

    private void OnWindowTextInput(object? sender, TextInputEventArgs e)
    {
        if (IsEditingText || string.IsNullOrEmpty(e.Text) || selectTool.SelectedAnnotations is not [TextBoxAnnotation { Locked: false } box]) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return; // another text field
        BeginTextEdit(box.Id);
        editSession?.Model.Type(e.Text);
        e.Handled = true;
    }
}
