using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;

namespace PdfNotes.ViewModels;

/// <summary>
/// The four pen favorites (spec 24.7; P1 V4, R4d): their slots (app settings), names and kinds, and one item per button.
/// Applying one changes the tool styles and the tool (<see cref="ToolBarViewModel.ApplyFavorite"/>).
/// </summary>
public sealed class FavoritesViewModel : ObservableObject
{
    private readonly AppSettings settings;
    private readonly Action save;
    private readonly Texts texts;

    /// <param name="apply">A favorite button was clicked (an empty one stores the current pen).</param>
    /// <param name="store">즐겨찾기 교체 of a button.</param>
    public FavoritesViewModel(AppSettings settings, Action save, Texts texts, Action<int>? apply = null, Action<int>? store = null)
    {
        this.settings = settings;
        this.save = save;
        this.texts = texts;
        Items = [.. Enumerable.Range(0, settings.Favorites.Length).Select(i => new FavoriteItemViewModel(this, i, apply, store))];
    }

    public IReadOnlyList<AppSettings.FavoriteSettings?> Slots => settings.Favorites;

    /// <summary>The favorite buttons.</summary>
    public IReadOnlyList<FavoriteItemViewModel> Items { get; }

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
        foreach (var item in Items) item.Changed();
    }

    /// <summary>`펜 2pt (Ctrl+1)`, `밑줄 1.5pt (Ctrl+3)`; an empty slot says how to fill it.</summary>
    public string Tip(AppSettings.FavoriteSettings? slot, int index)
    {
        if (slot is null) return texts.Get("favorites.emptyTip");
        string name = slot.Tool != ToolKind.FreeHighlight ? texts.Get("ribbon.pen") : slot.MarkKind switch
        {
            TextMarkupKind.Underline => texts.Get("markup.underline"),
            TextMarkupKind.StrikeOut => texts.Get("text.strikeOut"),
            _ => texts.Get("ribbon.highlighter")
        };
        return texts.Format("favorites.tip", ("name", name), ("width", Math.Round(slot.Style.WidthPt, 1)), ("key", index + 1));
    }

    /// <summary>The annotation a favorite draws: ink (pen), free highlight, or a text mark (underline, strikeout).</summary>
    public static AnnotationKind Kind(AppSettings.FavoriteSettings slot) =>
        slot.Tool != ToolKind.FreeHighlight ? AnnotationKind.Ink : slot.MarkKind is null ? AnnotationKind.FreeHighlight : AnnotationKind.TextMarkup;
}

/// <summary>One favorite button: what it holds, its tip, and its click, 교체 and 지우기.</summary>
public sealed class FavoriteItemViewModel(FavoritesViewModel owner, int index, Action<int>? apply, Action<int>? store) : ObservableObject
{
    public int Index => index;

    /// <summary>The favorite, or null for an empty slot.</summary>
    public AppSettings.FavoriteSettings? Slot => owner.Slots[index];

    public string Tip => owner.Tip(Slot, index);

    public bool CanClear => Slot is not null;

    public Command Apply => field ??= new(() => apply?.Invoke(index));
    public Command Store => field ??= new(() => store?.Invoke(index));
    public Command Clear => field ??= new(() => owner.Clear(index), () => Slot is not null);

    internal void Changed()
    {
        OnPropertyChanged(nameof(Slot));
        OnPropertyChanged(nameof(Tip));
        OnPropertyChanged(nameof(CanClear));
        Clear.Refresh();
    }
}
