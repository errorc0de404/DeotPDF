using PdfNotes.Pdf.Writing;

namespace PdfNotes.Tests.Pdf;

/// <summary>
/// macOS and Linux replace a saved file by renaming a temporary file over it: the saved file must keep the mode of the
/// one it replaces, and a symbolic link must still point at the saved file. Windows skips these (the CLI tests run them
/// on Linux and macOS in CI).
/// </summary>
public sealed class SafeFileSaverUnixTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("pdfnotes-saver-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void SavingKeepsTheFileModeOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        string path = Path.Combine(folder, "a.pdf");
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        SafeFileSaver.Save(path, [4, 5, 6], _ => { });
        Assert.Equal([4, 5, 6], File.ReadAllBytes(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void SavingThroughASymbolicLinkReplacesTheFileItPointsAt()
    {
        if (OperatingSystem.IsWindows()) return;
        string real = Path.Combine(folder, "real.pdf"), link = Path.Combine(folder, "link.pdf");
        File.WriteAllBytes(real, [1, 2, 3]);
        File.CreateSymbolicLink(link, real);
        SafeFileSaver.Save(link, [4, 5, 6], _ => { });
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal([4, 5, 6], File.ReadAllBytes(real));
    }
}
