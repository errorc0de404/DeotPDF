using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace PdfNotes.Desktop.Tests;

/// <summary>
/// Review 2026-11-08 D7: the projects stack in one direction, Core &lt; Text &lt; Pdf, Text.Skia &lt; Render.Skia &lt; Editor &lt; ViewModels &lt; UI &lt; the
/// apps, and the screen draws PDFs only through the editor (AGENTS.md 구조).
/// </summary>
public sealed class LayerTests
{
    /// <summary>The layer of each project: a project references only projects of lower layers.</summary>
    private static readonly (Assembly Assembly, int Layer)[] Layers =
    [
        (typeof(PdfNotes.Core.Models.DocumentModel).Assembly, 0),
        (typeof(PdfNotes.Text.TextLayout).Assembly, 1),
        (typeof(PdfNotes.Pdf.Writing.EditablePdfWriter).Assembly, 2),
        (typeof(PdfNotes.Text.SkiaText).Assembly, 2),
        (typeof(PdfNotes.Render.Skia.SkiaAnnotationPainter).Assembly, 3),
        (typeof(PdfNotes.TextEditor.TextEditorModel).Assembly, 2),
        (typeof(PdfNotes.Editor.Services.AppSettings).Assembly, 4),
        (typeof(PdfNotes.Cli.CliLive).Assembly, 5), // deot's commands: the desktop app runs them for AI clients (MCP_LIVE_PLAN)
        (typeof(PdfNotes.ViewModels.MainViewModel).Assembly, 5),
        (typeof(PdfNotes.TextEditor.Avalonia.TextBoxEditor).Assembly, 5),
        (typeof(PdfNotes.UI.Views.MainView).Assembly, 6),
        (typeof(MainWindow).Assembly, 7),
    ];

    [Fact]
    public void LayersOnlyReferenceDownward()
    {
        var layerOf = Layers.ToDictionary(l => l.Assembly.GetName().Name!, l => l.Layer);
        foreach (var (assembly, layer) in Layers)
            foreach (var reference in assembly.GetReferencedAssemblies().Select(r => r.Name!).Where(n => n.StartsWith("PdfNotes.", StringComparison.Ordinal)))
            {
                Assert.True(layerOf.TryGetValue(reference, out int other), $"{assembly.GetName().Name} references {reference}, which has no layer here");
                Assert.True(other < layer, $"{assembly.GetName().Name} (layer {layer}) references {reference} (layer {other})");
            }
    }

    /// <summary>The import and merge windows draw through the editor's PdfPreview; nothing in the screen opens the engine.</summary>
    [Fact]
    public void TheUiDoesNotUseThePdfEngine()
    {
        string file = typeof(PdfNotes.UI.Views.MainView).Assembly.Location;
        using var stream = File.OpenRead(file);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var used = reader.TypeReferences.Select(h => reader.GetTypeReference(h))
            .Select(t => $"{reader.GetString(t.Namespace)}.{reader.GetString(t.Name)}").ToHashSet();
        Assert.DoesNotContain("PdfNotes.Pdf.Engine.PdfiumDocument", used);
        Assert.Contains("PdfNotes.Pdf.Engine.RenderedPage", used); // the check reads the references at all
    }
}
