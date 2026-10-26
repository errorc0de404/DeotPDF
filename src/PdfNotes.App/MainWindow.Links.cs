using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Input;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.App.Services;
using PdfNotes.Pdf.Reading;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

/// <summary>
/// PDF links and the back/forward history of jumps (spec 24.3, D13h). Links are read from the source pages (never part
/// of the document model) and placed on the current paper like the glyphs; following one is a view change only.
/// </summary>
public partial class MainWindow
{
    /// <summary>A place to go back to: the page (by ID, so page moves do not matter), the point at the view center and the zoom.</summary>
    private sealed record ViewPlace(Guid PageId, Point2 Center, double Zoom);

    public const int MaxHistory = 50;
    private readonly List<ViewPlace> backPlaces = [], forwardPlaces = [];
    private readonly ConcurrentDictionary<int, PdfLink[]> sourceLinks = new();
    private OpenDocument? linkDocument;

    /// <summary>Opens a URI after the user agreed (validation harness: replaces the browser).</summary>
    public Action<string>? LinkLauncherOverride { get; set; }

    public bool CanGoBack => backPlaces.Count > 0;
    public bool CanGoForward => forwardPlaces.Count > 0;

    private void InitializeLinks()
    {
        View.LinkTip = p => LinkAt(p) is { } hit ? Describe(hit.Link) : null;
        View.LinkFollowsClick = () => tools.Current == PdfNotes.Editor.Tools.ToolKind.Select;
        View.LinkClicked += (_, p) =>
        {
            // A click that selected an annotation over the link edits it instead (selection tool).
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && selectTool.SelectedIds.Count > 0) return;
            if (LinkAt(p) is { } hit) FollowLink(hit.Link);
        };
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1 && GoBack()) e.Handled = true;
            else if (e.ChangedButton == MouseButton.XButton2 && GoForward()) e.Handled = true;
        };
    }

    /// <summary>Links of a model page, placed on its current paper (D10 content transform).</summary>
    public IReadOnlyList<PdfLink> PageLinks(int pageIndex)
    {
        if (document is not { } doc || pageIndex < 0 || pageIndex >= doc.Session.Current.Pages.Count) return [];
        if (!ReferenceEquals(linkDocument, doc)) { linkDocument = doc; sourceLinks.Clear(); backPlaces.Clear(); forwardPlaces.Clear(); }
        var setup = doc.Session.Current.Pages[pageIndex].Setup;
        if (setup.Source is not { Kind: PageSourceKind.Pdf, PdfPageIndex: { } index } || index >= doc.Sources.Pages.Count) return [];
        var links = sourceLinks.GetOrAdd(index, i =>
        {
            try { return doc.Sources.Links(i); }
            catch (InvalidDataException) { return []; }
        });
        var placement = setup.Content?.Transform ?? Affine.Identity;
        return placement.IsIdentity ? links : links.Select(l => l with { Area = l.Area.Transform(placement) }).ToArray();
    }

    /// <summary>The followable link under a viewport point: a URI, or an internal target whose page is still in the document.</summary>
    private (PdfLink Link, int PageIndex)? LinkAt(Point viewport)
    {
        if (View.HitTestPage(viewport) is not { } at) return null;
        foreach (var link in PageLinks(at.PageIndex).Reverse()) // the last one is drawn on top
            if (TextSelection.InQuad(link.Area, at.PagePoint) && (link.Uri is not null || TargetPage(link.TargetPage) is not null)) return (link, at.PageIndex);
        return null;
    }

    private string Describe(PdfLink link) => link.Uri ?? Strings.Format("link.pageTip", ("page", TargetPage(link.TargetPage)!.Value + 1));

    /// <summary>The first page of the document that shows source page <paramref name="sourceIndex"/>, or null when it was deleted.</summary>
    private int? TargetPage(int? sourceIndex)
    {
        if (sourceIndex is not { } wanted || document is null) return null;
        var pages = document.Session.Current.Pages;
        for (int i = 0; i < pages.Count; i++)
            if (pages[i].Setup.Source is { Kind: PageSourceKind.Pdf, PdfPageIndex: { } index } && index == wanted) return i;
        return null;
    }

    /// <summary>Internal targets jump (remembered for Alt+←); http, https and mailto open in the browser after a question; nothing else opens.</summary>
    public void FollowLink(PdfLink link)
    {
        if (link.Uri is { } uri)
        {
            if (!IsOpenableUri(uri)) return;
            if (Prompt(Strings.Format("link.openConfirm", ("uri", uri)), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (LinkLauncherOverride is { } launch) { launch(uri); return; }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return;
        }
        if (TargetPage(link.TargetPage) is { } page) JumpTo(page, link.TargetPoint);
    }

    public static bool IsOpenableUri(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" or "mailto";

    /// <summary>Goes to a page (and a point of its source page) remembering where the view was.</summary>
    private void JumpTo(int pageIndex, Point2? sourcePoint)
    {
        if (Here() is { } here)
        {
            backPlaces.Add(here);
            if (backPlaces.Count > MaxHistory) backPlaces.RemoveAt(0);
        }
        forwardPlaces.Clear();
        ShowTarget(pageIndex, sourcePoint);
    }

    private void ShowTarget(int pageIndex, Point2? sourcePoint)
    {
        if (sourcePoint is { } p)
        {
            var placement = document!.Session.Current.Pages[pageIndex].Setup.Content?.Transform ?? Affine.Identity;
            View.ShowPagePoint(pageIndex, placement.Transform(p));
        }
        else View.GoToPage(pageIndex);
    }

    private ViewPlace? Here() => document is { } doc && View.CenterAnchor is { } a && a.PageIndex < doc.Session.Current.Pages.Count
        ? new(doc.Session.Current.Pages[a.PageIndex].Id, a.PagePoint, View.Zoom) : null;

    /// <summary>Alt+← / mouse back: the place before the last jump (places on deleted pages are skipped).</summary>
    public bool GoBack() => Step(backPlaces, forwardPlaces);

    /// <summary>Alt+→ / mouse forward.</summary>
    public bool GoForward() => Step(forwardPlaces, backPlaces);

    private bool Step(List<ViewPlace> from, List<ViewPlace> to)
    {
        while (from.Count > 0)
        {
            var place = from[^1];
            from.RemoveAt(from.Count - 1);
            int index = document?.Session.Current.Pages.FindIndex(p => p.Id == place.PageId) ?? -1;
            if (index < 0) continue;
            if (Here() is { } here) to.Add(here);
            View.SetZoom(place.Zoom);
            View.ScrollToPagePoint(index, place.Center);
            return true;
        }
        return false;
    }
}
