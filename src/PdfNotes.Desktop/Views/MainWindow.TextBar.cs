using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Desktop.Themes;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Tools;
using PdfNotes.Text;
using PdfNotes.ViewModels;
using Alignment = PdfNotes.Core.Models.TextAlignment;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// The text bar (spec 3.2, 6, 9.4; D13c layout): [target 글자/채우기/테두리 | palette, user colors, + | transparency | font
/// and size | B I U S highlight | alignment, saved styles]. The state is <see cref="TextStyleViewModel"/>'s; the box being
/// edited is the P2 editor model (<see cref="EditorSession"/>). Also the text-aware Undo of the window (spec 13.2–13.3).
/// </summary>
public sealed partial class MainWindow
{
    private ComboBox fontBox = null!, alignmentBox = null!;
    private TextBox textSizeBox = null!, textAlphaBox = null!;
    private StackPanel textPalettePanel = null!, textRecentPanel = null!;
    private readonly RadioButton[] textTargetButtons = new RadioButton[3];
    private readonly Dictionary<char, ToggleButton> textToggles = [];
    private ToggleButton textHighlightButton = null!;
    private bool syncingTextBar;

    private TextStyleViewModel textStyle => viewModel.TextStyle;

    /// <summary>The box being edited, as the text bar sees it: the P2 editor model has the same members (P1 V8).</summary>
    private sealed class EditorSession(MainWindow window) : ITextEditorSession
    {
        private TextEditor.TextEditorModel? Model => window.editSession?.Model;
        public bool IsEditing => Model is not null;
        public TextBoxAnnotation Box => Model!.Box;
        public (int Start, int Length) Range => Model!.Range;
        public TextRun TypingStyle => Model!.TypingStyle;
        public void CompleteInput() => Model?.CompleteInput();
        public void Format(Func<TextRun, TextRun> change) => Model?.Format(change);
        public void Change(Func<TextBoxAnnotation, TextBoxAnnotation> change) => Model?.Change(change);
    }

    private ControlTheme ToolTheme => (ControlTheme)this.FindResource("ToolButton")!;

