using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PdfNotes.App.Tools;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Text;
using PdfNotes.App.Themes;
using Alignment = PdfNotes.Core.Models.TextAlignment;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

/// <summary>What the text bar's palette and transparency edit (spec 3.2, 2026-10-13): the characters, the box fill or the box border.</summary>
public enum TextColorTarget { Text, Fill, Border }

public partial class MainWindow
{
    private RichTextBox? textEditor;
    private TextBoxAnnotation? editingText, draftText;
    private int editingPage;
    private bool editReturnsToText; // editing began from a box selected with the text tool: finishing goes back to it
    private (Guid Id, Point At)? reclickText;
    private bool loadingText, composingText, finishingText, syncingTextBar;
    private TextComposition? activeComposition;
    private TextRun? inputStyleOverride;
    private TextRun typingStyle = TextEditing.Default;
    private ComboBox fontBox = null!, alignmentBox = null!;
    private TextBox textSizeBox = null!, textAlphaBox = null!;
    private StackPanel textPalettePanel = null!, textRecentPanel = null!;
    private readonly RadioButton[] textTargetButtons = new RadioButton[3];
    private TextColorTarget textColorTarget = TextColorTarget.Text;
    private readonly Dictionary<string, ToggleButton> textToggles = [];
    private ToggleButton textHighlightButton = null!;
    public RichTextBox? ActiveTextEditor => textEditor;
    public TextBoxAnnotation? EditingText => editingText;
    public TextRun DefaultTextStyle => settings.DefaultText;
    private TextBoxAnnotation? TextTarget => textEditor is not null ? ReadEditor() : selectTool.SelectedAnnotations.OfType<TextBoxAnnotation>().FirstOrDefault();

