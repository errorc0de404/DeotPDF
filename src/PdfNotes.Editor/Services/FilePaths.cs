namespace PdfNotes.Editor.Services;

/// <summary>
/// How file paths compare (roadmap P4 A6): Windows and macOS file systems (NTFS, APFS by default) ignore case, Linux
/// file systems do not. Paths that name the same file the user sees — the open document, a backup group, the recent
/// list, "already open in another window" — compare with this, not with a fixed rule.
/// </summary>
public static class FilePaths
{
    public static StringComparison Comparison { get; } = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static StringComparer Comparer { get; } = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static bool Same(string? a, string? b) => string.Equals(a, b, Comparison);
}
