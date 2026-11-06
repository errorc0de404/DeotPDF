using Avalonia;
using Avalonia.Controls;
using PdfNotes.Core.Geometry;
using PdfNotes.Editor.Services;

namespace PdfNotes.UI.Views;

/// <summary>
/// The last position in each document (spec 24.1, D13h): an app setting, never written into a document, never Undo or the
/// unsaved mark (the window placement is the desktop window's). The recent documents menu is in MainView.Ribbon.cs.
/// </summary>
public sealed partial class MainView
{
    public IReadOnlyList<string> RecentFiles => viewModel.Recent.Files;

    /// <summary>Remembers where the document was shown (page, point at the view center, zoom); new notes are not remembered.</summary>
    private void RememberPosition(OpenDocument? doc)
    {
        if (doc?.FilePath is not { } path || !ReferenceEquals(doc, Document) || Canvas.CenterAnchor is not { } anchor) return;
        viewModel.Recent.RememberPosition(path, anchor.PageIndex, anchor.PagePoint, Canvas.Zoom);
    }

    /// <summary>Shows a reopened document where it was left; fewer pages than before go to the last page.</summary>
    private void RestorePosition(string path)
    {
        var saved = viewModel.Recent.PositionOf(path);
        if (saved is null || Canvas.PageCount == 0) return;
        int page = Math.Clamp(saved.Page, 0, Canvas.PageCount - 1);
        Canvas.SetZoom(saved.Zoom);
        if (page == saved.Page) Canvas.ScrollToPagePoint(page, new Point2(saved.X, saved.Y));
        else Canvas.GoToPage(page);
    }
}