    private void InitializeTextEditing()
    {
        fontBox = new ComboBox { Width = 124, IsEditable = true, ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(f => f).ToArray(), Margin = new Thickness(0, 0, 6, 0) };
        fontBox.DropDownClosed += (_, _) => ApplyFont();
        fontBox.LostKeyboardFocus += (_, _) => { if (!syncingTextBar) ApplyFont(); };
        fontBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyFont(); textEditor?.Focus(); e.Handled = true; } };
        // Layout (spec 3.2, 2026-10-13): the shape bar's rule [대상 | 팔레트 | 투명도 | 종류별 옵션]. 글자 colors the characters,
        // 채우기/테두리 the box; a color code is typed only to add a color (+). Each group wraps as a whole in a narrow window.
        var targets = Group();
        foreach (var (name, label, target) in new[] { ("TextTargetText", Strings.Get("text.targetText"), TextColorTarget.Text), ("TextTargetFill", Strings.Get("text.targetFill"), TextColorTarget.Fill), ("TextTargetBorder", Strings.Get("text.targetBorder"), TextColorTarget.Border) })
        {
            var button = new RadioButton { Name = name, Content = label, GroupName = "TextColorTarget", Style = (Style)FindResource("ToolButton"), IsChecked = target == textColorTarget };
            button.Checked += (_, _) => { if (!syncingTextBar) SetTextColorTarget(target); };
            textTargetButtons[(int)target] = button;
            targets.Children.Add(button);
        }
        textPalettePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var color in ColorPalette.Basic) textPalettePanel.Children.Add(CreateSwatch(color, ColorName(color), SetTextColor));
        textRecentPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var (custom, customPopup) = CustomColorEntry("TextCustomColor", () => TextTargetColor ?? RgbColor.Black, SetTextColor);
        Group(textPalettePanel, new Separator(), textRecentPanel, custom, customPopup);
        TextStylePanel.Children.Add(new Separator());
        textAlphaBox = Field("TextAlphaBox", "", 40, () => { if (TryParseNumber(textAlphaBox.Text, out double percent)) SetTextTransparency(percent); else UpdateTextStyleBar(); }, Strings.Get("style.transparency"));
        Group(BarLabel(Strings.Get("style.transparency")), textAlphaBox, BarUnit("%"));
        TextStylePanel.Children.Add(new Separator());
        textSizeBox = Field("TextSizeBox", "12", 42, () => { if (double.TryParse(textSizeBox.Text, out double size) && double.IsFinite(size)) ApplyTextFormat(r => r with { SizePt = TextEditing.InputSize(size) }); }, Strings.Get("text.size"));
        Group(fontBox, textSizeBox, BarUnit("pt"));
        TextStylePanel.Children.Add(new Separator());
        var toggles = Group();
        foreach (var (label, tip) in new[] { ("B", Strings.Get("text.bold")), ("I", Strings.Get("text.italic")), ("U", Strings.Get("text.underline")), ("S", Strings.Get("text.strikeOut")) })
        {
            var button = new ToggleButton { Content = label, Width = 27, Focusable = false, ToolTip = tip, Style = (Style)FindResource("ToolButton") };
            button.Click += (_, _) => ApplyTextFormat(r => label switch { "B" => r with { Bold = button.IsChecked == true }, "I" => r with { Italic = button.IsChecked == true }, "U" => r with { Underline = button.IsChecked == true }, _ => r with { Strikethrough = button.IsChecked == true } });
            textToggles[label] = button; toggles.Children.Add(button);
        }
        textHighlightButton = new ToggleButton { Focusable = false, ToolTip = Strings.Get("text.highlight"), Style = (Style)FindResource("ToolButton"),
            Content = new ContentControl { Template = (ControlTemplate)FindResource("Part.Icon.TextHighlight"), Focusable = false } };
        textHighlightButton.Click += (_, _) => ToggleTextHighlight();
        toggles.Children.Add(textHighlightButton);
        TextStylePanel.Children.Add(new Separator());
        // 정렬 as the PowerPoint glyphs (왼쪽·가운데·오른쪽 맞춤): the words did not fit at the 1200 DIP one-row width (D13e)
        alignmentBox = new ComboBox { Name = "TextAlignmentBox", Width = 50, ItemsSource = new[] { "\uE8E4", "\uE8E3", "\uE8E2" }, Margin = new Thickness(0, 0, 4, 0), ToolTip = Strings.Get("text.alignment") };
        alignmentBox.SetResourceReference(FontFamilyProperty, "Font.Icon");
        alignmentBox.SelectionChanged += (_, _) => { if (!syncingTextBar && alignmentBox.SelectedIndex >= 0) ApplyTextBox(b => b with { Alignment = (Alignment)alignmentBox.SelectedIndex }); };
        var slots = new Button { Content = Strings.Get("text.stylesButton"), Focusable = false, ToolTip = Strings.Get("text.savedStyles"), Style = (Style)FindResource("ToolButton") };
        slots.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = slots };
            for (int i = 0; i < 5; i++) { int slot = i; var item = new MenuItem { Header = SlotHeader(i), IsEnabled = settings.TextSlots[i] is not null }; item.Click += (_, _) => ApplyTextSlot(slot); menu.Items.Add(item); }
            menu.IsOpen = true;
        };
        Group(alignmentBox, slots);
        // The densest bar: slightly narrower separators keep it on one row at 1200 DIP with the whole font name visible (D13e).
        TextStylePanel.Resources.Add(typeof(Separator), new Style(typeof(Separator), (Style)FindResource(typeof(Separator)))
            { Setters = { new Setter(MarginProperty, new Thickness(4, 4, 4, 4)) } });
        View.ViewChanged += (_, _) => PositionTextEditor();
        View.PreviewMouseLeftButtonDown += (_, e) =>
        {
            FinishTextEditing();
            // PowerPoint-like (spec 5.4, D12): after the text tool selected a box, clicking it again starts editing at that point.
            reclickText = tools.Current == ToolKind.Select && tools.Return == ToolKind.Text && e.ClickCount == 1 && selectTool.SelectedIds.Count == 1 &&
                selectTool.Pick(At(e.GetPosition(View))) is { } picked && picked == selectTool.SelectedIds.First() &&
                document?.Session.Current.FindAnnotation(picked)?.Annotation is TextBoxAnnotation ? (picked, e.GetPosition(View)) : null;
            if (tools.Current == ToolKind.Select && e.ClickCount == 2 &&
                selectTool.Pick(At(e.GetPosition(View)), includeLocked: true) is { } id && document?.Session.Current.FindAnnotation(id)?.Annotation is TextBoxAnnotation { Locked: false } text)
            { BeginTextEdit(text.Id); e.Handled = true; }
        };
    }
    private TextBox Field(string name, string initial, double width, Action apply, string tip)
    {
        var box = new TextBox { Name = name, Text = initial, Width = width, ToolTip = tip };
        box.LostKeyboardFocus += (_, _) => { if (!syncingTextBar) apply(); };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { apply(); textEditor?.Focus(); e.Handled = true; } };
        return box;
    }

    /// <summary>One wrap unit of the text bar (label, field and unit stay on one line).</summary>
    private StackPanel Group(params UIElement[] items)
    {
        var group = new StackPanel { Style = (Style)FindResource("FieldGroup"), Margin = (Thickness)FindResource("Space.FieldGroup") };
        foreach (var item in items) group.Children.Add(item);
        TextStylePanel.Children.Add(group);
        return group;
    }

    private TextBlock BarLabel(string text) => new() { Text = text, Style = (Style)FindResource("FieldLabel") };

    private TextBlock BarUnit(string text) => new() { Text = text, Style = (Style)FindResource("FieldUnit") };
    private void ApplyFont()
    {
        if (syncingTextBar || string.IsNullOrWhiteSpace(fontBox.Text)) return;
        string family = fontBox.Text;
        if (!TextLayout.FontAvailable(family)) { ShowMessage(Strings.Get("text.fontMissing"), MessageBoxImage.Information); family = "Malgun Gothic"; }
        ApplyTextFormat(r => r with { FontFamily = family });
    }
    public TextColorTarget TextColorTarget => textColorTarget;

    /// <summary>Chooses what the text bar's palette and transparency edit (not document state).</summary>
    public void SetTextColorTarget(TextColorTarget target)
    {
        textColorTarget = target;
        UpdateTextStyleBar();
    }

    /// <summary>The color of the current target, or null when the box has no fill or border to show.</summary>
    private RgbColor? TextTargetColor
    {
        get
        {
            var style = TextTarget?.BoxStyle ?? NewTextBoxStyle;
            return textColorTarget switch
            {
                TextColorTarget.Text => CurrentTextStyle().Color,
                TextColorTarget.Fill => style.BackgroundOpacity > 0 ? style.BackgroundColor : null,
                _ => style.BorderEnabled ? style.BorderColor : null
            };
        }
    }

    /// <summary>
    /// A palette color for the current target. Choosing a fill or border color for a box without one turns it on, opaque
    /// (like shapes). Colors outside the basic palette join the shared user colors.
    /// </summary>
    public void SetTextColor(RgbColor color)
    {
        RememberColor(color);
        settings.Save();
        switch (textColorTarget)
        {
            case TextColorTarget.Text: ApplyTextFormat(r => r with { Color = color }); break;
            case TextColorTarget.Fill: ApplyTextBoxStyle(s => s with { BackgroundColor = color, BackgroundOpacity = s.BackgroundOpacity > 0 ? s.BackgroundOpacity : 1 }); break;
            default: ApplyTextBoxStyle(s => s with { BorderColor = color, BorderOpacity = s.BorderEnabled && s.BorderOpacity > 0 ? s.BorderOpacity : 1, BorderEnabled = true }); break;
        }
        UpdateStyleBar();
    }

    /// <summary>Transparency (0–100%) of the box fill or border; a 100% border is no border. Characters have none.</summary>
    public void SetTextTransparency(double percent)
    {
        if (!double.IsFinite(percent) || textColorTarget == TextColorTarget.Text) return;
        percent = Math.Clamp(percent, 0, 100);
        double opacity = PdfNotes.Core.Models.Opacity.FromTransparencyPercent(percent);
        if (textColorTarget == TextColorTarget.Fill) ApplyTextBoxStyle(s => s with { BackgroundOpacity = opacity });
        else ApplyTextBoxStyle(s => percent >= 100 ? s with { BorderEnabled = false } : s with { BorderEnabled = true, BorderOpacity = opacity });
    }

    /// <summary>Changes the box fill/border of the edited or selected boxes; the next new box starts with it too (2026-10-13 user request, like the shape tool).</summary>
    public void ApplyTextBoxStyle(Func<TextBoxStyle, TextBoxStyle> change)
    {
        var next = change(TextTarget?.BoxStyle ?? NewTextBoxStyle);
        ApplyTextBox(b => b with { BoxStyle = change(b.BoxStyle) });
        settings.TextBox = PdfNotes.Editor.Services.AppSettings.TextBoxSettings.From(next);
        settings.Save();
    }

    public void ApplyTextBoxStyle(TextBoxStyle style) => ApplyTextBoxStyle(_ => style);

    /// <summary>Box fill/border a new text box starts with: the last one set in the text bar (spec 6.2).</summary>
    public TextBoxStyle NewTextBoxStyle => settings.TextBox.ToStyle();
    public void BeginNewText(int pageIndex, Point2 position, double width = 180)
    {
        FinishTextEditing();
        if (document is null) return;
        SetTool(ToolKind.Text);
        editingPage = pageIndex; typingStyle = settings.DefaultText; inputStyleOverride = typingStyle;
        editingText = new(Guid.NewGuid(), false, Affine.Translation(position.X, position.Y), "", [], Alignment.Left, Math.Clamp(width, 16, 100000), NewTextBoxStyle, []);
        AttachEditor();
    }
    /// <summary>
    /// A new box from a click (text tool, quick menu): the caret starts where the user clicked — the box moves left by its
    /// 4pt padding and up by the padding plus half a line of the typing style (2026-10-15 user report: the text started
    /// below and right of the click). <paramref name="position"/> is the click, or the left end of a drag at the press height.
    /// </summary>
    public void BeginNewTextAtClick(int pageIndex, Point2 position, double width = 180)
    {
        var style = settings.DefaultText;
        var probe = new TextBoxAnnotation(Guid.NewGuid(), false, Affine.Identity, "가", [style with { Start = 0, Length = 1 }], Alignment.Left, 200, TextBoxStyle.Default, []); // font metrics sample, not UI text (D14e)
        double line = Math.Max(0, TextLayout.Measure(probe).Height - 2 * TextLayout.PaddingPt);
        BeginNewText(pageIndex, new(position.X - TextLayout.PaddingPt, position.Y - TextLayout.PaddingPt - line / 2), width);
    }

    /// <summary>Starts editing a box; with <paramref name="at"/> (a view point) the caret goes to the character there, else to the end.</summary>
    public void BeginTextEdit(Guid id, Point? at = null)
    {
        FinishTextEditing();
        if (document?.Session.Current.FindAnnotation(id) is not { Annotation: TextBoxAnnotation { Locked: false } box } location) return;
        editReturnsToText = tools.Current == ToolKind.Select && tools.Return == ToolKind.Text;
        AutoRibbonTab(RibbonTab.Home);
        inputStyleOverride = null; editingPage = location.PageIndex; editingText = box; typingStyle = TextEditing.StyleAt(box, 0); AttachEditor();
        if (at is { } point)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (textEditor is null || editingText?.Id != id) return;
                textEditor.UpdateLayout();
                if (textEditor.GetPositionFromPoint(View.TranslatePoint(point, textEditor), snapToText: true) is { } caret) textEditor.CaretPosition = caret;
            });
    }

    /// <summary>
    /// Text tool press on an existing unlocked box (spec 5.4, D12): select it with the selection tool, remember to come
    /// back to the text tool, and let the selection tool take this press (a drag moves the box). False: not on a box.
    /// </summary>
    private bool SelectTextAt(PdfNotes.Editor.Input.CanvasPointer pointer)
    {
        if (document is null || View.HitTestPage(pointer.Position) is not { } anchor) return false;
        var page = document.Session.Current.Pages[anchor.PageIndex];
        double tolerance = View.PageTransform(anchor.PageIndex).DipLengthToPoints(SelectTool.PickToleranceDip);
        var box = page.Annotations.OfType<TextBoxAnnotation>().LastOrDefault(t => AnnotationGeometry.Hit(t, anchor.PagePoint, tolerance, TextLayout.Bounds));
        if (box is null || box.Locked) return false;
        SetTool(ToolKind.Select);
        tools.Return = ToolKind.Text;
        selectTool.Select(box.Id);
        return selectTool.PointerDown(pointer with { Modifiers = InputModifiers.None, ClickCount = 1 });
    }

    /// <summary>A key that types a character: with a box selected from the text tool it starts editing that box (spec 5.4, D12).</summary>
    private static bool IsTypingKey(KeyEventArgs e) =>
        (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None &&
        TypedKey(e) is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.Divide or >= Key.Oem1 and <= Key.OemBackslash;

    private static Key TypedKey(KeyEventArgs e) => e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;

    /// <summary>
    /// Starts editing the selected box with the key that was typed. The canvas has no IME, so the key arrives plain; it is
    /// held and sent again once the editor's IME is ready (ApplicationIdle), where it composes (ㅎ of 한 was lost, D12 probe).
    /// Keys typed before the replayed ones come back are held too and sent after them, so fast typing keeps its order.
    /// </summary>
    private void BeginTextEditWithKey(Guid id, KeyEventArgs e)
    {
        BeginTextEdit(id);
        if (textEditor is null) return;
        e.Handled = true;
        heldKeys = [];
        HoldKey(e);
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, ReleaseHeldKeys);
        // Never swallow typing for long if the replayed keys do not come back (focus moved to another program).
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; var hold = heldKeys;
        timeout.Tick += (_, _) => { timeout.Stop(); if (heldKeys == hold) { heldKeys = null; keysReleased = false; outstandingKeys = 0; } };
        timeout.Start();
    }

    private const nint ReplayedKeyTag = 0x504E4B59;
    private List<(ushort Key, bool Shift)>? heldKeys;
    private bool keysReleased;
    private int outstandingKeys;

    private void HoldKey(KeyEventArgs e)
    {
        var key = (ushort)KeyInterop.VirtualKeyFromKey(TypedKey(e));
        if (key == 0) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (keysReleased) Replay([(key, shift)]); else heldKeys!.Add((key, shift));
    }

    /// <summary>Window key filter while keys are held: replayed keys pass, other character keys wait their turn.</summary>
    private bool FilterHeldKey(KeyEventArgs e)
    {
        if (heldKeys is null) return false;
        if (GetMessageExtraInfo() == ReplayedKeyTag)
        {
            if (e.Key is Key.LeftShift or Key.RightShift) return false;
            if (--outstandingKeys <= 0 && keysReleased) { heldKeys = null; keysReleased = false; outstandingKeys = 0; }
            return false;
        }
        // A key the editor's IME already took (ImeProcessed) cannot wait; it only happens when typing faster than the
        // dispatcher reaches idle, a few milliseconds.
        if (textEditor is null || e.Key == Key.ImeProcessed || !(IsTypingKey(e) || e.Key is Key.Space or Key.Back or Key.Enter)) return false;
        HoldKey(e);
        e.Handled = true;
        return true;
    }

    private void ReleaseHeldKeys()
    {
        if (heldKeys is null) return;
        keysReleased = true;
        if (textEditor is null) { heldKeys = null; keysReleased = false; return; }
        Replay([.. heldKeys]); heldKeys.Clear();
        if (outstandingKeys == 0) { heldKeys = null; keysReleased = false; }
    }

    private void Replay(IReadOnlyList<(ushort Key, bool Shift)> keys)
    {
        var inputs = new List<NativeInput>();
        void Add(ushort key, bool up) => inputs.Add(new() { Type = 1, Keyboard = new() { Key = key, Scan = (ushort)MapVirtualKey(key, 0), Flags = up ? 2u : 0, Extra = ReplayedKeyTag } });
        foreach (var (key, shift) in keys)
        {
            // The Shift state when the key was typed, whatever it is now.
            bool now = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
            if (shift != now) Add(0x10, !shift);
            Add(key, false); Add(key, true);
            if (shift != now) Add(0x10, shift);
            outstandingKeys++;
        }
        SendInput((uint)inputs.Count, [.. inputs], System.Runtime.InteropServices.Marshal.SizeOf<NativeInput>());
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeKeyboardInput { public ushort Key; public ushort Scan; public uint Flags; public uint Time; public nint Extra; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 40)]
    private struct NativeInput { [System.Runtime.InteropServices.FieldOffset(0)] public uint Type; [System.Runtime.InteropServices.FieldOffset(8)] public NativeKeyboardInput Keyboard; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
    private void AttachEditor()
    {
        if (editingText is not { } box) return;
        loadingText = true;
        textEditor = new PageTextEditor { Document = WpfText.EditorDocument(box, typingStyle), IsUndoEnabled = false, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Background = Brushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            AcceptsTab = true, Width = box.WidthPt / box.LayoutScale, MinHeight = 20,
            // The editor sits on the paper: its caret and inherited text color never follow the dark theme (D13d).
            CaretBrush = Theme.Brush("Overlay.Caret"), Foreground = Theme.Brush("Overlay.Caret") };
        TextOptions.SetTextFormattingMode(textEditor, TextFormattingMode.Ideal);
        var editor = textEditor;
        // Typing WPF still had queued can reach an editor that editing already left (keys typed right before Esc).
        textEditor.TextChanged += (_, _) => { if (editor != textEditor) return; if (!loadingText && !composingText) FlushTextEdit(); PositionTextEditor(); };
        textEditor.SelectionChanged += (_, _) =>
        {
            if (loadingText || editor != textEditor || editingText is null) return;
            var (start, length) = EditorRange();
            if (length == 0) typingStyle = TextEditing.StyleAt(ReadEditor(), Math.Max(0, start - 1)) with { Start = 0, Length = 0 };
            UpdateTextStyleBar();
        };
        TextCompositionManager.AddPreviewTextInputStartHandler(textEditor, (_, e) => { if (e.TextComposition.CompositionText.Length > 0 || e.TextComposition.Text.Length == 0) { document?.Session.SealCoalescing(); composingText = true; activeComposition = e.TextComposition; } });
        TextCompositionManager.AddPreviewTextInputUpdateHandler(textEditor, (_, e) => { composingText = e.TextComposition.CompositionText.Length > 0 || e.TextComposition.Text.Length == 0; activeComposition = composingText ? e.TextComposition : null; });
        textEditor.AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler((_, _) =>
        {
            bool wasComposition = composingText; composingText = false; activeComposition = null;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { FlushTextEdit(); if (wasComposition) document?.Session.SealCoalescing(); });
        }), handledEventsToo: true);
        textEditor.PreviewMouseLeftButtonDown += (_, _) => { inputStyleOverride = null; document?.Session.SealCoalescing(); };
        textEditor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End) { inputStyleOverride = null; document?.Session.SealCoalescing(); }
            if (e.Key == Key.Escape && !composingText) { FinishTextEditing(); e.Handled = true; }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.B or Key.I or Key.U)
            {
                var r = CurrentTextStyle(); ApplyTextFormat(t => e.Key switch { Key.B => t with { Bold = !r.Bold }, Key.I => t with { Italic = !r.Italic }, _ => t with { Underline = !r.Underline } }); e.Handled = true;
            }
        };
        DataObject.AddPastingHandler(textEditor, (_, e) =>
        {
            e.CancelCommand(); document?.Session.SealCoalescing();
            if (e.DataObject.GetData(DataFormats.UnicodeText) is string plain)
            { textEditor.Selection.Text = Normalize(plain); textEditor.CaretPosition = textEditor.Selection.End; FlushTextEdit(); document?.Session.SealCoalescing(); }
        });
        textEditor.ContextMenuOpening += (_, _) => textEditor.ContextMenu = TextContextMenu();
        textEditor.ContextMenu = TextContextMenu();
        TextEditorLayer.Children.Add(textEditor); loadingText = false;
        PositionTextEditor(); textEditor.Focus(); textEditor.CaretPosition = textEditor.Document.ContentEnd;
        View.RefreshOverlays(); UpdateStyleBar();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { PositionTextEditor(); textEditor?.Focus(); });
    }
    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private TextBoxAnnotation ReadEditor()
    {
        if (textEditor is null || editingText is null) throw new InvalidOperationException();
        var content = new StringBuilder(); var styles = new List<TextRun>();
        void Append(string text, TextRun style)
        {
            text = Normalize(text); int start = content.Length; content.Append(text); TextEditing.Add(styles, style with { Start = start, Length = text.Length });
        }
        void ReadInline(Inline inline)
        {
            var c = inline.Foreground is SolidColorBrush brush ? new RgbColor(brush.Color.R, brush.Color.G, brush.Color.B) : RgbColor.Black;
            var style = new TextRun(0, 0, inline.FontFamily.Source, inline.FontSize * editingText.LayoutScale, c, inline.FontWeight >= FontWeights.Bold, inline.FontStyle == FontStyles.Italic,
                inline.TextDecorations.Any(d => d.Location == TextDecorationLocation.Underline), inline.TextDecorations.Any(d => d.Location == TextDecorationLocation.Strikethrough));
            if (inline is Run run) Append(run.Text, style);
            else if (inline is LineBreak) Append("\n", style);
            else if (inline is Span span) foreach (var child in span.Inlines) ReadInline(child);
        }
        bool firstParagraph = true;
        foreach (var block in textEditor.Document.Blocks)
        {
            if (block is not Paragraph paragraph) continue;
            if (!firstParagraph) Append("\n", styles.LastOrDefault() ?? typingStyle);
            firstParagraph = false;
            foreach (var inline in paragraph.Inlines) ReadInline(inline);
        }
        string value = content.ToString();
        // An editor may split a surrogate pair/combining cluster between runs; normalize to cluster-start formatting.
        var result = new List<TextRun>(); var boundaries = StringInfo.ParseCombiningCharacters(value);
        for (int i = 0; i < boundaries.Length; i++)
        {
            int start = boundaries[i], end = i + 1 < boundaries.Length ? boundaries[i + 1] : value.Length;
            var style = styles.FirstOrDefault(r => r.Start <= start && r.Start + r.Length > start) ?? typingStyle;
            TextEditing.Add(result, style with { Start = start, Length = end - start });
        }
        // Highlights follow their characters (spec 9.4); the caret decides ambiguous edits next to equal characters.
        int caret = Normalize(new TextRange(textEditor.Document.ContentStart, textEditor.CaretPosition).Text).Length;
        var current = editingText with { Text = value, Runs = result.ToImmutableArray(),
            Highlights = TextHighlights.Remap(editingText.Text, value, editingText.Highlights, caret) };
        if (SameText(editingText, current)) return editingText;
        if (draftText is not null && SameText(draftText, current)) return draftText;
        return draftText = current;
    }
    private (int Start, int Length) EditorRange()
    {
        if (textEditor is null) return (0, 0);
        int start = Normalize(new TextRange(textEditor.Document.ContentStart, textEditor.Selection.Start).Text).Length;
        int length = Normalize(textEditor.Selection.Text).Length;
        var text = ReadEditor().Text; var (a, z) = TextEditing.Range(text, start, length); return (a, z - a);
    }
    private TextPointer PointerAt(int index)
    {
        var doc = textEditor!.Document;
        for (var p = doc.ContentStart; p is not null; p = p.GetNextInsertionPosition(LogicalDirection.Forward))
            if (Normalize(new TextRange(doc.ContentStart, p).Text).Length >= index) return p;
        return doc.ContentEnd;
    }
    private static bool SameText(TextBoxAnnotation a, TextBoxAnnotation b) => a.Id == b.Id && a.Locked == b.Locked && a.Text == b.Text && a.WidthPt == b.WidthPt && a.LayoutScale == b.LayoutScale && a.Alignment == b.Alignment && a.Transform == b.Transform && a.BoxStyle == b.BoxStyle && a.Runs.SequenceEqual(b.Runs) && a.Highlights.SequenceEqual(b.Highlights);

    public void FlushTextEdit()
    {
        if (loadingText || composingText || textEditor is null || editingText is null || document is null) return;
        var after = ReadEditor();
        if (inputStyleOverride is { } inputStyle && after.Text != editingText.Text)
        {
            // WPF may clear caret springloaded formatting during reflow. Reapply it only to the newly committed
            // text after IME completion; never replace a document while a composition is active.
            string priorText = editingText.Text; int prefix = 0, suffix = 0;
            while (prefix < priorText.Length && prefix < after.Text.Length && priorText[prefix] == after.Text[prefix]) prefix++;
            while (suffix < priorText.Length - prefix && suffix < after.Text.Length - prefix && priorText[^(suffix + 1)] == after.Text[^(suffix + 1)]) suffix++;
            int count = after.Text.Length - prefix - suffix;
            if (count > 0)
            {
                var (a, z) = TextEditing.Range(after.Text, prefix, count);
                loadingText = true;
                try { SetRangeFormat(new TextRange(PointerAt(a), PointerAt(z)), inputStyle); }
                finally { loadingText = false; }
                after = ReadEditor();
            }
        }
        if (after.IsBlank) return; // Removal is deferred until editing ends; keep prior nonblank content for one-step undo.
        if (ModelValidator.ValidateAnnotation(after, document.Session.Current.Resources.ContainsKey) is not null) return;
        var existing = document.Session.Current.FindAnnotation(after.Id);
        if (existing?.Annotation is TextBoxAnnotation prior && SameText(prior, after)) { editingText = prior; return; }
        var command = existing is null ? DocumentEdits.AddAnnotation(document.Session.Current, document.Session.Current.Pages[editingPage].Id, after, label: "undo.typeText")
            : DocumentEdits.UpdateAnnotations(document.Session.Current, [after], "undo.typeText", "text:" + after.Id);
        if (existing is null) command = new EditCommand(command.Label, command.Operations, "text:" + after.Id);
        editingText = after; document.Session.Execute(command);
    }
    public void FinishTextEditing()
    {
        CloseNote(); // an open sticky note is finished like a text box (spec 24.9)
        if (finishingText || textEditor is null || document is null) return;
        finishingText = true;
        try
        {
            // Moving native focus completes the TSF/IME composition before reading its final characters.
            CompleteTextInput(); View.Focus(); composingText = false; FlushTextEdit();
            var after = ReadEditor();
            if (after.IsBlank && document.Session.Current.FindAnnotation(after.Id) is not null)
                document.Session.Execute(DocumentEdits.DeleteAnnotations(document.Session.Current, [after.Id], "undo.deleteEmptyText"));
            TextEditorLayer.Children.Clear(); textEditor = null; editingText = null; draftText = null;
            document.Session.SealCoalescing(); View.RefreshOverlays(); UpdateStyleBar();
            // The box stays selected after editing (spec 5.4, D14g); only a box that went away ends the visit.
            if (editReturnsToText) { editReturnsToText = false; if (selectTool.SelectedIds.Count == 0) ReturnAfterAutoSelect(); }
        }
        finally { finishingText = false; }
    }
    private void PositionTextEditor()
    {
        if (textEditor is null || editingText is null || document is null) return;
        var box = ReadEditor(); var transform = Affine.Scale(box.LayoutScale).Then(box.Transform).Then(View.PageTransform(editingPage).PageToDip);
        var origin = View.TranslatePoint(new Point(0, 0), TextEditorLayer);
        textEditor.ApplyTemplate();
        if (textEditor.Document.PagePadding != new Thickness(4)) textEditor.Document.PagePadding = new Thickness(4);
        textEditor.Width = box.WidthPt / box.LayoutScale; textEditor.Height = TextLayout.Measure(box).Height / box.LayoutScale;
        textEditor.RenderTransform = new MatrixTransform(new Matrix(transform.M11, transform.M12, transform.M21, transform.M22, transform.OffsetX + origin.X, transform.OffsetY + origin.Y));
        textEditor.Background = Brushes.Transparent;
        var setup = document.Session.Current.Pages[editingPage].Setup;
        var corners = AnnotationGeometry.Corners(new(0, 0, setup.WidthPt, setup.HeightPt), box.Transform.Invert().Then(Affine.Scale(1 / box.LayoutScale)));
        var clip = new StreamGeometry(); using (var c = clip.Open()) { c.BeginFigure(new(corners[0].X, corners[0].Y), true, true); c.PolyLineTo(corners.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(), true, false); }
        textEditor.Clip = clip;
        View.RefreshOverlays();
        textEditor.BorderBrush = WpfText.Brush(box.BoxStyle.BorderColor, box.BoxStyle.BorderOpacity);
        // Keep the editor's content origin identical to the document padding; border is drawn by a separate overlay.
    }
    private TextRun CurrentTextStyle()
    {
        if (textEditor is not null) { var box = ReadEditor(); var (start, length) = EditorRange(); return length > 0 ? TextEditing.StyleAt(box, start) : typingStyle; }
        return TextTarget is { } target ? TextEditing.StyleAt(target, 0) : settings.DefaultText;
    }
    private void CompleteTextInput()
    {
        if (!composingText || textEditor is null) return;
        if (activeComposition is { } composition) TextCompositionManager.CompleteComposition(composition);
        View.Focus(); textEditor?.Focus(); composingText = false; activeComposition = null; FlushTextEdit();
    }
    public void ApplyTextFormat(Func<TextRun, TextRun> change)
    {
        CompleteTextInput();
        document?.Session.SealCoalescing();
        if (textEditor is not null && editingText is not null)
        {
            var box = ReadEditor(); var (start, length) = EditorRange(); typingStyle = change(CurrentTextStyle()) with { Start = 0, Length = 0 };
            if (length > 0)
            {
                inputStyleOverride = null;
                box = TextEditing.Format(box, start, length, change); ReplaceEditor(box, start, length); FlushTextEdit();
            }
            else { inputStyleOverride = typingStyle; loadingText = true; try { SetCaretFormat(typingStyle); } finally { loadingText = false; } }
        }
        else if (document is not null && selectTool.SelectedAnnotations.OfType<TextBoxAnnotation>().Any())
            document.Session.Execute(DocumentEdits.UpdateAnnotations(document.Session.Current, selectTool.SelectedAnnotations.OfType<TextBoxAnnotation>().Select(b => TextEditing.Format(b, 0, b.Text.Length, change)), "undo.textFormat"));
        else typingStyle = change(typingStyle);
        document?.Session.SealCoalescing(); UpdateTextStyleBar();
    }
    private void SetCaretFormat(TextRun r)
    {
        SetRangeFormat(textEditor!.Selection, r);
    }
    private void SetRangeFormat(TextRange selection, TextRun r)
    {
        selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily(r.FontFamily));
        selection.ApplyPropertyValue(TextElement.FontSizeProperty, r.SizePt / (editingText?.LayoutScale ?? 1));
        selection.ApplyPropertyValue(TextElement.ForegroundProperty, WpfText.Brush(r.Color));
        selection.ApplyPropertyValue(TextElement.FontWeightProperty, r.Bold ? FontWeights.Bold : FontWeights.Normal);
        selection.ApplyPropertyValue(TextElement.FontStyleProperty, r.Italic ? FontStyles.Italic : FontStyles.Normal);
        var d = new TextDecorationCollection(); if (r.Underline) d.Add(TextDecorations.Underline[0]); if (r.Strikethrough) d.Add(TextDecorations.Strikethrough[0]);
        selection.ApplyPropertyValue(Inline.TextDecorationsProperty, d);
    }
    private void ReplaceEditor(TextBoxAnnotation box, int start, int length)
    {
        loadingText = true;
        try { editingText = box; textEditor!.Document = WpfText.EditorDocument(box, typingStyle); textEditor.Selection.Select(PointerAt(start), PointerAt(start + length)); }
        finally { loadingText = false; }
        PositionTextEditor();
    }
    public void ApplyTextBox(Func<TextBoxAnnotation, TextBoxAnnotation> change)
    {
        CompleteTextInput();
        if (textEditor is not null) { var range = EditorRange(); ReplaceEditor(change(ReadEditor()), range.Start, range.Length); FlushTextEdit(); }
        else if (document is not null) document.Session.Execute(DocumentEdits.UpdateAnnotations(document.Session.Current, selectTool.SelectedAnnotations.OfType<TextBoxAnnotation>().Select(change), "undo.textBoxStyle"));
        document?.Session.SealCoalescing(); UpdateTextStyleBar();
    }
    /// <summary>
    /// Highlights the selected characters of the edited box with the highlight tool's style, or removes the highlight
    /// when they are all highlighted already (spec 9.4). Without an editor it applies to whole selected boxes.
    /// </summary>
    public void ToggleTextHighlight()
    {
        CompleteTextInput();
        document?.Session.SealCoalescing();
        var s = TextHighlightToolStyle;
        var style = new TextHighlightRange(0, 0, s.Mode, s.ManualHeight, s.Style.Color, s.Style.Opacity);
        TextBoxAnnotation Toggle(TextBoxAnnotation box, int start, int length) =>
            TextHighlights.Covers(box, start, length) ? TextHighlights.Remove(box, start, length) : TextHighlights.Add(box, start, length, style);
        if (textEditor is not null && editingText is not null)
        {
            var (start, length) = EditorRange();
            if (length > 0) { ReplaceEditor(Toggle(ReadEditor(), start, length), start, length); FlushTextEdit(); }
        }
        else if (document is not null && selectTool.SelectedAnnotations.OfType<TextBoxAnnotation>().ToArray() is { Length: > 0 } boxes)
            document.Session.Execute(DocumentEdits.UpdateAnnotations(document.Session.Current, boxes.Select(b => Toggle(b, 0, b.Text.Length)), "undo.textHighlight"));
        document?.Session.SealCoalescing(); UpdateTextStyleBar(); View.RefreshOverlays();
    }

    public void SaveTextStyle(int? slot)
    {
        CompleteTextInput();
        var style = CurrentTextStyle() with { Start = 0, Length = 0 };
        if (slot is { } i) settings.TextSlots[i] = style; else settings.DefaultText = style;
        settings.Save(); StatusText.Text = slot is { } number ? Strings.Format("text.slotSaved", ("number", number + 1)) : Strings.Get("text.defaultSaved");
    }
    public void ApplyTextSlot(int slot) { if (settings.TextSlots[slot] is { } r) ApplyTextFormat(old => r with { Start = old.Start, Length = old.Length }); }
    /// <summary>
    /// A style slot's menu header drawn in that slot's format (spec 6.3, D14g): font, size (kept readable), color (a
    /// near-white color shows in the menu's text color), bold, italic, underline and strikethrough. An empty slot is plain.
    /// </summary>
    private object SlotHeader(int index)
    {
        string name = $"Slot {index + 1}"; // slot names are fixed, as in the spec
        if (settings.TextSlots[index] is not { } run) return name;
        var block = new TextBlock
        {
            Text = name, FontFamily = new FontFamily(run.FontFamily), FontSize = Math.Clamp(run.SizePt * 96 / 72, 11, 20),
            FontWeight = run.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = run.Italic ? FontStyles.Italic : FontStyles.Normal
        };
        var decorations = new TextDecorationCollection();
        if (run.Underline) decorations.Add(TextDecorations.Underline);
        if (run.Strikethrough) decorations.Add(TextDecorations.Strikethrough);
        block.TextDecorations = decorations;
        if (0.2126 * run.Color.R + 0.7152 * run.Color.G + 0.0722 * run.Color.B < 230) block.Foreground = WpfText.Brush(run.Color, 1);
        return block;
    }

    private MenuItem SavedTextStylesMenu(bool enabled = true)
    {
        var saved = new MenuItem { Header = Strings.Get("text.savedStyles"), IsEnabled = enabled };
        for (int i = 0; i < settings.TextSlots.Length; i++)
        {
            int slot = i;
            var item = new MenuItem { Header = SlotHeader(i), IsEnabled = settings.TextSlots[i] is not null };
            item.Click += (_, _) => ApplyTextSlot(slot);
            saved.Items.Add(item);
        }
        return saved;
    }
    public ContextMenu TextContextMenu()
    {
        CompleteTextInput();
        var menu = new ContextMenu();
        menu.Items.Add(SavedTextStylesMenu());
        var save = new MenuItem { Header = Strings.Get("text.saveStyle") };
        for (int i = 0; i < 5; i++) { int index = i; var item = new MenuItem { Header = SlotHeader(i) }; item.Click += (_, _) => SaveTextStyle(index); save.Items.Add(item); }
        menu.Items.Add(save); var def = new MenuItem { Header = Strings.Get("text.makeDefault") }; def.Click += (_, _) => SaveTextStyle(null); menu.Items.Add(def);
        menu.Items.Add(new Separator());
        foreach (var command in new[] { ApplicationCommands.Copy, ApplicationCommands.Cut, ApplicationCommands.Paste, ApplicationCommands.SelectAll })
            menu.Items.Add(new MenuItem { Header = command == ApplicationCommands.Copy ? Strings.Get("command.copy") : command == ApplicationCommands.Cut ? Strings.Get("command.cut") : command == ApplicationCommands.Paste ? Strings.Get("command.paste") : Strings.Get("command.selectAll"), Command = command, CommandTarget = textEditor });
        return menu;
    }
    private void UpdateTextStyleBar()
    {
        if (fontBox is null) return;
        bool visible = tools.Current == ToolKind.Text || textEditor is not null || selectTool.SelectedAnnotations.Any(a => a is TextBoxAnnotation);
        TextStyleBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TextStyleBorder.IsEnabled = textEditor is not null || selectTool.SelectedAnnotations.OfType<TextBoxAnnotation>().Any();
        if (!visible) { textRecentPanel.Children.Clear(); return; } // rebuilt with the current user colors when shown
        syncingTextBar = true;
        try
        {
            var r = CurrentTextStyle(); var box = TextTarget;
            if (!fontBox.IsKeyboardFocusWithin) fontBox.Text = r.FontFamily;
            if (!textSizeBox.IsKeyboardFocusWithin) textSizeBox.Text = r.SizePt.ToString("0.##", CultureInfo.CurrentCulture);
            textHighlightButton.IsChecked = textEditor is not null && box is not null && EditorRange() is { Length: > 0 } range
                ? TextHighlights.Covers(box, range.Start, range.Length) : box is not null && TextHighlights.Covers(box, 0, box.Text.Length);
            textToggles["B"].IsChecked = r.Bold; textToggles["I"].IsChecked = r.Italic; textToggles["U"].IsChecked = r.Underline; textToggles["S"].IsChecked = r.Strikethrough;
            alignmentBox.SelectedIndex = (int)(box?.Alignment ?? Alignment.Left);
            var style = box?.BoxStyle ?? NewTextBoxStyle;
            for (int i = 0; i < textTargetButtons.Length; i++) textTargetButtons[i].IsChecked = i == (int)textColorTarget;
            var color = TextTargetColor;
            foreach (Button swatch in textPalettePanel.Children) MarkSwatch(swatch, Equals(swatch.DataContext, color));
            textRecentPanel.Children.Clear();
            foreach (var recent in recentColors)
            {
                var swatch = CreateSwatch(recent, recent.ToHex(), SetTextColor, removable: true);
                MarkSwatch(swatch, recent == color);
                textRecentPanel.Children.Add(swatch);
            }
            textAlphaBox.IsEnabled = textColorTarget != TextColorTarget.Text;
            if (!textAlphaBox.IsKeyboardFocusWithin)
                textAlphaBox.Text = textColorTarget switch
                {
                    TextColorTarget.Fill => FormatNumber(PdfNotes.Core.Models.Opacity.ToTransparencyPercent(style.BackgroundOpacity)),
                    TextColorTarget.Border => style.BorderEnabled ? FormatNumber(PdfNotes.Core.Models.Opacity.ToTransparencyPercent(style.BorderOpacity)) : "100",
                    _ => ""
                };
        }
        finally { syncingTextBar = false; }
    }
    /// <summary>Plain text from the clipboard as a new text box whose first line starts at <paramref name="at"/> (null: the page middle).</summary>
    private void PastePlainTextBox(string plain, int pageIndex, Point2? at)
    {
        var page = document!.Session.Current.Pages[pageIndex];
        if (at is { } point) BeginNewTextAtClick(pageIndex, point);
        else BeginNewText(pageIndex, new(page.Setup.WidthPt / 2 - 90, page.Setup.HeightPt / 2));
        textEditor!.Selection.Text = Normalize(plain); textEditor.CaretPosition = textEditor.Selection.End; FlushTextEdit();
    }
    /// <summary>
    /// Ctrl+Z / Ctrl+Y (spec 13.2–13.3, D14g). While a text box is edited, only its own text goes back or forward: when
    /// the next history step touches anything else, nothing happens (the editing goes on). Otherwise the step runs and
    /// <see cref="RevealHistoryChange"/> shows where it happened.
    /// </summary>
    private void UndoTextAware(bool redo)
    {
        if (document is not { } doc) return;
        if (editingText?.Id is { } id)
        {
            FinishTextEditing(); // the typing so far becomes its own history step
            var (_, touched) = EditTargets.Of(redo ? doc.Session.RedoOperations : doc.Session.UndoOperations);
            if (touched.Count > 0 && touched.All(t => t == id)) { if (redo) doc.Session.Redo(); else doc.Session.Undo(); }
            if (doc.Session.Current.FindAnnotation(id)?.Annotation is TextBoxAnnotation) BeginTextEdit(id);
            return;
        }
        var operations = redo ? doc.Session.RedoOperations : doc.Session.UndoOperations;
        if (redo ? doc.Session.Redo() : doc.Session.Undo()) RevealHistoryChange(operations);
    }

    private bool holdRibbonTab;

    /// <summary>
    /// After an Undo or Redo (spec 13.3, D14g): when what the step left (restored or changed annotations of one page) is off
    /// screen, the view goes there (zoom kept) and selects it — with an automatic selection visit, so clicking away returns
    /// to the tool. On screen the tool stays as it is (the selection tool selects it). A page command (also one that moves
    /// annotations, like shrinking the content) only makes its first remaining page active. The ribbon tab stays (spec 3.3).
    /// </summary>
    private void RevealHistoryChange(IReadOnlyList<EditOperation> operations)
    {
        if (document is not { } doc) return;
        var model = doc.Session.Current;
        var (pages, annotations) = EditTargets.Of(operations);
        var left = annotations.Select(model.FindAnnotation).OfType<AnnotationLocation>().ToList();
        if (left.Count > 0 && !EditTargets.ChangesPages(operations)) // a page command (shrink, rotate…) only makes its page active
        {
            var page = left[0];
            var shown = left.Where(l => l.PageIndex == page.PageIndex).Select(l => l.Annotation).ToList();
            var b = AnnotationGeometry.Bounds(shown, PdfNotes.Text.TextLayout.Bounds);
            var box = new Quad(new(b.X, b.Y), new(b.X + b.Width, b.Y), new(b.X, b.Y + b.Height), new(b.X + b.Width, b.Y + b.Height));
            bool offScreen = !IsPartlyInView(page.PageIndex, [box]); // partly seen counts as seen: a page command touches the whole page
            if (offScreen) View.ScrollToPagePoint(page.PageIndex, new(b.X + b.Width / 2, b.Y + b.Height / 2));
            var selectable = shown.Where(a => !a.Locked).Select(a => a.Id).ToList();
            if (selectable.Count == 0 || NotesHidden || !offScreen && tools.Current != ToolKind.Select) return; // seen where it is: keep drawing
            var back = tools.Current;
            holdRibbonTab = true; // an Undo is not canvas work: e.g. undoing a 페이지 tab command keeps that tab (spec 3.3)
            try
            {
                if (back != ToolKind.Select) { SetTool(ToolKind.Select); tools.Return = back; }
                selectTool.SetSelection(selectable);
            }
            finally { holdRibbonTab = false; }
            return;
        }
        if (pages.FirstOrDefault(p => model.IndexOfPage(p) >= 0) is { } pageId && pageId != Guid.Empty)
        {
            int index = model.IndexOfPage(pageId);
            if (View.CurrentPageIndex != index) View.GoToPage(index);
            Activate(pageId, scroll: false);
        }
    }
}
