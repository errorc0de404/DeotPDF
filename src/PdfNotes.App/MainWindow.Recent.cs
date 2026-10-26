using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PdfNotes.App.Services;
using PdfNotes.Core.Geometry;
using PdfNotes.Editor.Localization;

namespace PdfNotes.App;

/// <summary>
/// Recent documents, the last position in each document and the window placement (spec 24.1, D13h). All of it is app
/// settings: never written into a document, never Undo or the unsaved mark.
/// </summary>
public partial class MainWindow
{
    /// <summary>Recent documents, most recent first (validation harness and the 파일 tab menu).</summary>
    public IReadOnlyList<string> RecentFiles => settings.RecentFiles;

    /// <summary>The recent documents menu opened last (validation harness).</summary>
    public ContextMenu? LastRecentMenu { get; private set; }

    private void AddRecent(string path)
    {
        path = Path.GetFullPath(path);
        settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        settings.RecentFiles.Insert(0, path);
        if (settings.RecentFiles.Count > AppSettings.MaxRecentFiles) settings.RecentFiles.RemoveRange(AppSettings.MaxRecentFiles, settings.RecentFiles.Count - AppSettings.MaxRecentFiles);
        settings.Save();
        UpdateRecentButton();
    }

    public void RemoveRecent(string path)
    {
        settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        settings.Save();
        UpdateRecentButton();
    }

    private void UpdateRecentButton() => RecentButton.IsEnabled = settings.RecentFiles.Count > 0;

    /// <summary>파일 › 최근 문서 ▾: file names (full path as tooltip), most recent first; right-click 목록에서 제거.</summary>
    private void RecentClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = RecentButton, Placement = PlacementMode.Bottom };
        foreach (string path in settings.RecentFiles.ToArray())
        {
            var item = new MenuItem { Header = Path.GetFileName(path), ToolTip = path };
            item.Click += async (_, _) => await OpenRecentAsync(path);
            var remove = new MenuItem { Header = Strings.Get("recent.remove") };
            remove.Click += (_, _) => RemoveRecent(path);
            item.ContextMenu = new ContextMenu { Items = { remove } };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        LastRecentMenu = menu;
    }

    /// <summary>Opens a recent document like 열기; a file that is gone is reported briefly and leaves the list.</summary>
    public async Task<bool> OpenRecentAsync(string path)
    {
        if (!File.Exists(path))
        {
            RemoveRecent(path);
            StatusText.Text = Strings.Format("recent.missing", ("name", Path.GetFileName(path)));
            return false;
        }
        return await OpenFileAsync(path);
    }

    /// <summary>Remembers where the document was shown (page, point at the view center, zoom); new notes are not remembered.</summary>
    private void RememberPosition(OpenDocument? doc)
    {
        if (doc?.FilePath is not { } path || !ReferenceEquals(doc, document) || View.CenterAnchor is not { } anchor) return;
        path = Path.GetFullPath(path);
        settings.Positions.RemoveAll(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        settings.Positions.Insert(0, new() { Path = path, Page = anchor.PageIndex, X = anchor.PagePoint.X, Y = anchor.PagePoint.Y, Zoom = View.Zoom });
        if (settings.Positions.Count > AppSettings.MaxPositions) settings.Positions.RemoveRange(AppSettings.MaxPositions, settings.Positions.Count - AppSettings.MaxPositions);
    }

    /// <summary>Shows a reopened document where it was left; a document seen for the first time keeps the fit width start.
    /// Fewer pages than before go to the last page.</summary>
    private void RestorePosition(string path)
    {
        var saved = settings.Positions.FirstOrDefault(p => string.Equals(p.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (saved is null || View.PageCount == 0) return;
        int page = Math.Clamp(saved.Page, 0, View.PageCount - 1);
        View.SetZoom(saved.Zoom);
        if (page == saved.Page) View.ScrollToPagePoint(page, new Point2(saved.X, saved.Y));
        else View.GoToPage(page);
    }

    // ----- window placement -----

    /// <summary>Restores size, position and maximized state; a position off every connected screen moves onto the main screen.</summary>
    private void RestoreWindowPlacement()
    {
        if (settings.Window is not { } w || !double.IsFinite(w.Width) || !double.IsFinite(w.Height) || w.Width < MinWidth || w.Height < MinHeight) return;
        Width = w.Width;
        Height = w.Height;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var title = new Rect(w.Left, w.Top, w.Width, 32); // the title bar must be reachable
        if (double.IsFinite(w.Left) && double.IsFinite(w.Top) && screen.IntersectsWith(title) && Rect.Intersect(screen, title).Width >= 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = w.Left;
            Top = w.Top;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (w.Maximized) WindowState = WindowState.Maximized;
        SourceInitialized += FitRestoredSize;
    }

    /// <summary>
    /// A size remembered on a bigger (or less scaled) monitor is shrunk to the work area of the monitor the window opens on
    /// (D13e: e.g. saved at 1600 DIP, now on a 1366×768 screen at 125 %).
    /// </summary>
    private void FitRestoredSize(object? sender, EventArgs e)
    {
        SourceInitialized -= FitRestoredSize;
        if (WindowState != WindowState.Normal) return;
        var area = Views.ScreenArea.WorkArea(this);
        if (Width > area.Width) Width = Math.Max(MinWidth, area.Width);
        if (Height > area.Height) Height = Math.Max(MinHeight, area.Height);
    }

    private void RememberWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || bounds.Width <= 0) return;
        settings.Window = new() { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height, Maximized = WindowState == WindowState.Maximized };
    }
}
