using System.Globalization;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Layout;

namespace PdfNotes.ViewModels;

/// <summary>A place to go back to: the page (by ID, so page moves do not matter), the point at the view center and the zoom.</summary>
public sealed record ViewPlace(Guid PageId, Point2 Center, double Zoom);

/// <summary>
/// Moving around the document (P1 V4): the page and zoom of the status bar, the view mode, the page keys and the
/// back/forward history of link and outline jumps (spec 4.4, 24.2, 24.3). A view change only: no Undo, no unsaved mark.
/// </summary>
public sealed class NavigationViewModel(MainViewModel owner, IMainView view) : ObservableObject
{
    public const int MaxHistory = 50;
    private readonly List<ViewPlace> back = [], forward = [];

    private IDocumentViewport Viewport => view.Viewport;

    // ----- status bar -----

    /// <summary>`3` (one-based).</summary>
    public string PageNumberText => (Viewport.CurrentPageIndex + 1).ToString(CultureInfo.CurrentCulture);

    /// <summary>`/ 12`.</summary>
    public string PageCountText => $"/ {Viewport.PageCount}";

    /// <summary>`125%`.</summary>
    public string ZoomText => ZoomSteps.Format(Viewport.Zoom, CultureInfo.CurrentCulture);

    public ViewMode Mode => Viewport.Mode;

    /// <summary>The marks over the pages (search results, selection, an open note's icon) are drawn again.</summary>
    public void RefreshOverlays() => Viewport.RefreshOverlays();

    /// <summary>The page in view.</summary>
    public int CurrentPageIndex => Viewport.CurrentPageIndex;

    public bool IsContinuous => Mode == ViewMode.Continuous;
    public bool IsSinglePage => Mode == ViewMode.SinglePage;

