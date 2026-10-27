using System.IO;
using System.Text.Json.Nodes;

namespace PdfNotes.Cli;

/// <summary>
/// Every file a command reads or writes is resolved here. On the command line any path is allowed and relative paths
/// mean the current folder. <c>deot mcp</c> sets <see cref="Roots"/>: then relative paths mean the first root and a path
/// outside all roots is <c>PATH_NOT_ALLOWED</c>.
/// </summary>
public static class CliPaths
{
    private static readonly AsyncLocal<IReadOnlyList<string>?> roots = new();

    /// <summary>
    /// Allowed folders (full paths); null allows every path. It holds for the caller's flow only (an async local, carried
    /// to the STA thread <see cref="CliApp.Run"/> starts), so commands run elsewhere at the same time are not limited.
    /// </summary>
    public static IReadOnlyList<string>? Roots { get => roots.Value; set => roots.Value = value; }

    /// <summary>The full path of <paramref name="path"/>, refused when it is outside <see cref="Roots"/>.</summary>
    public static string Full(string path)
    {
        if (Roots is not { } roots) return Path.GetFullPath(path);
        string full = roots.Count > 0 ? Path.GetFullPath(path, roots[0]) : Path.GetFullPath(path);
        if (!roots.Any(root => Inside(full, root)))
            throw new CliException("PATH_NOT_ALLOWED", $"{full} is outside the folders this server may use ({string.Join(", ", roots)}).",
                ExitCodes.InvalidInput, new JsonObject { ["path"] = full });
        return full;
    }

    public static bool Inside(string full, string root)
    {
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(full, folder, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
