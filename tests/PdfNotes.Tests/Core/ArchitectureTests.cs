using PdfNotes.Core.Models;

namespace PdfNotes.Tests.Core;

public class ArchitectureTests
{
    [Fact]
    public void CoreReferencesNeitherWpfNorPdfEngines()
    {
        var references = typeof(DocumentModel).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        string[] forbidden = ["PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml", "PdfSharp", "PdfNotes.Pdf", "DeotPDF"];
        Assert.DoesNotContain(references, name => forbidden.Any(f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
        // Clipper2 (polygon arithmetic for the D08 eraser/lasso) is the only allowed non-framework dependency.
        string[] allowed = ["netstandard", "Clipper2Lib"];
        Assert.All(references, name => Assert.True(name.StartsWith("System", StringComparison.Ordinal) || allowed.Contains(name), name));
    }

    /// <summary>D14a/D14b/D14f: the shared projects are plain net10.0 — no WPF, no Windows-only target or assembly, no App.</summary>
    [Theory]
    [InlineData(typeof(DocumentModel))]
    [InlineData(typeof(PdfNotes.Text.TextLayout))]
    [InlineData(typeof(PdfNotes.Pdf.Writing.EditablePdfWriter))]
    [InlineData(typeof(PdfNotes.Editor.Services.AppSettings))]
    [InlineData(typeof(PdfNotes.ViewModels.MainViewModel))]
    [InlineData(typeof(PdfNotes.TextEditor.TextEditorModel))]
    [InlineData(typeof(PdfNotes.Text.SkiaText))]
    public void SharedProjectsAreNet10WithoutWpf(Type marker)
    {
        var assembly = marker.Assembly;
        // Microsoft.Win32.Primitives is cross-platform (Win32Exception is what Process throws everywhere); Registry and SystemEvents are not.
        string[] forbidden = ["PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml", "System.Windows", "System.Drawing.Common",
            "Microsoft.Win32.Registry", "Microsoft.Win32.SystemEvents", "DeotPDF", "PdfNotes.Text.Wpf"];
        var references = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.DoesNotContain(references, name => forbidden.Any(f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
        var target = assembly.GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false).Cast<System.Runtime.Versioning.TargetFrameworkAttribute>().Single();
        Assert.Equal(".NETCoreApp,Version=v10.0", target.FrameworkName);
        Assert.Empty(assembly.GetCustomAttributes(typeof(System.Runtime.Versioning.SupportedOSPlatformAttribute), false));
    }

    /// <summary>D14f: no Win32 calls in the shared projects — the only native library is PDFium, loaded by the Pdf layer.</summary>
    [Theory]
    [InlineData(typeof(DocumentModel), null)]
    [InlineData(typeof(PdfNotes.Text.TextLayout), null)]
    [InlineData(typeof(PdfNotes.Pdf.Writing.EditablePdfWriter), "pdfium")]
    [InlineData(typeof(PdfNotes.Editor.Services.AppSettings), null)]
    [InlineData(typeof(PdfNotes.ViewModels.MainViewModel), null)]
    [InlineData(typeof(PdfNotes.TextEditor.TextEditorModel), null)]
    public void SharedProjectsCallNoNativeCodeExceptPdfium(Type marker, string? allowed)
    {
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
        var imports = marker.Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(all))
            .Where(m => m.Attributes.HasFlag(System.Reflection.MethodAttributes.PinvokeImpl))
            .Select(m => (Method: $"{m.DeclaringType!.FullName}.{m.Name}", Library: m.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), false)
                .Cast<System.Runtime.InteropServices.DllImportAttribute>().Single().Value))
            .ToArray();
        Assert.All(imports, i => Assert.True(i.Library == allowed, $"{i.Method} imports {i.Library}"));
        if (allowed is not null)
            Assert.NotEmpty(imports); // the check would pass vacuously if reflection stopped seeing the imports
    }

    /// <summary>P2: the editor model takes no packages — only Core and Text (roadmap P2, new projects).</summary>
    [Fact]
    public void TextEditorReferencesOnlyCoreAndText()
    {
        var references = typeof(PdfNotes.TextEditor.TextEditorModel).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        string[] allowed = ["netstandard", "PdfNotes.Core", "PdfNotes.Text"];
        Assert.All(references, name => Assert.True(name.StartsWith("System", StringComparison.Ordinal) || allowed.Contains(name), name));
    }

    /// <summary>The painters draw plain data (model, marks, text layout): they do not depend on the editor's tools or sessions.</summary>
    [Fact]
    public void PaintersDoNotReferenceTheEditor()
    {
        var references = typeof(PdfNotes.Render.Skia.SkiaMarkPainter).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.DoesNotContain("PdfNotes.Editor", references);
        Assert.DoesNotContain("PdfNotes.Pdf", references);
    }

    /// <summary>
    /// The projects without a platform (model, PDF, text layout, editor services, view models, editor model) take no
    /// drawing or UI library, so the desktop app, the CLI and a later Android app (P5) choose their own. The Skia engine,
    /// the Avalonia editor control, the painters and the apps are the only ones that do.
    /// </summary>
    [Theory]
    [InlineData(typeof(PdfNotes.ViewModels.MainViewModel))]
    [InlineData(typeof(PdfNotes.Editor.Services.AppSettings))]
    [InlineData(typeof(PdfNotes.Pdf.Writing.EditablePdfWriter))]
    [InlineData(typeof(PdfNotes.Text.TextLayout))]
    [InlineData(typeof(PdfNotes.TextEditor.TextEditorModel))]
    public void PlatformFreeProjectsTakeNoUiOrDrawingLibrary(Type marker)
    {
        string[] forbidden = ["PdfNotes.Text.Skia", "PdfNotes.TextEditor.Avalonia", "PdfNotes.Render", "Avalonia", "SkiaSharp", "HarfBuzzSharp"];
        var references = marker.Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.DoesNotContain(references, name => forbidden.Any(f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
    }
}
