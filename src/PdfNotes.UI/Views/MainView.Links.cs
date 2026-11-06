using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Input;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Services;
using PdfNotes.Editor.Tools;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.UI.Views;

/// <summary>
/// PDF links and the back/forward history of jumps (spec 24.3, D13h), as the WPF window: links are read from the source
/// pages (never part of the document model) and placed on the current paper like the glyphs; following one is a view
/// change only.
/// </summary>
public sealed partial class MainView
{
    private readonly ConcurrentDictionary<int, PdfLink[]> sourceLinks = new();
    private OpenDocument? linkDocument;

    /// <summary>Opens a URI after the user agreed (tests: replaces the browser).</summary>
    public Action<string>? LinkLauncherOverride { get; set; }

    private void InitializeLinks()
    {
        Canvas.LinkTip = p => LinkAt(p) is { } hit ? Describe(hit.Link) : null;
        Canvas.LinkFollowsClick = () => tools.Current == ToolKind.Select;
        Canvas.LinkClicked += (_, p) =>
        {
            // A click that selected an annotation over the link edits it instead (selection tool).
            if (selectTool.SelectedIds.Count > 0 && !lastCtrl) return;
            if (LinkAt(p) is { } hit) FollowLink(hit.Link);
        };
        // The mouse's back and forward buttons (spec 24.3).
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            lastCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (kind == PointerUpdateKind.XButton1Pressed && viewModel.Navigation.GoBack()) e.Handled = true;
            else if (kind == PointerUpdateKind.XButton2Pressed && viewModel.Navigation.GoForward()) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private bool lastCtrl;

    /// <summary>Links of a model page, placed on its current paper (D10 content transform).</summary>
    public IReadOnlyList<PdfLink> PageLinks(int pageIndex)
    {
        if (Document is not { } doc || pageIndex < 0 || pageIndex >= doc.Session.Current.Pages.Count) return [];
        if (!ReferenceEquals(linkDocument, doc)) { linkDocument = doc; sourceLinks.Clear(); }
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
    private (PdfLink Link, int PageIndex)? LinkAt(Point2 viewport)
    {
        if (Canvas.HitTestPage(viewport) is not { } at) return null;
        foreach (var link in PageLinks(at.PageIndex).Reverse()) // the last one is drawn on top
            if (TextSelection.InQuad(link.Area, at.PagePoint) && (link.Uri is not null || viewModel.PageShowing(link.TargetPage) is not null)) return (link, at.PageIndex);
        return null;
    }

    private string Describe(PdfLink link) => link.Uri ?? Strings.Format("link.pageTip", ("page", viewModel.PageShowing(link.TargetPage)!.Value + 1));

    /// <summary>Internal targets jump (remembered for Alt+←); http, https and mailto open in the browser after a question; nothing else opens.</summary>
    public void FollowLink(PdfLink link)
    {
        if (link.Uri is { } uri)
        {
            if (!IsOpenableUri(uri) || !Confirm(Strings.Format("link.openConfirm", ("uri", uri)))) return;
            if (LinkLauncherOverride is { } launch) { launch(uri); return; }
            OpenWebPage(uri);
            return;
        }
        if (viewModel.PageShowing(link.TargetPage) is { } page) viewModel.Navigation.JumpTo(page, link.TargetPoint);
    }

    public static bool IsOpenableUri(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" or "mailto";

    /// <summary>A web page or mail link in the system's browser (update button, links).</summary>
    private static void OpenWebPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException) { }
    }
}