    /// <summary>The view scrolled, zoomed or changed its pages: the status bar, the active page and the outline follow.</summary>
    public void ViewChanged()
    {
        owner.Pages.FollowView();
        owner.Outline.Highlight();
        OnPropertyChanged(nameof(PageNumberText));
        OnPropertyChanged(nameof(PageCountText));
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsContinuous));
        OnPropertyChanged(nameof(IsSinglePage));
        // A box the user is typing in keeps its text; one showing the last value follows the view.
        string page = PageNumberText, zoom = ZoomText;
        if (PageInput == pageShown) PageInput = page;
        if (ZoomInput == zoomShown) ZoomInput = zoom;
        (pageShown, zoomShown) = (page, zoom);
    }

    // ----- the page box and the zoom box (R4c) -----

    private string pageInput = "1", pageShown = "1", zoomInput = "100%", zoomShown = "100%";

    /// <summary>The page box's text: the current page, or what the user is typing.</summary>
    public string PageInput { get => pageInput; set => SetProperty(ref pageInput, value); }

    /// <summary>The zoom box's text: the current zoom, or what the user is typing.</summary>
    public string ZoomInput { get => zoomInput; set => SetProperty(ref zoomInput, value); }

    /// <summary>Enter in the page box: go there (a number that is not one is ignored), then show the page again.</summary>
    public void CommitPageInput()
    {
        GoToPageNumber(PageInput);
        ResetPageInput();
    }

    /// <summary>Esc or leaving the page box: show the current page again.</summary>
    public void ResetPageInput() => PageInput = pageShown = PageNumberText;

    /// <summary>Enter in the zoom box, or a preset picked from its list: zoom there, then show the zoom again.</summary>
    public void CommitZoomInput(string? text = null)
    {
        SetZoomText(text ?? ZoomInput);
        ResetZoomInput();
    }

    /// <summary>Esc or leaving the zoom box: show the current zoom again.</summary>
    public void ResetZoomInput() => ZoomInput = zoomShown = ZoomText;

    /// <summary>The zoom box's list (up to 800 %).</summary>
    public IReadOnlyList<string> ZoomPresets { get; } = [.. ZoomSteps.MenuPercents.Where(p => p <= 800).Select(p => p + "%")];

    // ----- commands (R4c) -----

    public Command PreviousPageCommand => previousPage ??= new(PreviousPage);
    public Command NextPageCommand => nextPage ??= new(NextPage);
    public Command ZoomInCommand => zoomIn ??= new(() => ZoomBy(1.25));
    public Command ZoomOutCommand => zoomOut ??= new(() => ZoomBy(1 / 1.25));
    public Command FitWidthCommand => fitWidth ??= new(FitWidth);
    public Command FitPageCommand => fitPage ??= new(FitPage);
    public Command ContinuousCommand => continuous ??= new(() => SetMode(ViewMode.Continuous));
    public Command SinglePageCommand => singlePage ??= new(() => SetMode(ViewMode.SinglePage));
    public Command ZoomMenuCommand => zoomMenu ??= new(() => owner.Shell.ShowZoomMenu());
    private Command? previousPage, nextPage, zoomIn, zoomOut, fitWidth, fitPage, continuous, singlePage, zoomMenu;

    /// <summary>The page box: a one-based number goes there (clamped); anything else is ignored.</summary>
    public bool GoToPageNumber(string text)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int number)) return false;
        Viewport.GoToPage(number - 1);
        return true;
    }

    /// <summary>The zoom box: `150`, `150%`.</summary>
    public bool SetZoomText(string text)
    {
        if (!ZoomSteps.TryParse(text, CultureInfo.CurrentCulture, out double zoom)) return false;
        Viewport.SetZoom(zoom);
        return true;
    }

    public void SetZoom(double zoom) => Viewport.SetZoom(zoom);

    /// <summary>The zoom buttons: 1.25 times in or out.</summary>
    public void ZoomBy(double factor) => Viewport.SetZoom(Viewport.Zoom * factor);

    /// <summary>The next (<paramref name="direction"/> 1) or previous (-1) zoom step around the view center (spec 24.2).</summary>
    public void ZoomByStep(int direction) => Viewport.SetZoom(ZoomSteps.Step(Viewport.Zoom, direction));

    public void FitWidth() => Viewport.FitWidth();
    public void FitPage() => Viewport.FitPage();
    public void NextPage() => Viewport.GoToPage(Viewport.CurrentPageIndex + 1);
    public void PreviousPage() => Viewport.GoToPage(Viewport.CurrentPageIndex - 1);

    /// <summary>Zoom presets of the status bar menu (spec 4.4), the current one checked.</summary>
    public IReadOnlyList<(int Percent, bool Current)> ZoomMenu()
    {
        int current = (int)Math.Round(Viewport.Zoom * 100);
        return [.. ZoomSteps.MenuPercents.Select(p => (p, p == current))];
    }

    /// <summary>Continuous or single page, remembered (app setting).</summary>
    public void SetMode(ViewMode mode)
    {
        Viewport.SetMode(mode);
        owner.Settings.ViewMode = mode;
        owner.SaveSettings();
        ViewChanged();
    }

    // ----- shortcuts -----

    /// <summary>
    /// Runs a view shortcut (zoom, history, page keys). Returns true when the key is used — also when the page must stay
    /// because a stroke is in progress (the 2026-10-15 crash: the page under the pen left the view). Favorites are the view's.
    /// </summary>
    public bool Run(ShortcutCommand command, bool repeat)
    {
        if (Viewport.IsToolActive && command is ShortcutCommand.SidewaysLeft or ShortcutCommand.SidewaysRight or ShortcutCommand.PreviousPage
            or ShortcutCommand.NextPage or ShortcutCommand.FirstPage or ShortcutCommand.LastPage) return true;
        switch (command)
        {
            case ShortcutCommand.ZoomIn: ZoomByStep(1); return true;
            case ShortcutCommand.ZoomOut: ZoomByStep(-1); return true;
            case ShortcutCommand.FitWidth: FitWidth(); return true;
            case ShortcutCommand.Back: GoBack(); return true;
            case ShortcutCommand.Forward: GoForward(); return true;
            case ShortcutCommand.ScrollUp: Viewport.LineUp(); return true;
            case ShortcutCommand.ScrollDown: Viewport.LineDown(); return true;
            case ShortcutCommand.SidewaysLeft: Viewport.ArrowSideways(-1, repeat); return true;
            case ShortcutCommand.SidewaysRight: Viewport.ArrowSideways(1, repeat); return true;
            case ShortcutCommand.PreviousPage: PreviousPage(); return true;
            case ShortcutCommand.NextPage: NextPage(); return true;
            case ShortcutCommand.FirstPage: Viewport.GoToPage(0); return true;
            case ShortcutCommand.LastPage: Viewport.GoToPage(Viewport.PageCount - 1); return true;
            default: return false;
        }
    }

    // ----- back/forward of jumps (spec 24.3) -----

    public bool CanGoBack => back.Count > 0;
    public bool CanGoForward => forward.Count > 0;

    /// <summary>Jumps belong to one document.</summary>
    public void DocumentReplaced()
    {
        back.Clear();
        forward.Clear();
        HistoryChanged();
    }

    /// <summary>Goes to a page (and a point of its source page) remembering where the view was (links, outline).</summary>
    public void JumpTo(int pageIndex, Point2? sourcePoint)
    {
        if (Here() is { } here)
        {
            back.Add(here);
            if (back.Count > MaxHistory) back.RemoveAt(0);
        }
        forward.Clear();
        HistoryChanged();
        if (sourcePoint is { } p && owner.Document is { } doc)
        {
            var placement = doc.Session.Current.Pages[pageIndex].Setup.Content?.Transform ?? Affine.Identity;
            Viewport.ShowPagePoint(pageIndex, placement.Transform(p));
        }
        else Viewport.GoToPage(pageIndex);
    }

    /// <summary>Alt+← / mouse back: the place before the last jump (places on deleted pages are skipped).</summary>
    public bool GoBack() => Step(back, forward);

    /// <summary>Alt+→ / mouse forward.</summary>
    public bool GoForward() => Step(forward, back);

    private bool Step(List<ViewPlace> from, List<ViewPlace> to)
    {
        try
        {
            while (from.Count > 0)
            {
                var place = from[^1];
                from.RemoveAt(from.Count - 1);
                int index = owner.Document?.Session.Current.Pages.FindIndex(p => p.Id == place.PageId) ?? -1;
                if (index < 0) continue;
                if (Here() is { } here) to.Add(here);
                Viewport.SetZoom(place.Zoom);
                Viewport.ScrollToPagePoint(index, place.Center);
                return true;
            }
            return false;
        }
        finally { HistoryChanged(); }
    }

    private ViewPlace? Here() => owner.Document is { } doc && Viewport.CenterAnchor is { } a && a.PageIndex < doc.Session.Current.Pages.Count
        ? new(doc.Session.Current.Pages[a.PageIndex].Id, a.PagePoint, Viewport.Zoom) : null;

    private void HistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }
}
