using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.ViewModels;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// The context settings row under the ribbon (spec 3.1–3.2, 7.3, 8, 9.1, D13c): the style bar, the eraser bar and the
/// text bar. Their controls are bound to <see cref="ToolStylesViewModel"/> and <see cref="TextStyleViewModel"/> (R4d);
/// this file has only what is the window's: where the keyboard goes after a box, when a slider drag ends, and the
/// eyedropper.
/// </summary>
public sealed partial class MainWindow
{
    private void InitializeStyleBar()
    {
        // Sliders change while dragged (one Undo unit until committed: the release, a key up or the focus leaving).
        foreach (var slider in new[] { WidthSlider, TransparencySlider })
        {
            slider.AddHandler(PointerReleasedEvent, (_, _) => styles.Commit(), RoutingStrategies.Tunnel, handledEventsToo: true);
            slider.KeyUp += (_, _) => styles.Commit();
            slider.LostFocus += (_, _) => styles.Commit();
        }
        Field(WidthBox, styles.CommitWidth, styles.WidthField);
        Field(TransparencyBox, styles.CommitTransparency, styles.TransparencyField);
        // The eraser size is an app setting: stored when a drag or a key ends.
        EraserSizeSlider.AddHandler(PointerReleasedEvent, (_, _) => viewModel.SaveSettings(), RoutingStrategies.Tunnel, handledEventsToo: true);
        EraserSizeSlider.KeyUp += (_, _) => viewModel.SaveSettings();
        Field(EraserSizeBox, styles.CommitEraserSize, styles.EraserField);
        foreach (var entry in new[] { CustomColor, TextCustomColor })
        {
            CustomColorPickers[entry.Name!] = entry.Picker;
            entry.EyedropperRequested += () => StartEyedropper(entry.Pick);
            entry.Done += FocusBack;
        }
        InitializeEyedropper();
    }

    /// <summary>
    /// A bar field (the WPF StyleBoxKeyDown): Enter or Esc gives the keyboard back to the canvas, and leaving the field
    /// applies it once — Esc first puts the current value back, so nothing changes.
    /// </summary>
    private void Field(TextBox box, Action apply, InputField field)
    {
        box.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            if (e.Key == Key.Escape) field.Reset();
            FocusBack();
            e.Handled = true;
        };
        box.LostFocus += (_, _) => apply();
    }

    /// <summary>The keyboard goes back to the text being edited, else the canvas.</summary>
    private void FocusBack()
    {
        if (textEditor is { } editor) editor.Focus();
        else Canvas.Focus();
    }

    /// <summary>The color pickers of the palettes by name (tests).</summary>
    public Dictionary<string, ColorPickerPanel> CustomColorPickers { get; } = [];

    // ----- eyedropper (spec 3.2): one click anywhere in the window picks that pixel's color -----

    private Action<RgbColor>? eyedropper;

    public bool IsEyedropperActive => eyedropper is not null;

    public void StartEyedropper(Action<RgbColor> apply)
    {
        eyedropper = apply;
        Cursor = new Cursor(StandardCursorType.Cross);
        Canvas.CursorOverride = new Cursor(StandardCursorType.Cross); // the pages too (the WPF Mouse.OverrideCursor)
        viewModel.Status = Strings.Get("color.eyedropperTip");
    }

    private void EndEyedropper()
    {
        if (eyedropper is null) return;
        eyedropper = null;
        Cursor = Cursor.Default;
        Canvas.CursorOverride = null;
        viewModel.Status = "";
    }

    private bool suppressEyedropperUp;

    private void InitializeEyedropper()
    {
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (eyedropper is not { } apply) return;
            e.Handled = true;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { EndEyedropper(); return; }
            suppressEyedropperUp = true; // its button-up must not reach a tool either
            var color = SampleColor(e.GetPosition(this));
            EndEyedropper();
            apply(color);
            FocusBack();
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, e) => { if (suppressEyedropperUp) { suppressEyedropperUp = false; e.Handled = true; } }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) => { if (eyedropper is not null && e.Key == Key.Escape) { EndEyedropper(); e.Handled = true; } }, RoutingStrategies.Tunnel);
    }

    /// <summary>The color of the window's pixel at a point (DIPs), as drawn now — before the reading dark page inverts it (spec 24.12).</summary>
    public RgbColor SampleColor(Point windowPoint)
    {
        double scale = RenderScaling;
        var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale)));
        bool dark = Canvas.DarkPages;
        Canvas.DarkPages = false;
        try
        {
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(this);
            int x = Math.Clamp((int)(windowPoint.X * scale), 0, size.Width - 1), y = Math.Clamp((int)(windowPoint.Y * scale), 0, size.Height - 1);
            var pixel = new byte[4];
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
            try
            {
                bitmap.CopyPixels(new PixelRect(x, y, 1, 1), buffer, 4, 4);
                System.Runtime.InteropServices.Marshal.Copy(buffer, pixel, 0, 4);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
            return new(pixel[2], pixel[1], pixel[0]); // BGRA, the window is opaque
        }
        finally { Canvas.DarkPages = dark; }
    }
}
