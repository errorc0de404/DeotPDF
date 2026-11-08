using System.IO;
using PdfNotes.Pdf.Writing;

namespace PdfNotes.Tests.App;

/// <summary>
/// A save that cannot write a temporary file and rename (an Android content URI, P5b) overwrites the document in place:
/// whatever fails on the way — the write itself, the read back, the check — the old content is put back.
/// </summary>
public sealed class RestoringWriteTests : IDisposable
{
    private readonly string folder = TestFolders.New();
    private static readonly byte[] Old = [1, 2, 3, 4, 5, 6];
    private static readonly byte[] New = [9, 9, 9, 9, 9, 9, 9, 9];

    public void Dispose() => Directory.Delete(folder, true);

    private string Keep => Path.Combine(folder, "saving", "keep.pdf");

    [Fact]
    public void AWriteThatFailsHalfwayPutsTheOldContentBack()
    {
        byte[] store = Old;
        int writes = 0;
        void Write(byte[] content)
        {
            if (writes++ == 0) { store = content[..3]; throw new IOException("provider went away"); } // half written
            store = content;
        }
        var e = Assert.Throws<IOException>(() => RestoringWrite.Run(New, Old, Write, () => store, _ => { }, Keep));
        Assert.Equal("provider went away", e.Message);
        Assert.Equal(Old, store);
        Assert.False(File.Exists(Keep));
    }

    /// <summary>
    /// Final review of 2026-11-08 (I1): the failed write's stream is closed before the old content goes back, so a
    /// provider that commits on close cannot put the half-written content over the restored one.
    /// </summary>
    [Fact]
    public void TheOldContentGoesBackAfterTheFailedWriteClosedItsStream()
    {
        byte[] store = Old;
        int open = 0, writes = 0;
        var openWhileWriting = new List<int>();
        void Write(byte[] content)
        {
            openWhileWriting.Add(open);
            open++; // a stream opened with "wt", closed by its using block
            try
            {
                if (writes++ == 0) throw new IOException("provider went away");
                store = content;
            }
            finally { open--; }
        }
        Assert.Throws<IOException>(() => RestoringWrite.Run(New, Old, Write, () => store, _ => { }, Keep));
        Assert.Equal([0, 0], openWhileWriting);
        Assert.Equal(Old, store);
    }

    [Fact]
    public void AFailedRestoreKeepsTheCopy()
    {
        byte[] store = Old;
        void Write(byte[] content) { store = []; throw new IOException("no space"); }
        var e = Assert.Throws<IOException>(() => RestoringWrite.Run(New, Old, Write, () => store, _ => { }, Keep));
        Assert.Equal("no space", e.Message);
        Assert.Equal(Old, File.ReadAllBytes(Keep)); // the only copy of the document left
    }

    [Fact]
    public void WhatDoesNotReadBackIsReplacedByTheOldContent()
    {
        byte[] store = Old;
        void Write(byte[] content) => store = content == New ? content[..4] : content; // the provider drops the end
        void Verify(byte[] content) { if (content.Length != New.Length) throw new InvalidDataException("truncated"); }
        var e = Assert.Throws<SaveFailedException>(() => RestoringWrite.Run(New, Old, Write, () => store, Verify, Keep));
        Assert.Equal(SaveFailure.VerificationFailed, e.Failure);
        Assert.Equal(Old, store);
        Assert.False(File.Exists(Keep));
    }

    [Fact]
    public void AVerifiedWriteDeletesTheCopy()
    {
        byte[] store = Old;
        RestoringWrite.Run(New, Old, c => store = c, () => store, _ => { }, Keep);
        Assert.Equal(New, store);
        Assert.False(File.Exists(Keep));
    }
}
