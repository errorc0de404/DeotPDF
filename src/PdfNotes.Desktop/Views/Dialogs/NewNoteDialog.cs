using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;

namespace PdfNotes.Desktop.Views;

/// <summary>New note (spec 4.1) and a blank page (11.5): size preset or custom mm, orientation, background. Result in <see cref="Result"/>.</summary>
public sealed class NewNoteDialog : AppDialog
{
    private readonly (double WidthMm, double HeightMm)? reference;
    private readonly ComboBox sizeBox = new() { Name = "SizeBox", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 10) };
    private readonly RadioButton portrait = new() { Name = "PortraitButton", Content = Strings.Get("newNote.portrait"), IsChecked = true, GroupName = "Orientation", Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton landscape = new() { Name = "LandscapeButton", Content = Strings.Get("newNote.landscape"), GroupName = "Orientation" };
    private readonly TextBox widthBox = new() { Name = "WidthBox", Width = 72, TextAlignment = Avalonia.Media.TextAlignment.Right };
    private readonly TextBox heightBox = new() { Name = "HeightBox", Width = 72, TextAlignment = Avalonia.Media.TextAlignment.Right };
    private readonly RadioButton blank = new() { Name = "BlankButton", Content = Strings.Get("newNote.blank"), IsChecked = true, GroupName = "Background", Margin = new Thickness(0, 0, 0, 4) };
    private readonly RadioButton grid = new() { Name = "GridButton", Content = Strings.Get("newNote.grid"), GroupName = "Background", Margin = new Thickness(0, 0, 0, 4) };
    private readonly RadioButton lined = new() { Name = "LinedButton", Content = Strings.Get("newNote.lined"), GroupName = "Background" };
    private readonly CheckBox sameSize = new() { Name = "SameSizeBox", Content = Strings.Get("newNote.sameSize"), Margin = new Thickness(0, 0, 0, 12), IsVisible = false };
    private static readonly (string Label, PaperSize Size)[] Sizes =
        [("A4 (210 × 297mm)", PaperSize.A4), ("A5 (148 × 210mm)", PaperSize.A5), ("Letter (215.9 × 279.4mm)", PaperSize.Letter), (Strings.Get("transform.custom"), PaperSize.Custom)]; // paper names, not UI text (D14e)

    public NewNoteDialog() : base(Strings.Get("file.newNoteName"))
    {
        sizeBox.ItemsSource = Sizes.Select(s => s.Label).ToArray();
        sizeBox.SelectedIndex = 0;
        sizeBox.SelectionChanged += (_, _) => ShowPreset();
        portrait.IsCheckedChanged += (_, _) => OrientationChanged();
        sameSize.IsCheckedChanged += (_, _) => SameSizeChanged();
        string Number(double v) => v.ToString("0", CultureInfo.CurrentCulture);
        var range = Muted(Strings.Format("newNote.range", ("min", Number(InputLimits.MinNewPageSideMm)), ("max", Number(InputLimits.MaxNewPageSideMm))));
        range.Margin = new Thickness(0, 0, 0, 12);
        var body = Form(
            (Label(Strings.Get("transform.size")), sizeBox),
            (Label(Strings.Get("newNote.orientation")), new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10), Children = { portrait, landscape } }),
            (Label(Strings.Get("newNote.widthHeight"), 4), new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4), Children = { widthBox, new TextBlock { Text = " × ", VerticalAlignment = VerticalAlignment.Center }, heightBox, new TextBlock { Text = " mm", VerticalAlignment = VerticalAlignment.Center } } }),
            (new Panel(), range),
            (Label(Strings.Get("newNote.background")), new StackPanel { Margin = new Thickness(0, 0, 0, 10), Children = { blank, grid, lined } }),
            (null, sameSize));
        body.MinWidth = 340;
        Build(body, Strings.Get("common.create"));
        ShowPreset();
    }

    /// <summary>A blank page (spec 11.5): the reference page's size and background are chosen already, so Enter creates it.</summary>
    public NewNoteDialog(string title, (double WidthMm, double HeightMm) referenceSize, PageBackgroundKind background = PageBackgroundKind.Blank) : this()
    {
        Title = title;
        reference = referenceSize;
        sameSize.IsVisible = true;
        sameSize.IsChecked = true;
        SameSizeChanged();
        (background switch { PageBackgroundKind.Grid => grid, PageBackgroundKind.Lined => lined, _ => blank }).IsChecked = true;
        Opened += (_, _) => OkButton.Focus();
    }

    public NewPageSpec? Result { get; private set; }

    public CheckBox SameSizeBox => sameSize;
    public ComboBox SizeBox => sizeBox;
    public TextBox WidthBox => widthBox;
    public TextBox HeightBox => heightBox;
    public RadioButton GridButton => grid;
    public RadioButton LinedButton => lined;
    public RadioButton LandscapeButton => landscape;

    private bool SameSize => reference is not null && sameSize.IsChecked == true;
    private PaperSize SelectedSize => Sizes[Math.Max(0, sizeBox.SelectedIndex)].Size;
    private PageOrientation SelectedOrientation => landscape.IsChecked == true ? PageOrientation.Landscape : PageOrientation.Portrait;
    private PageBackground SelectedBackground => grid.IsChecked == true ? PageBackground.Grid : lined.IsChecked == true ? PageBackground.Lined : PageBackground.Blank;

    private void SameSizeChanged()
    {
        sizeBox.IsEnabled = portrait.IsEnabled = landscape.IsEnabled = !SameSize;
        if (SameSize && reference is { } r)
        {
            widthBox.Text = Format(r.WidthMm);
            heightBox.Text = Format(r.HeightMm);
            widthBox.IsReadOnly = heightBox.IsReadOnly = true;
        }
        else ShowPreset();
    }

    private void OrientationChanged()
    {
        if (SelectedSize != PaperSize.Custom) { ShowPreset(); return; }
        // Custom: swap the entered values when they do not match the chosen orientation.
        if (TryParse(widthBox.Text, out double w) && TryParse(heightBox.Text, out double h) && (SelectedOrientation == PageOrientation.Landscape) != (w > h) && w != h)
            (widthBox.Text, heightBox.Text) = (heightBox.Text, widthBox.Text);
    }

    private void ShowPreset()
    {
        if (SameSize) return;
        bool custom = SelectedSize == PaperSize.Custom;
        widthBox.IsReadOnly = heightBox.IsReadOnly = !custom;
        if (custom) { widthBox.Focus(); widthBox.SelectAll(); return; }
        var spec = NewPageSpec.FromPreset(SelectedSize, SelectedOrientation, PageBackground.Blank);
        widthBox.Text = Format(spec.WidthMm);
        heightBox.Text = Format(spec.HeightMm);
    }

    public override bool Accept()
    {
        NewPageSpec spec;
        if (SameSize && reference is { } r) spec = new NewPageSpec(r.WidthMm, r.HeightMm, SelectedBackground);
        else if (SelectedSize == PaperSize.Custom)
        {
            if (!TryParse(widthBox.Text, out double w) || !TryParse(heightBox.Text, out double h)) return Fail(Strings.Get("newNote.enterNumbers"));
            spec = new NewPageSpec(w, h, SelectedBackground);
        }
        else spec = NewPageSpec.FromPreset(SelectedSize, SelectedOrientation, SelectedBackground);
        if (spec.Validate() is { } error)
            return Fail(Strings.Format(error, ("min", InputLimits.MinNewPageSideMm.ToString("0", CultureInfo.CurrentCulture)), ("max", InputLimits.MaxNewPageSideMm.ToString("0", CultureInfo.CurrentCulture))));
        Result = spec;
        return true;
    }
}
