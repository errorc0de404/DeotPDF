using System.Collections.ObjectModel;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.ViewModels;

/// <summary>A context menu row; <see cref="Text"/> null is a separator.</summary>
public sealed record PageMenuEntry(PageCommand Command, string? Text, bool Enabled = true)
{
    public static PageMenuEntry Separator { get; } = new(default, null);
}
