using System.Collections.ObjectModel;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.ViewModels;

/// <summary>Draws page thumbnails (the PDF background and the app's notes) at the screen's pixels (P1 V5).</summary>
public interface IPageThumbnailSource
{
    /// <summary>
    /// The thumbnail of <paramref name="page"/>, <paramref name="width"/> DIPs wide, without notes when
    /// <paramref name="notesHidden"/>; null when cancelled. An unrenderable PDF page gives a white thumbnail.
    /// </summary>
    Task<PageThumbnail?> RenderAsync(OpenDocument document, DocumentModel model, Page page, double width, bool notesHidden, CancellationToken cancel);
}
