namespace PdfNotes.Tests;

/// <summary>Temporary folders for tests, so nothing writes into the user's settings folder.</summary>
internal static class TestFolders
{
    /// <summary>A new empty folder under the system's temporary folder.</summary>
    public static string New()
    {
        string path = Path.Combine(Path.GetTempPath(), "PdfNotes-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
