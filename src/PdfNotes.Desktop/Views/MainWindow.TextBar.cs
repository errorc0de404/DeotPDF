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
/// The text bar (spec 3.2, 6, 9.4; D13c layout): its controls are bound to <see cref="TextStyleViewModel"/> (R4d); the box
/// being edited is the P2 editor model (<see cref="EditorSession"/>). Here: the keyboard after a field, the saved styles
/// menus.
/// </summary>
public sealed partial class MainWindow
{
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

        public void UndoStep(bool redo) => Model?.UndoStep(redo);
    }

    private void InitializeTextBar()
    {
        textStyle.Attach(new EditorSession(this), selectTool);
        // A button of the bar gives the keyboard back to the box being edited (the bar is bound, R4d).
        TextStylePanel.AddHandler(Button.ClickEvent, (_, _) => FocusBack(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        TextField(TextAlphaBox, textStyle.CommitTransparency);
        TextField(TextSizeBox, textStyle.CommitSize);
        TextFontBox.DropDownClosed += (_, _) => textStyle.CommitFont();
        TextFontBox.LostFocus += (_, _) => textStyle.CommitFont();
        TextFontBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FocusBack(); e.Handled = true; } };
        TextAlignmentBox.DropDownClosed += (_, _) => FocusBack();
    }

    /// <summary>Leaving the field applies it once; Enter gives the keyboard back.</summary>
    private void TextField(TextBox box, Action apply)
    {
        box.LostFocus += (_, _) => apply();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FocusBack(); e.Handled = true; } };
    }

    /// <summary>The saved styles menu of the bar (Slot 1–5, each in its own format).</summary>
    private void TextStylesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
        for (int i = 0; i < 5; i++) { int slot = i; menu.Items.Add(Menus.Item(SlotHeader(i), () => { textStyle.ApplySlot(slot); FocusBack(); }, enabled: settings.TextSlots[i] is not null)); }
        menu.Open(TextStylesButton);
    }

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


}
