using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;
using PdfNotes.Tests.App;
using PdfNotes.TextEditor;
using PdfNotes.TextEditor.Avalonia;
using PdfNotes.ViewModels;
using static PdfNotes.Tests.TextEditor.TextEditorModelTests;

namespace PdfNotes.Tests.TextEditor;

/// <summary>The Avalonia application of the headless tests: Skia drawing, no windowing system.</summary>
public sealed class HeadlessTestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Roadmap P2 S6: the Avalonia editor control under Avalonia.Headless — keys, text, pointer selection, the IME client,
/// drawing and the P1 text bar view model working on it. Real IME is checked by hand (work log S6).
/// </summary>
public sealed class TextBoxEditorHeadlessTests
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp)));

    private static void Run(Action test) => Session.Value.Dispatch(test, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>A shown window with an editor at (20, 20) holding <paramref name="text"/>, zoom 2, focused.</summary>
    private static (Window Window, TextBoxEditor Editor) Show(string text = "", double width = 200)
    {
        var editor = new TextBoxEditor { Zoom = 2, Model = TextBoxEditor.Edit(Box(text, width)) };
        var canvas = new Canvas { Width = 600, Height = 400 };
        Canvas.SetLeft(editor, 20); Canvas.SetTop(editor, 20);
        canvas.Children.Add(editor);
        var window = new Window { Width = 600, Height = 400, Content = canvas };
        window.Show();
        editor.Focus();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (window, editor);
    }

    /// <summary>The window point of a box-local point.</summary>
    private static Point At(TextBoxEditor editor, Point2 local) => new(20 + local.X * editor.Zoom, 20 + local.Y * editor.Zoom);

    [Fact]
    public void KeysAndTextEditTheBox() => Run(() =>
    {
        var (window, editor) = Show();
        window.KeyTextInput("abc 한글");
        window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyTextInput("\r"); // a platform may send the Enter character as text too: not typed twice
        window.KeyTextInput("둘째");
        Assert.Equal("abc 한\n둘째", editor.Model!.Box.Text);
        window.KeyPress(Key.Home, RawInputModifiers.Control, PhysicalKey.Home, null);
        window.KeyPress(Key.Right, RawInputModifiers.Shift, PhysicalKey.ArrowRight, null);
        Assert.Equal((0, 1), editor.Model.Range);
        window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
        Assert.Equal(editor.Model.Box.Text.Length, editor.Model.Range.Length);
        window.KeyTextInput("x");
        Assert.Equal("x", editor.Model.Box.Text);
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        Assert.Equal("abc 한\n둘째", editor.Model.Box.Text);
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.Contains('\t', editor.Model.Box.Text); // Tab types a tab, it does not move the focus
        Assert.True(editor.IsFocused);
        int finished = 0;
        editor.Finished += (_, _) => finished++;
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Equal(1, finished);
        window.Close();
    });

    [Fact]
    public void PointerPlacesTheCaretAndDragSelects() => Run(() =>
    {
        var (window, editor) = Show("한글 문장 English words", 300);
        var model = editor.Model!;
        var m = model.Layout.Characters.Single(c => c.Text == "문");
        var w = model.Layout.Characters.Single(c => c.Text == "w");
        double y = m.Bounds.Y + m.Bounds.Height / 2;
        window.MouseDown(At(editor, new(m.Bounds.X + m.Bounds.Width * .3, y)), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(At(editor, new(w.Bounds.X + 1, y)), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(editor, new(w.Bounds.X + 1, y)), MouseButton.Left, RawInputModifiers.None);
        Assert.Equal((3, w.Offset - 3), model.Range);
        Assert.Equal(300 * 2, editor.DesiredSize.Width, 0.5);
        Assert.Equal(model.Layout.Height * 2, editor.DesiredSize.Height, 1.0); // layout rounding to whole pixels
        window.Close();
    });

    [Fact]
    public void TheImeClientComposesAndPlacesTheCandidateWindow() => Run(() =>
    {
        var (window, editor) = Show("가");
        var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        editor.RaiseEvent(request);
        var client = request.Client!;
        Assert.True(client.SupportsPreedit);
        client.SetPreeditText("나", null);
        Assert.Equal("나", editor.Model!.Composition);
        Assert.Equal("가", client.SurroundingText); // the box only, not the composition
        var caret = editor.Model.CompositionCaretRect();
        Assert.Equal(caret.X * editor.Zoom, client.CursorRectangle.X, 6); // in the control's coordinates
        client.SetPreeditText("", null);
        window.KeyTextInput("나"); // the IME commits
        Assert.Equal("가나", editor.Model.Box.Text);

        // A command that commits the composition asks the IME to drop it (it would commit it again).
        int resets = 0;
        client.ResetRequested += (_, _) => resets++;
        client.SetPreeditText("다", null);
        editor.Model.Format(r => r with { Bold = true });
        Assert.Equal("가나다", editor.Model.Box.Text);
        Assert.Equal(1, resets);
        window.Close();
    });

    [Fact]
    public void TheControlDrawsTheTextAndTheCaret() => Run(() =>
    {
        var (window, editor) = Show("■■■■", 200);
        var frame = window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();
        var pixels = new byte[buffer.RowBytes * buffer.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);
        int dark = 0;
        for (int y = 20; y < 20 + 40; y++)
            for (int x = 20; x < 20 + 160; x++)
                if (pixels[y * buffer.RowBytes + x * 4 + 1] < 100) dark++;
        Assert.True(dark > 500, $"{dark} dark pixels where the text is");
        window.Close();
    });

    /// <summary>The P1 text bar view model on the Avalonia editor through the ITextEditorSession adapter P4 will have.</summary>
    [Fact]
    public void TheTextBarViewModelFormatsTheEditedBox() => Run(() =>
    {
        var (window, editor) = Show("abc def");
        var main = new MainViewModel(new Shell(), new Shell(), new AppSettings(), "DeotPDF", _ => { }, TestFolders.New());
        main.TextStyle.Attach(new Adapter(editor), new SelectTool(new NoCanvas(), () => null));
        editor.Model!.Select(0, 3);
        main.TextStyle.SetFlag('B', true);
        Assert.True(editor.Model.Box.Runs[0].Bold && editor.Model.Box.Runs[0].Length == 3);
        Assert.True(main.TextStyle.Bar.Style.Bold);
        main.TextStyle.SetSize("20");
        Assert.Equal(20, editor.Model.Box.Runs[0].SizePt);
        main.TextStyle.SetAlignment(TextAlignment.Center);
        Assert.Equal(TextAlignment.Center, editor.Model.Box.Alignment);
        main.TextStyle.ToggleHighlight();
        Assert.Equal((0, 3), (editor.Model.Box.Highlights[0].Start, editor.Model.Box.Highlights[0].Length));
        main.TextStyle.SetColorTarget(TextColorTarget.Fill);
        main.TextStyle.SetColor(new RgbColor(240, 200, 100));
        Assert.Equal(new RgbColor(240, 200, 100), editor.Model.Box.BoxStyle.BackgroundColor);
        window.Close();
    });

    private sealed class Adapter(TextBoxEditor editor) : ITextEditorSession
    {
        public bool IsEditing => editor.Model is not null;
        public TextBoxAnnotation Box => editor.Model!.Box;
        public (int Start, int Length) Range => editor.Model!.Range;
        public TextRun TypingStyle => editor.Model!.TypingStyle;
        public void CompleteInput() => editor.Model?.CompleteInput();
        public void Format(Func<TextRun, TextRun> change) => editor.Model!.Format(change);
        public void Change(Func<TextBoxAnnotation, TextBoxAnnotation> change) => editor.Model!.Change(change);
    }

    private sealed class Shell : IMainView, IEditorDialogs
    {
        public void FinishTextEditing() { }
        public void CancelTool() { }
        public void DocumentLeaving(OpenDocument? previous) { }
        public void DocumentReplaced(OpenDocument? previous, OpenDocument next) { }
        public void DocumentChanged() { }
        public void WaitForFirstPages() { }
        public void Opened(string path) { }
        public void Saved(string path) { }
        public bool ShowOpenElsewhere(string path) => false;
        public void CommandsChanged() { }
        public void OnUiThread(Action action) => action();
        public IDocumentViewport Viewport { get; } = new FakeViewport();
        public IPageThumbnailSource Thumbnails { get; } = new FakePageList();
        public IWindowCoordinator Window { get; } = new FakeWindow();
        public Answer Ask(string text, AskButtons buttons, NoticeKind kind) => Answer.Cancel;
        public void Tell(string text, NoticeKind kind) { }
        public string? ChooseSavePath(SavePathRequest request) => null;
    }

    private sealed class NoCanvas : ICanvasView
    {
        public PageAnchor? HitTestPage(Point2 viewportPoint) => null;
        public ViewTransform PageTransform(int index) => throw new NotSupportedException();
        public double DpiScale => 1;
        public Point2 Offset => default;
        public void SetHorizontalOffset(double value) { }
        public void SetVerticalOffset(double value) { }
        public void SetPreview(int pageIndex, ICanvasPreview? preview, bool mark = false) { }
        public void RefreshPreview() { }
        public void RefreshOverlays() { }
        public void RefreshOverlay(int pageIndex) { }
    }
}
