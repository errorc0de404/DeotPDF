using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Reading;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Pdf.Writing;

/// <summary>Reject unsupported foreign appearances before any page in an atomic UI command is changed.</summary>
public static class PagePlacementValidation
{
    public static void Validate(PdfSourceDocument source, IEnumerable<PageSetup> targets) =>
        Validate(new DocumentSources(source, ownsMain: false), targets);

    public static void Validate(DocumentSources sources, IEnumerable<PageSetup> targets)
    {
        var opened = new Dictionary<byte[], PdfDocument>(ReferenceEqualityComparer.Instance);
        try
        {
            foreach (int index in targets.Where(p => p.Source.Kind == PageSourceKind.Pdf).Select(p => p.Source.PdfPageIndex!.Value).Distinct())
            {
                var (_, bytes, pages, local) = sources.Locate(index);
                if (!opened.TryGetValue(bytes, out var doc)) opened[bytes] = doc = PdfReader.Open(new MemoryStream(bytes, false), PdfDocumentOpenMode.Import);
                Validate(pages[local].Box, doc.Pages[local]);
            }
        }
        finally
        {
            foreach (var doc in opened.Values) doc.Dispose();
        }
    }

    private static void Validate(PdfPageBox clip, PdfPage page)
    {
        var annots = page.Elements.GetArray("/Annots"); if (annots is null) return;
        foreach (var item in annots.Elements)
        {
            if (EditablePdfReader.Resolve(item) is not { } annotation) continue;
            var rect = annotation.Elements.GetRectangle("/Rect");
            if (rect.X2 <= clip.Left || rect.X1 >= clip.Right || rect.Y2 <= clip.Bottom || rect.Y1 >= clip.Top ||
                rect.X1 >= clip.Left && rect.X2 <= clip.Right && rect.Y1 >= clip.Bottom && rect.Y2 <= clip.Top) continue;
            var form = annotation.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
            if (form?.Stream is null) form = form?.Elements.GetDictionary(annotation.Elements.GetName("/AS"));
            if (form?.Stream is null) throw new NotSupportedException("pageTransform.foreignNoAppearance");
        }
    }
}
