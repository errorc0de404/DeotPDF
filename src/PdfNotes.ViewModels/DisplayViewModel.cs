using PdfNotes.Editor.Services;

namespace PdfNotes.ViewModels;

/// <summary>
/// 화면 values of the settings window (spec 18.1, D13d): theme, accent color, page numbers (4.5), automatic 필기 ↔ 페이지
/// tab switch (3.2), reading dark page (24.12, off) and the pen favorites group (24.7, on).
/// </summary>
public sealed record DisplayValues(ThemeChoice Theme, AccentColor Accent, bool PageNumbers, bool AutoSwitchTabs = true, bool DarkPages = false, bool Favorites = true,
    PageNumberSize NumberSize = PageNumberSize.Normal)
{
    public static DisplayValues Default { get; } = new(ThemeChoice.System, AccentColor.Blue, true);
}

/// <summary>
/// How the pages are shown (P1 V4): reading dark page (spec 24.12), page numbers (4.5), the favorites group (24.7) and
/// hidden notes (24.10). The settings window previews its 화면 values here before they are stored. None of it changes
/// how a document is saved. The theme itself is the platform's (the view applies <see cref="Theme"/>).
/// </summary>
public sealed class DisplayViewModel : ObservableObject
{
    private readonly AppSettings settings;
    private readonly Action save;
    private bool darkPages, pageNumbers, favoritesShown, notesHidden;
    private PageNumberSize numberSize;
    private ThemeChoice theme;
    private AccentColor accent;

    public DisplayViewModel(AppSettings settings, Action save)
    {
        this.settings = settings;
        this.save = save;
        darkPages = settings.DarkPages;
        pageNumbers = settings.ShowPageNumbers;
        favoritesShown = settings.ShowFavorites;
        numberSize = settings.PageNumberSize;
        theme = settings.Theme;
        accent = settings.Accent;
    }

    /// <summary>The page display is inverted (also while the settings window previews it).</summary>
    public bool DarkPages { get => darkPages; private set => SetProperty(ref darkPages, value); }

    public bool PageNumbers { get => pageNumbers; private set => SetProperty(ref pageNumbers, value); }

    public PageNumberSize NumberSize { get => numberSize; private set => SetProperty(ref numberSize, value); }

    /// <summary>The page number height in DIPs.</summary>
    public double PageNumberDip => numberSize switch { PageNumberSize.Small => 11, PageNumberSize.Large => 18, _ => 14 };

    public bool FavoritesShown { get => favoritesShown; private set => SetProperty(ref favoritesShown, value); }

    public ThemeChoice Theme { get => theme; private set => SetProperty(ref theme, value); }
    public AccentColor Accent { get => accent; private set => SetProperty(ref accent, value); }

    /// <summary>
    /// App annotations are hidden on the pages and thumbnails (보기 › 필기 숨기기). View state only: no Undo, no unsaved
    /// mark. The view finishes editing and switches tools around the change (spec 24.10, D14g).
    /// </summary>
    public bool NotesHidden { get => notesHidden; set => SetProperty(ref notesHidden, value); }

    /// <summary>The stored 화면 settings.</summary>
    public DisplayValues Stored => new(settings.Theme, settings.Accent, settings.ShowPageNumbers, settings.AutoSwitchTabs, settings.DarkPages, settings.ShowFavorites,
        settings.PageNumberSize);

    /// <summary>Shows the values now without storing them (settings window preview; cancel previews <see cref="Stored"/> again).</summary>
    public void Preview(DisplayValues values)
    {
        Theme = values.Theme;
        Accent = values.Accent;
        DarkPages = values.DarkPages;
        FavoritesShown = values.Favorites;
        NumberSize = values.NumberSize;
        PageNumbers = values.PageNumbers;
    }

    /// <summary>Applies and stores the values (an app setting: no Undo, the document stays unchanged).</summary>
    public void Apply(DisplayValues values)
    {
        Preview(values);
        settings.Theme = values.Theme;
        settings.Accent = values.Accent;
        settings.ShowPageNumbers = values.PageNumbers;
        settings.AutoSwitchTabs = values.AutoSwitchTabs;
        settings.DarkPages = values.DarkPages;
        settings.ShowFavorites = values.Favorites;
        settings.PageNumberSize = values.NumberSize;
        save();
    }
}