    private void InitializeTextBar()
    {
        textStyle.Attach(new EditorSession(this), selectTool);
        textStyle.Changed += (_, _) => UpdateTextStyleBar();

        var targets = Group();
        foreach (var (name, label, target) in new[] { ("TextTargetText", Strings.Get("text.targetText"), TextColorTarget.Text), ("TextTargetFill", Strings.Get("text.targetFill"), TextColorTarget.Fill), ("TextTargetBorder", Strings.Get("text.targetBorder"), TextColorTarget.Border) })
        {
            var button = new RadioButton { Name = name, Content = label, GroupName = "TextColorTarget", Theme = ToolTheme, IsChecked = target == textStyle.ColorTarget };
            button.Click += (_, _) => { if (!syncingTextBar) textStyle.SetColorTarget(target); FocusBack(); };
            textTargetButtons[(int)target] = button;
            targets.Children.Add(button);
        }
        textPalettePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var color in ColorPalette.Basic) textPalettePanel.Children.Add(CreateSwatch(color, ColorName(color), SetTextColor));
        textRecentPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Group(textPalettePanel, Sep(), textRecentPanel, CustomColorEntry("TextCustomColor", () => textStyle.TargetColor ?? RgbColor.Black, SetTextColor));
        TextStylePanel.Children.Add(Sep());
        textAlphaBox = BarField("TextAlphaBox", 40, () => { if (TryParseNumber(textAlphaBox.Text, out double percent)) textStyle.SetTransparency(percent); else UpdateTextStyleBar(); }, Strings.Get("style.transparency"));
        Group(new TextBlock { Text = Strings.Get("style.transparency"), Classes = { "label" } }, textAlphaBox, new TextBlock { Text = "%", Classes = { "unit" } });
        TextStylePanel.Children.Add(Sep());
        fontBox = new ComboBox { Name = "TextFontBox", Width = 124, IsEditable = true, ItemsSource = TextLayout.FontFamilies().ToArray(), Margin = new Thickness(0, 0, 6, 0), Classes = { "bar" } };
        fontBox.DropDownClosed += (_, _) => ApplyFont();
        fontBox.LostFocus += (_, _) => ApplyFont();
        fontBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FocusBack(); e.Handled = true; } };
        textSizeBox = BarField("TextSizeBox", 42, () => textStyle.SetSize(textSizeBox.Text ?? ""), Strings.Get("text.size"));
        Group(fontBox, textSizeBox, new TextBlock { Text = "pt", Classes = { "unit" } });
        TextStylePanel.Children.Add(Sep());
        var toggles = Group();
        foreach (var (flag, tip, weight, style) in new[] { ('B', Strings.Get("text.bold"), FontWeight.Bold, FontStyle.Normal), ('I', Strings.Get("text.italic"), FontWeight.Normal, FontStyle.Italic), ('U', Strings.Get("text.underline"), FontWeight.Normal, FontStyle.Normal), ('S', Strings.Get("text.strikeOut"), FontWeight.Normal, FontStyle.Normal) })
        {
            var text = new TextBlock { Text = flag.ToString(), FontWeight = weight, FontStyle = style, TextDecorations = flag == 'U' ? TextDecorations.Underline : flag == 'S' ? TextDecorations.Strikethrough : null };
            var button = new ToggleButton { Name = $"Text{flag}Button", Content = text, Width = 27, Theme = ToolTheme };
            ToolTip.SetTip(button, tip);
            char f = flag;
            button.Click += (_, _) => { textStyle.SetFlag(f, button.IsChecked == true); FocusBack(); };
            textToggles[flag] = button;
            toggles.Children.Add(button);
        }
        textHighlightButton = new ToggleButton { Name = "TextHighlightButton", Theme = ToolTheme, Content = new AppIcon { Glyph = "Highlighter" } };
        ToolTip.SetTip(textHighlightButton, Strings.Get("text.highlight"));
        textHighlightButton.Click += (_, _) => { textStyle.ToggleHighlight(); FocusBack(); };
        toggles.Children.Add(textHighlightButton);
        TextStylePanel.Children.Add(Sep());
        // Alignment as icons (the words did not fit at the 1200 DIP one-row width, D13e).
        alignmentBox = new ComboBox { Name = "TextAlignmentBox", Width = 54, Margin = new Thickness(0, 0, 4, 0), Classes = { "bar" },
            ItemsSource = new[] { "AlignLeft", "AlignCenter", "AlignRight" },
            // Names, not icon controls: a control has one parent, so the open list and the box could not both show it.
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((glyph, _) => new AppIcon { Glyph = glyph }) };
        ToolTip.SetTip(alignmentBox, Strings.Get("text.alignment"));
        alignmentBox.SelectionChanged += (_, _) => { if (!syncingTextBar && alignmentBox.SelectedIndex >= 0) { textStyle.SetAlignment((Alignment)alignmentBox.SelectedIndex); FocusBack(); } };
        var slots = new Button { Name = "TextStylesButton", Content = Strings.Get("text.stylesButton"), Theme = ToolTheme };
        ToolTip.SetTip(slots, Strings.Get("text.savedStyles"));
        slots.Click += (_, _) =>
        {
            var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
            for (int i = 0; i < 5; i++) { int slot = i; menu.Items.Add(Menus.Item(SlotHeader(i), () => { textStyle.ApplySlot(slot); FocusBack(); }, enabled: settings.TextSlots[i] is not null)); }
            menu.Open(slots);
        };
        Group(alignmentBox, slots);
    }

    private static Border Sep() => new() { Classes = { "sep" } };

    private StackPanel Group(params Control[] items)
    {
        var group = new StackPanel { Classes = { "field" }, Margin = new Thickness(0, 0, 6, 0) };
        foreach (var item in items) group.Children.Add(item);
        TextStylePanel.Children.Add(group);
        return group;
    }

    private TextBox BarField(string name, double width, Action apply, string tip)
    {
        var box = new TextBox { Name = name, Width = width, Classes = { "bar" } };
        ToolTip.SetTip(box, tip);
        box.LostFocus += (_, _) => { if (!syncingTextBar) apply(); };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FocusBack(); e.Handled = true; } }; // leaving the field applies it once
        return box;
    }

    private void ApplyFont()
    {
        if (!syncingTextBar && fontBox.Text is { Length: > 0 } family && family != textStyle.CurrentStyle.FontFamily) textStyle.SetFont(family);
    }

    private void SetTextColor(RgbColor color) { textStyle.SetColor(color); FocusBack(); }

    /// <summary>`Slot 1` in the slot's own font, size (11–20 DIP), weight and color, as in the WPF menu.</summary>
    private object SlotHeader(int index)
    {
        string name = $"Slot {index + 1}"; // slot names are fixed, as in the spec (not UI text, D14e)
        if (settings.TextSlots[index] is not { } run) return name;
        var block = new TextBlock
        {
            Text = name, FontFamily = new FontFamily(run.FontFamily), FontSize = Math.Clamp(run.SizePt * 96 / 72, 11, 20),
            FontWeight = run.Bold ? FontWeight.Bold : FontWeight.Normal, FontStyle = run.Italic ? FontStyle.Italic : FontStyle.Normal,
            TextDecorations = run.Underline && run.Strikethrough ? [.. TextDecorations.Underline, .. TextDecorations.Strikethrough]
                : run.Underline ? TextDecorations.Underline : run.Strikethrough ? TextDecorations.Strikethrough : null
        };
        if (0.2126 * run.Color.R + 0.7152 * run.Color.G + 0.0722 * run.Color.B < 230) block.Foreground = new SolidColorBrush(Color.FromRgb(run.Color.R, run.Color.G, run.Color.B));
        return block;
    }

    private MenuItem SavedTextStylesMenu(bool enabled = true) =>
        Menus.Sub(Strings.Get("text.savedStyles"), enabled, Enumerable.Range(0, settings.TextSlots.Length).Select(i => (Control)Menus.Item(SlotHeader(i), () => textStyle.ApplySlot(i), enabled: settings.TextSlots[i] is not null)));

    /// <summary>The right-click menu of the box being edited: saved styles, save the style, make it the default, the clipboard commands.</summary>
    public ContextMenu TextContextMenu()
    {
        editSession?.Model.CompleteInput();
        var menu = new ContextMenu();
        menu.Items.Add(SavedTextStylesMenu());
        menu.Items.Add(Menus.Sub(Strings.Get("text.saveStyle"), true, Enumerable.Range(0, 5).Select(i => (Control)Menus.Item(SlotHeader(i), () => textStyle.SaveStyle(i)))));
        menu.Items.Add(Menus.Item(Strings.Get("text.makeDefault"), () => textStyle.SaveStyle(null)));
        // The clipboard commands of the editor (the WPF menu's Copy, Cut, Paste, Select all).
        menu.Items.Add(new Separator());
        if (textEditor is { } editor)
        {
            menu.Items.Add(Menus.Item(Strings.Get("command.copy"), () => _ = editor.CopyAsync()));
            menu.Items.Add(Menus.Item(Strings.Get("command.cut"), () => _ = editor.CutAsync()));
            menu.Items.Add(Menus.Item(Strings.Get("command.paste"), () => _ = editor.PasteAsync()));
            menu.Items.Add(Menus.Item(Strings.Get("command.selectAll"), () => { editor.Model?.SelectAll(); editor.InvalidateVisual(); }));
        }
        return menu;
    }

    private void UpdateTextStyleBar()
    {
        if (fontBox is null) return;
        var bar = textStyle.Bar;
        TextStyleBorder.IsVisible = bar.Visible;
        TextStyleBorder.IsEnabled = bar.Enabled;
        if (StyleBar is not null) StyleBar.IsVisible = !bar.Visible && !EraserBorder.IsVisible;
        if (!bar.Visible) { textRecentPanel.Children.Clear(); return; }
        syncingTextBar = true;
        try
        {
            var r = bar.Style;
            if (!fontBox.IsKeyboardFocusWithin) fontBox.Text = r.FontFamily;
            if (!textSizeBox.IsFocused) textSizeBox.Text = bar.Size;
            textHighlightButton.IsChecked = bar.Highlighted;
            textToggles['B'].IsChecked = r.Bold; textToggles['I'].IsChecked = r.Italic; textToggles['U'].IsChecked = r.Underline; textToggles['S'].IsChecked = r.Strikethrough;
            alignmentBox.SelectedIndex = (int)bar.Alignment;
            for (int i = 0; i < textTargetButtons.Length; i++) textTargetButtons[i].IsChecked = i == (int)bar.Target;
            var color = bar.Color;
            foreach (var swatch in textPalettePanel.Children.OfType<Button>()) MarkSwatch(swatch, Equals(swatch.Tag, color));
            textRecentPanel.Children.Clear();
            foreach (var recent in styles.UserColors)
            {
                var swatch = CreateSwatch(recent, recent.ToHex(), SetTextColor, removable: true);
                MarkSwatch(swatch, recent == color);
                textRecentPanel.Children.Add(swatch);
            }
            textAlphaBox.IsEnabled = bar.TransparencyEnabled;
            if (!textAlphaBox.IsFocused) textAlphaBox.Text = bar.Transparency;
        }
        finally { syncingTextBar = false; }
    }

    // ----- text-aware Undo (spec 13.2–13.3, D14g) -----

    /// <summary>
    /// While a text box is edited, Undo and Redo are the editor's own word steps, whatever has the keyboard (the quick
    /// buttons too); with none left nothing happens, never a document step (spec 13.2). Otherwise the document step runs
    /// and <see cref="RevealHistoryChange"/> shows where.
    /// </summary>
    private void UndoTextAware(bool redo)
    {
        if (Document is not { } doc) return;
        Canvas.CancelTool();
        if (editSession is { } session)
        {
            if (redo) session.Model.Redo(); else session.Model.Undo();
            return;
        }
        CloseNote();
        var operations = redo ? doc.Session.RedoOperations : doc.Session.UndoOperations;
        if (redo ? doc.Session.Redo() : doc.Session.Undo()) RevealHistoryChange(operations);
    }

    /// <summary>
    /// After an Undo or Redo (spec 13.3, D14g): what the step left off screen is scrolled to and selected (an automatic
    /// selection visit); on screen the tool stays. A page command only makes its first remaining page active. The tab stays.
    /// </summary>
    private void RevealHistoryChange(IReadOnlyList<EditOperation> operations)
    {
        if (Document is not { } doc) return;
        var model = doc.Session.Current;
        var (pageIds, annotations) = EditTargets.Of(operations);
        var left = annotations.Select(model.FindAnnotation).OfType<AnnotationLocation>().ToList();
        if (left.Count > 0 && !EditTargets.ChangesPages(operations))
        {
            var page = left[0];
            var shown = left.Where(l => l.PageIndex == page.PageIndex).Select(l => l.Annotation).ToList();
            var b = AnnotationGeometry.Bounds(shown, TextLayout.Bounds);
            var box = new Quad(new(b.X, b.Y), new(b.X + b.Width, b.Y), new(b.X, b.Y + b.Height), new(b.X + b.Width, b.Y + b.Height));
            bool offScreen = !Canvas.IsPartlyInView(page.PageIndex, [box]);
            if (offScreen) Canvas.ScrollToPagePoint(page.PageIndex, new(b.X + b.Width / 2, b.Y + b.Height / 2));
            var selectable = shown.Where(a => !a.Locked).Select(a => a.Id).ToList();
            if (selectable.Count == 0 || NotesHidden || !offScreen && tools.Current != ToolKind.Select) return;
            var back = tools.Current;
            viewModel.Ribbon.Hold = true; // an Undo is not canvas work (spec 3.3)
            try
            {
                if (back != ToolKind.Select) { SetTool(ToolKind.Select); tools.Return = back; }
                selectTool.SetSelection(selectable);
            }
            finally { viewModel.Ribbon.Hold = false; }
            return;
        }
        if (pageIds.FirstOrDefault(p => model.IndexOfPage(p) >= 0) is { } pageId && pageId != Guid.Empty)
        {
            int index = model.IndexOfPage(pageId);
            if (Canvas.CurrentPageIndex != index) Canvas.GoToPage(index);
            viewModel.Pages.Activate(pageId, scroll: false);
        }
    }
}
