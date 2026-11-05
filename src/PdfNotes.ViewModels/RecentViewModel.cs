using PdfNotes.Core.Geometry;
using PdfNotes.Editor.Services;

namespace PdfNotes.ViewModels;

/// <summary>
/// Recent documents and the last position in each document (spec 24.1, D13h; P1 V2). All of it is app settings: never
/// written into a document, never Undo or the unsaved mark. The window placement stays with the view.
/// </summary>
public sealed class RecentViewModel(AppSettings settings, Action save) : ObservableObject
{
    /// <summary>Recent documents, most recent first.</summary>
    public IReadOnlyList<string> Files => settings.RecentFiles;

    public bool HasFiles => settings.RecentFiles.Count > 0;

    public void Add(string path)
    {
        path = Path.GetFullPath(path);
        settings.RecentFiles.RemoveAll(p => Same(p, path));
        settings.RecentFiles.Insert(0, path);
        if (settings.RecentFiles.Count > AppSettings.MaxRecentFiles) settings.RecentFiles.RemoveRange(AppSettings.MaxRecentFiles, settings.RecentFiles.Count - AppSettings.MaxRecentFiles);
        Changed();
    }

    public void Remove(string path)
    {
        settings.RecentFiles.RemoveAll(p => Same(p, path));
        Changed();
    }

    private void Changed()
    {
        save();
        OnPropertyChanged(nameof(Files));
        OnPropertyChanged(nameof(HasFiles));
    }

    /// <summary>Remembers where a document was shown (page, point at the view center, zoom). Saved with the other settings.</summary>
    public void RememberPosition(string path, int page, Point2 point, double zoom)
    {
        path = Path.GetFullPath(path);
        settings.Positions.RemoveAll(p => Same(p.Path, path));
        settings.Positions.Insert(0, new() { Path = path, Page = page, X = point.X, Y = point.Y, Zoom = zoom });
        if (settings.Positions.Count > AppSettings.MaxPositions) settings.Positions.RemoveRange(AppSettings.MaxPositions, settings.Positions.Count - AppSettings.MaxPositions);
    }

    /// <summary>Where <paramref name="path"/> was left, or null for a document seen for the first time.</summary>
    public AppSettings.DocumentPosition? PositionOf(string path) => settings.Positions.FirstOrDefault(p => Same(p.Path, Path.GetFullPath(path)));

    private static bool Same(string a, string b) => FilePaths.Same(a, b);
}
