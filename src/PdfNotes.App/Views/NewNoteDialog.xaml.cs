using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PdfNotes.Core.Models;
using PdfNotes.App.Themes;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App.Views;

/// <summary>New note: size preset or custom mm, orientation, background (spec 4.1). Result in <see cref="Result"/>.</summary>
public partial class NewNoteDialog : Window
{
    private readonly (double WidthMm, double HeightMm)? reference;

    public NewNoteDialog()
    {
        Theme.Attach(this);
        InitializeComponent();
        RangeText.Text = Strings.Format("newNote.range", ("min", string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0}", InputLimits.MinNewPageSideMm)), ("max", string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0}", InputLimits.MaxNewPageSideMm)));
        ShowPreset();
    }

    /// <summary>
    /// Blank page add/insert (spec 11.5): same size (displayed size) and background as the reference page are chosen
    /// already, so Enter creates it.
    /// </summary>
    public NewNoteDialog(string title, (double WidthMm, double HeightMm) referenceSize, PageBackgroundKind background = PageBackgroundKind.Blank) : this()
    {
        Title = title;
        reference = referenceSize;
        SameSizeBox.Visibility = Visibility.Visible;
        SameSizeBox.IsChecked = true;
        SameSizeChanged(this, new RoutedEventArgs());
        (background switch { PageBackgroundKind.Grid => GridButton, PageBackgroundKind.Lined => LinedButton, _ => BlankButton }).IsChecked = true;
        Loaded += (_, _) => CreateButton.Focus();
    }

    private bool SameSize => reference is not null && SameSizeBox.IsChecked == true;

    private void SameSizeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        SizeBox.IsEnabled = PortraitButton.IsEnabled = LandscapeButton.IsEnabled = !SameSize;
        if (SameSize && reference is { } r)
        {
            WidthBox.Text = Format(r.WidthMm); HeightBox.Text = Format(r.HeightMm);
            WidthBox.IsReadOnly = HeightBox.IsReadOnly = true;
        }
        else ShowPreset();
    }

    public NewPageSpec? Result { get; private set; }

    private PaperSize SelectedSize =>
        Enum.Parse<PaperSize>((string)((ComboBoxItem)SizeBox.SelectedItem).Tag);

    private PageOrientation SelectedOrientation =>
        LandscapeButton.IsChecked == true ? PageOrientation.Landscape : PageOrientation.Portrait;

    private PageBackground SelectedBackground =>
        GridButton.IsChecked == true ? PageBackground.Grid : LinedButton.IsChecked == true ? PageBackground.Lined : PageBackground.Blank;

    private void PaperSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        ShowPreset();
    }

    private void OrientationChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (SelectedSize != PaperSize.Custom) { ShowPreset(); return; }
        // Custom: swap the entered values when they do not match the chosen orientation.
        if (TryParse(WidthBox.Text, out double w) && TryParse(HeightBox.Text, out double h) &&
            (SelectedOrientation == PageOrientation.Landscape) != (w > h) && w != h)
            (WidthBox.Text, HeightBox.Text) = (HeightBox.Text, WidthBox.Text);
    }

    private void ShowPreset()
    {
        bool custom = SelectedSize == PaperSize.Custom;
        WidthBox.IsReadOnly = HeightBox.IsReadOnly = !custom;
        if (custom) { WidthBox.Focus(); WidthBox.SelectAll(); return; }
        var spec = NewPageSpec.FromPreset(SelectedSize, SelectedOrientation, PageBackground.Blank);
        WidthBox.Text = Format(spec.WidthMm);
        HeightBox.Text = Format(spec.HeightMm);
    }

    private void CreateClick(object sender, RoutedEventArgs e)
    {
        NewPageSpec spec;
        if (SameSize && reference is { } r) spec = new NewPageSpec(r.WidthMm, r.HeightMm, SelectedBackground);
        else if (SelectedSize == PaperSize.Custom)
        {
            if (!TryParse(WidthBox.Text, out double w) || !TryParse(HeightBox.Text, out double h))
            {
                ShowError(Strings.Get("newNote.enterNumbers"));
                return;
            }
            spec = new NewPageSpec(w, h, SelectedBackground);
        }
        else spec = NewPageSpec.FromPreset(SelectedSize, SelectedOrientation, SelectedBackground);
        if (spec.Validate() is { } error)
        {
            ShowError(Strings.Format(error, ("min", InputLimits.MinNewPageSideMm.ToString("0", System.Globalization.CultureInfo.CurrentCulture)),
                ("max", InputLimits.MaxNewPageSideMm.ToString("0", System.Globalization.CultureInfo.CurrentCulture))));
            return;
        }
        Result = spec;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static string Format(double mm) => mm.ToString("0.##", CultureInfo.CurrentCulture);

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
