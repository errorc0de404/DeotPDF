namespace PdfNotes.ViewModels;

/// <summary>
/// The text of a box the user types a value into (a bar's width, the page number; roadmap P4.5 R4). It shows the current
/// value until the user types; what is typed stays while the value changes underneath, until it is applied or reset.
/// </summary>
public sealed class InputField(string initial = "") : ObservableObject
{
    private string text = initial, shown = initial;

    /// <summary>The box's text: the value shown last, or what the user is typing.</summary>
    public string Text { get => text; set => SetProperty(ref text, value ?? ""); }

    /// <summary>The value the box shows when the user is not typing.</summary>
    public string Shown => shown;

    /// <summary>The user typed something other than the value shown.</summary>
    public bool Edited => text != shown;

    /// <summary>The value changed: the box follows unless the user is typing.</summary>
    public void Show(string value)
    {
        bool follow = !Edited;
        shown = value;
        if (follow) Text = value;
    }

    /// <summary>Enter, Esc or leaving the box: it shows the value again.</summary>
    public void Reset() => Text = shown;
}
