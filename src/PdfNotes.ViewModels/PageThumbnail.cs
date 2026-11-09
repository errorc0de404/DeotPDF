using System.Collections.ObjectModel;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.ViewModels;

/// <summary>A drawn thumbnail: the platform image, its size in DIPs, and whether every text box was laid out.</summary>
public sealed record PageThumbnail(object Image, double Width, double Height, bool Complete);
