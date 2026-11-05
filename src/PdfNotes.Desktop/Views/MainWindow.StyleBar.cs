using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PdfNotes.Core.Models;
using PdfNotes.Desktop.Themes;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Tools;
using PdfNotes.ViewModels;
using DashStyle = PdfNotes.Core.Models.DashStyle;

namespace PdfNotes.Desktop.Views;

/// <summary>
/// The context settings row under the ribbon (spec 3.1–3.2, 7.3, 8, 9.1, D13c): the style bar [palette | width |
/// transparency | kind options], the eraser bar and (MainWindow.TextBar.cs) the text bar. What they show is
/// <see cref="ToolStylesViewModel.Bar"/>; this copies it into the controls and sends the changes back.
/// </summary>
public sealed partial class MainWindow
{
    private ComboBox shapeBorderBox = null!, dashBox = null!, lineShapeBox = null!;
    private RadioButton shapeFillButton = null!, shapeBorderButton = null!;
    private ToggleButton startArrowButton = null!, endArrowButton = null!;
    private bool syncingStyleBar;

    private static string[] DashNames => [Strings.Get("dash.solid"), Strings.Get("dash.dashed"), Strings.Get("dash.dotted"), Strings.Get("dash.dashDot")];
    private static string[] BorderNames => [Strings.Get("border.none"), .. DashNames];

    private void InitializeStyleBar()
    {
        styles.Changed += (_, _) => UpdateStyleBar();
        InitializeShapeOptions();
        InitializeLineOptions();
        foreach (var color in ColorPalette.Basic) PalettePanel.Children.Add(CreateSwatch(color, ColorName(color), styles.SetColor));
        PaletteGroup.Children.Add(CustomColorEntry("CustomColor", () => styles.StyleTarget?.Style.Color ?? styles.Pen.Color, styles.SetColor));

        foreach (var button in new[] { MarkHighlightButton, MarkUnderlineButton, MarkStrikeOutButton })
            button.Click += (_, _) =>
            {
                if (syncingStyleBar || button.Tag is not string tag) return;
                styles.SetMarkKind(tag switch { "Underline" => TextMarkupKind.Underline, "StrikeOut" => TextMarkupKind.StrikeOut, _ => null });
            };
        HighlightAutoButton.Click += (_, _) => styles.SetHighlightAuto(HighlightAutoButton.IsChecked == true);
        HighlighterSnapButton.Click += (_, _) => styles.SetHighlighterSnap(HighlighterSnapButton.IsChecked == true);

        // Sliders change while dragged (one Undo unit until committed: the release, a key up or the focus leaving).
        WidthSlider.ValueChanged += (_, e) => { if (!syncingStyleBar) styles.SetWidth(Math.Round(e.NewValue * 2) / 2, continuous: true); };
        TransparencySlider.ValueChanged += (_, e) => { if (!syncingStyleBar) styles.SetTransparency(Math.Round(e.NewValue), continuous: true); };
        foreach (var slider in new[] { WidthSlider, TransparencySlider })
        {
            slider.AddHandler(PointerReleasedEvent, (_, _) => styles.Commit(), RoutingStrategies.Tunnel, handledEventsToo: true);
            slider.KeyUp += (_, _) => styles.Commit();
            slider.LostFocus += (_, _) => styles.Commit();
        }
        Field(WidthBox, () => { if (TryParseNumber(WidthBox.Text, out double v) && WidthBox.Text != StyleText(t => t.WidthPt, styles.StyleMixed.Width)) styles.SetWidth(v); WidthBox.Text = StyleText(t => t.WidthPt, styles.StyleMixed.Width); },
            () => StyleText(t => t.WidthPt, styles.StyleMixed.Width));
        Field(TransparencyBox, () => { if (TryParseNumber(TransparencyBox.Text, out double v) && TransparencyBox.Text != StyleText(t => t.TransparencyPercent, styles.StyleMixed.Transparency)) styles.SetTransparency(v); TransparencyBox.Text = StyleText(t => t.TransparencyPercent, styles.StyleMixed.Transparency); },
            () => StyleText(t => t.TransparencyPercent, styles.StyleMixed.Transparency));

        // The eraser bar (spec 7.3–7.4)
        EraserSizeSlider.ValueChanged += (_, e) => { if (!syncingStyleBar && styles.SetEraserSize(Math.Round(e.NewValue))) UpdateEraserBar(); };
        EraserSizeSlider.AddHandler(PointerReleasedEvent, (_, _) => viewModel.SaveSettings(), RoutingStrategies.Tunnel, handledEventsToo: true);
        EraserSizeSlider.KeyUp += (_, _) => viewModel.SaveSettings();
        Field(EraserSizeBox, () => { if (TryParseNumber(EraserSizeBox.Text, out double v)) styles.SetEraserSize(v); UpdateEraserBar(); viewModel.SaveSettings(); },
            () => FormatNumber(styles.EraserSize));
        InitializeEyedropper();
    }

