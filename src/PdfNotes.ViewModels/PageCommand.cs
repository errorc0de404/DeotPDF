using System.Collections.ObjectModel;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Editor.Localization;
using PdfNotes.Editor.Platform;
using PdfNotes.Editor.Services;
using PdfNotes.Pdf.Reading;

namespace PdfNotes.ViewModels;

/// <summary>The commands of the page panel's context menu and the 페이지 tab (spec 11).</summary>
public enum PageCommand { InsertBlank, CopyWithNotes, CopyPageOnly, Paste, Delete, ClearNotes, Export, RotateRight, RotateLeft, Shrink, Resize }
