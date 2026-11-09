using PdfNotes.Pdf.Engine;

namespace PdfNotes.Editor.Services;

/// <summary>
/// A PDF shown page by page in a window (the thumbnails and preview of the import and merge windows), as saved with its
/// annotations; not a document being edited. The windows draw through this, not the PDF engine.
/// </summary>
public sealed class PdfPreview(PdfiumDocument engine) : IDisposable
{
    public int PageCount => engine.PageCount;

    /// <summary>
    /// Page <paramref name="index"/> fitted into <paramref name="width"/> × <paramref name="height"/> points (as turned on
    /// screen) at <paramref name="scale"/> pixels per point; null when the file closed or the page cannot be drawn.
    /// </summary>
    public RenderedPage? Fit(int index, double width, double height, double scale, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var box = engine.GetPageBox(index);
            double w = box.Right - box.Left, h = box.Top - box.Bottom;
            if (box.Rotation is 90 or 270) (w, h) = (h, w);
            double perPoint = Math.Min(width / Math.Max(w, 1), height / Math.Max(h, 1)) * scale;
            return engine.Render(index, perPoint, annotations: true, cancellationToken);
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidDataException or ArgumentOutOfRangeException or PdfiumOpenException) { return null; }
    }

    public void Dispose() => engine.Dispose();
}
