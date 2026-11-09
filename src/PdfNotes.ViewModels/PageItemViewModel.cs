using System.Collections.ObjectModel;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.ViewModels;

/// <summary>One row of the page panel: number, thumbnail, whether it is selected and whether it is the active page (spec 11.1, 11.5).</summary>
public sealed class PageItemViewModel(Guid pageId, Action<PageItemViewModel>? selectionChanged = null) : ObservableObject
{
    private int number;
    private bool active, selected;
    private object? image;
    private double width = PagesViewModel.ThumbnailWidth, height = PagesViewModel.ThumbnailWidth * 1.414;

    public Guid PageId { get; } = pageId;
    public int Number { get => number; set => SetProperty(ref number, value); }
    public bool IsActive { get => active; set => SetProperty(ref active, value); }

    /// <summary>Selected in the list (the list's selection is this, R4e): a click, Ctrl/Shift, or a command that made pages.</summary>
    public bool IsSelected
    {
        get => selected;
        set { if (SetProperty(ref selected, value)) selectionChanged?.Invoke(this); }
    }

    /// <summary>The thumbnail, a platform image made by <see cref="IPageThumbnailSource"/> (null until drawn).</summary>
    public object? Image { get => image; set => SetProperty(ref image, value); }

    public double ImageWidth { get => width; set => SetProperty(ref width, value); }
    public double ImageHeight { get => height; set => SetProperty(ref height, value); }

    /// <summary>The model page the image shows (reference); a changed page gets a new image. Null: draw again.</summary>
    public Page? Rendered { get; set; }
}
