using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;

namespace PdfNotes.ViewModels;

/// <summary>
/// The four pen favorites (spec 24.7; P1 V4): their slots (app settings), names and kinds. Applying one changes the
/// tool styles, which stay with the view until the style bar moves (V7).
/// </summary>
public sealed class FavoritesViewModel(AppSettings settings, Action save) : ObservableObject
{
    public IReadOnlyList<AppSettings.FavoriteSettings?> Slots => settings.Favorites;

    public void Store(int slot, AppSettings.FavoriteSettings favorite)
    {
        settings.Favorites[slot] = favorite;
        Changed();
    }

    public void Clear(int slot)
    {
        settings.Favorites[slot] = null;
        Changed();
    }

    private void Changed()
    {
        save();
        OnPropertyChanged(nameof(Slots));
    }

    /// <summary>`펜 2pt (Ctrl+1)`, `밑줄 1.5pt (Ctrl+3)`; an empty slot says how to fill it.</summary>
    public static string Tip(AppSettings.FavoriteSettings? slot, int index)
    {
        if (slot is null) return Strings.Get("favorites.emptyTip");
        string name = slot.Tool != ToolKind.FreeHighlight ? Strings.Get("ribbon.pen") : slot.MarkKind switch
        {
            TextMarkupKind.Underline => Strings.Get("markup.underline"),
            TextMarkupKind.StrikeOut => Strings.Get("text.strikeOut"),
            _ => Strings.Get("ribbon.highlighter")
        };
        return Strings.Format("favorites.tip", ("name", name), ("width", Math.Round(slot.Style.WidthPt, 1)), ("key", index + 1));
    }

    /// <summary>The annotation a favorite draws: ink (pen), free highlight, or a text mark (underline, strikeout).</summary>
    public static AnnotationKind Kind(AppSettings.FavoriteSettings slot) =>
        slot.Tool != ToolKind.FreeHighlight ? AnnotationKind.Ink : slot.MarkKind is null ? AnnotationKind.FreeHighlight : AnnotationKind.TextMarkup;
}