    private string StyleText(Func<StrokeStyle, double> value, bool mixed) => styles.StyleTarget is { } t && !mixed ? FormatNumber(value(t.Style)) : "";

    /// <summary>
    /// A bar field (the WPF StyleBoxKeyDown): Enter or Esc gives the keyboard back to the canvas, and leaving the field
    /// applies it once — Esc first puts the current value back, so nothing changes.
    /// </summary>
    private void Field(TextBox box, Action apply, Func<string> current)
    {
        box.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            if (e.Key == Key.Escape) box.Text = current();
            FocusBack();
            e.Handled = true;
        };
        box.LostFocus += (_, _) => { if (!syncingStyleBar) apply(); };
    }

    /// <summary>The keyboard goes back to the text being edited, else the canvas.</summary>
    private void FocusBack()
    {
        if (textEditor is { } editor) editor.Focus();
        else Canvas.Focus();
    }

    private void UpdateStyleBar()
    {
        if (StylePanel is null) return;
        var bar = styles.Bar;
        UpdateTextStyleBar();
        EraserBorder.IsVisible = bar.Eraser;
        StyleBar.IsVisible = !bar.Eraser && !TextStyleBorder.IsVisible;
        StyleBar.IsEnabled = bar.Target is not null;
        StyleBar.Opacity = bar.Target is null ? 0.55 : 1;
        bool was = syncingStyleBar;
        syncingStyleBar = true;
        try
        {
            UpdateShapeOptions(bar.ShapeOptionsShown);
            MarkKindPanel.IsVisible = bar.MarkKindShown;
            HighlighterSnapButton.IsVisible = bar.Highlighter;
            HighlighterSnapButton.IsChecked = settings.HighlighterSnap;
            HighlighterSnapButton.IsEnabled = bar.SnapEnabled; // underline and strikeout always go on text (spec 9.1)
            MarkHighlightButton.IsChecked = styles.MarkKind is null;
            MarkUnderlineButton.IsChecked = styles.MarkKind == TextMarkupKind.Underline;
            MarkStrikeOutButton.IsChecked = styles.MarkKind == TextMarkupKind.StrikeOut;
            TransparencySlider.IsEnabled = TransparencyBox.IsEnabled = bar.TransparencyEnabled;
            HighlightAutoButton.IsVisible = bar.HighlightAutoShown;
            HighlightAutoButton.IsChecked = bar.HighlightAutoChecked;
            WidthSlider.IsEnabled = WidthBox.IsEnabled = bar.WidthEnabled;
            if (bar.Eraser) UpdateEraserBar();
            var (style, kind) = bar.Shown(styles.Pen);
            var (min, max) = StrokeStyle.WidthRange(kind);
            WidthSlider.Minimum = min;
            WidthSlider.Maximum = max;
            WidthSlider.Value = Math.Clamp(style.WidthPt, min, max);
            if (!WidthBox.IsFocused) WidthBox.Text = bar.Target is null || bar.Mixed.Width ? "" : FormatNumber(style.WidthPt);
            TransparencySlider.Value = style.TransparencyPercent;
            if (!TransparencyBox.IsFocused) TransparencyBox.Text = bar.Target is null || bar.Mixed.Transparency ? "" : FormatNumber(style.TransparencyPercent);
            UpdateLineOptions(bar.LineOptionsShown);
            bool showColor = bar.Target is not null && !bar.Mixed.Color;
            foreach (var swatch in PalettePanel.Children.OfType<Button>()) MarkSwatch(swatch, showColor && Equals(swatch.Tag, style.Color));
            RecentPanel.Children.Clear();
            foreach (var color in styles.UserColors)
            {
                var swatch = CreateSwatch(color, color.ToHex(), styles.SetColor, removable: true);
                MarkSwatch(swatch, showColor && color == style.Color);
                RecentPanel.Children.Add(swatch);
            }
        }
        finally { syncingStyleBar = was; }
    }

    private void UpdateEraserBar()
    {
        bool was = syncingStyleBar;
        syncingStyleBar = true;
        try
        {
            EraserSizeSlider.Value = styles.EraserSize;
            if (!EraserSizeBox.IsFocused) EraserSizeBox.Text = FormatNumber(styles.EraserSize);
        }
        finally { syncingStyleBar = was; }
    }

    // ----- shapes (spec 8 preface, D11) -----

    private void InitializeShapeOptions()
    {
        var theme = (Avalonia.Styling.ControlTheme)this.FindResource("ToolButton")!;
        shapeFillButton = new RadioButton { Name = "ShapeFillButton", Content = Strings.Get("shape.fill"), GroupName = "ShapeTarget", Theme = theme, IsChecked = true };
        shapeBorderButton = new RadioButton { Name = "ShapeBorderButton", Content = Strings.Get("shape.border"), GroupName = "ShapeTarget", Theme = theme };
        shapeFillButton.Click += (_, _) => { if (!syncingStyleBar) styles.SetShapeColorTarget(true); };
        shapeBorderButton.Click += (_, _) => { if (!syncingStyleBar) styles.SetShapeColorTarget(false); };
        shapeBorderBox = new ComboBox { Name = "ShapeBorderBox", Width = 92, Margin = new Thickness(8, 0, 0, 0), ItemsSource = BorderNames, Classes = { "bar" } };
        ToolTip.SetTip(shapeBorderBox, Strings.Get("shape.borderStyle"));
        shapeBorderBox.SelectionChanged += (_, _) =>
        {
            if (!syncingStyleBar && shapeBorderBox.SelectedIndex >= 0) styles.SetShapeBorder(shapeBorderBox.SelectedIndex == 0 ? null : (DashStyle)(shapeBorderBox.SelectedIndex - 1));
        };
        ShapeOptionsPanel.Children.Add(shapeFillButton);
        ShapeOptionsPanel.Children.Add(shapeBorderButton);
        ShapeOptionsPanel.Children.Add(shapeBorderBox);
        ShapeOptionsPanel.Children.Add(new Border { Classes = { "sep" } }); // the palette follows (target first, like the text bar)
    }

    private void UpdateShapeOptions(bool visible)
    {
        ShapeOptionsPanel.IsVisible = visible;
        if (!visible) return;
        var state = styles.ShapeBar;
        shapeFillButton.IsChecked = state.FillTarget;
        shapeBorderButton.IsChecked = !state.FillTarget;
        shapeBorderBox.SelectedIndex = state.BorderIndex;
    }

    // ----- lines (spec 8.1–8.2) -----

    private void InitializeLineOptions()
    {
        LineOptionsPanel.Children.Add(new Border { Classes = { "sep" } });
        dashBox = new ComboBox { Name = "DashBox", Width = 82, ItemsSource = DashNames, Classes = { "bar" } };
        ToolTip.SetTip(dashBox, Strings.Get("line.dashStyle"));
        dashBox.SelectionChanged += (_, _) => { if (!syncingStyleBar && dashBox.SelectedIndex >= 0) styles.SetLineDash((DashStyle)dashBox.SelectedIndex); };
        LineOptionsPanel.Children.Add(dashBox);
        startArrowButton = ArrowToggle("ArrowLeft", Strings.Get("line.startArrow"), on => styles.SetLineArrows(start: on));
        endArrowButton = ArrowToggle("ArrowRight", Strings.Get("line.endArrow"), on => styles.SetLineArrows(end: on));
        lineShapeBox = new ComboBox { Name = "LineShapeBox", Width = 104, Margin = new Thickness(8, 0, 0, 0), Classes = { "bar" } };
        ToolTip.SetTip(lineShapeBox, Strings.Get("line.controls"));
        lineShapeBox.SelectionChanged += (_, _) => { if (!syncingStyleBar && lineShapeBox.SelectedIndex >= 0) styles.SetLineShape(lineShapeBox.SelectedIndex); };
        LineOptionsPanel.Children.Add(lineShapeBox);
    }

    private ToggleButton ArrowToggle(string glyph, string tip, Action<bool> apply)
    {
        var button = new ToggleButton { Content = new AppIcon { Glyph = glyph }, Width = 30, Margin = new Thickness(4, 0, 0, 0), Theme = (Avalonia.Styling.ControlTheme)this.FindResource("ToolButton")! };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => apply(button.IsChecked == true);
        LineOptionsPanel.Children.Add(button);
        return button;
    }

    private void UpdateLineOptions(bool visible)
    {
        LineOptionsPanel.IsVisible = visible;
        if (!visible) return;
        var state = styles.LineBar;
        dashBox.SelectedIndex = state.DashIndex;
        startArrowButton.IsChecked = state.StartArrow;
        endArrowButton.IsChecked = state.EndArrow;
        string[] shapes = state.Shapes switch
        {
            LineShapeChoice.SelectedLine => [Strings.Get("lineShape.straight"), Strings.Get("lineShape.oneControl"), Strings.Get("lineShape.twoControls")],
            LineShapeChoice.CurveTool => [Strings.Get("common.auto"), Strings.Get("lineShape.oneControl"), Strings.Get("lineShape.twoControls")],
            _ => []
        };
        if (lineShapeBox.ItemsSource is not string[] current || !current.SequenceEqual(shapes)) lineShapeBox.ItemsSource = shapes;
        lineShapeBox.IsVisible = shapes.Length > 0;
        lineShapeBox.SelectedIndex = state.ShapeIndex;
    }

    // ----- palette swatches and the + color window -----

    private static void MarkSwatch(Button swatch, bool current) => swatch.Classes.Set("current", current);

    /// <summary>A palette swatch; a user color (<paramref name="removable"/>) has the context menu 제거 (spec 3.2, 2026-10-13).</summary>
    private Button CreateSwatch(RgbColor color, string name, Action<RgbColor> pick, bool removable = false)
    {
        var swatch = new Button { Theme = (Avalonia.Styling.ControlTheme)this.FindResource("Swatch")!, Tag = color, Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)) };
        ToolTip.SetTip(swatch, name);
        swatch.Click += (_, _) => pick(color);
        if (removable) swatch.ContextMenu = new ContextMenu { Items = { Menus.Item(Strings.Get("color.remove"), () => styles.RemoveUserColor(color)) } };
        return swatch;
    }

    /// <summary>The color pickers of the palettes by name (tests).</summary>
    public Dictionary<string, ColorPickerPanel> CustomColorPickers { get; } = [];

    /// <summary>
    /// The + of a palette (spec 3.2): a popup with the color plane, hue strip, the code, the eyedropper and 추가. 추가 or
    /// Enter applies the color (it becomes a user color), Esc closes.
    /// </summary>
    private Control CustomColorEntry(string name, Func<RgbColor> current, Action<RgbColor> apply)
    {
        var button = new ToggleButton { Name = name + "Button", Theme = (Avalonia.Styling.ControlTheme)this.FindResource("ToolButton")!, Padding = new Thickness(6, 3), Content = new AppIcon { Glyph = "Add", Size = 14 } };
        ToolTip.SetTip(button, Strings.Get("color.custom"));
        var picker = new ColorPickerPanel(name);
        var frame = new Border { BorderThickness = new Thickness(1), Child = picker, Padding = new Thickness(10), CornerRadius = new CornerRadius(6) };
        frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("Surface.Background"));
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Border"));
        var popup = new Popup { Name = name + "Popup", PlacementTarget = button, Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true, Child = frame };
        button.Click += (_, _) => popup.IsOpen = button.IsChecked == true;
        popup.Closed += (_, _) => button.IsChecked = false;
        popup.Opened += (_, _) => { picker.Add.IsEnabled = styles.UserColors.Count < ColorPalette.MaxUser; picker.ShowColor(current()); picker.Code.Focus(); picker.Code.SelectAll(); };
        picker.Picked += color => { popup.IsOpen = false; apply(color); FocusBack(); };
        picker.EyedropperRequested += () => { popup.IsOpen = false; StartEyedropper(apply); };
        picker.KeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; FocusBack(); e.Handled = true; } };
        CustomColorPickers[name] = picker;
        var host = new Panel { Children = { button, popup } };
        return host;
    }

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

    private static string ColorName(RgbColor color)
    {
        int index = ((IList<RgbColor>)ColorPalette.Basic).IndexOf(color);
        string[] names = [Strings.Get("color.black"), Strings.Get("color.gray"), Strings.Get("color.red"), Strings.Get("color.orange"), Strings.Get("color.yellow"), Strings.Get("color.green"), Strings.Get("color.blue"), Strings.Get("color.navy"), Strings.Get("color.purple"), Strings.Get("color.white")];
        return index >= 0 && index < names.Length ? names[index] : color.ToHex();
    }

    private static string FormatNumber(double value) => Math.Round(value, 1).ToString("0.#", CultureInfo.CurrentCulture);

    private static bool TryParseNumber(string? text, out double value) =>
        double.TryParse((text ?? "").Trim().TrimEnd('%').Replace("pt", "", StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);
}
