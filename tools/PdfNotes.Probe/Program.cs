using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using PdfNotes.App.Prototype;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Probe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        PdfNotes.Text.WpfText.Register(); // D14a: the WPF text engine behind TextLayout
        // D14e: render the UI with another language pack (the long-string test pack) instead of Korean.
        if (Environment.GetEnvironmentVariable("PDFNOTES_LANGUAGE_PACK") is { Length: > 0 } packPath)
            PdfNotes.Editor.Localization.Strings.Use(PdfNotes.Editor.Localization.LanguagePack.Parse(File.ReadAllText(packPath), packPath));
        try
        {
            if (args.Length == 2 && args[0] is "d10bc" or "d10bc-reopen") return D10bcCheck.Run(Path.GetFullPath(args[1]), args[0] == "d10bc-reopen");
            if (args.Length == 2 && args[0] is "d10d" or "d10d-reopen") return D10bcCheck.Run(Path.GetFullPath(args[1]), args[0] == "d10d-reopen", d10d: true);
            if (args.Length == 2 && args[0] == "d13b") return D13bCheck.Run(Path.GetFullPath(args[1]), args[0]);
            if (args.Length == 2 && args[0] is "d13h" or "d13h-logic") return D13hCheck.Run(Path.GetFullPath(args[1]), args[0]);
            if (args.Length == 2 && args[0] == "d13j-logic") return D13jCheck.Run(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "d13e") return D13eCheck.Run(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "d13i-logic") return D13iCheck.Run(Path.GetFullPath(args[1]), args[0]);
            if (args.Length == 2 && args[0] == "d13-real") return D13RealCheck.Run(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "d14a-text") return D14aTextCheck.Run(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "d14g-logic") return D14gCheck.Run(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "d13f") return D13fCheck.Run(Path.GetFullPath(args[1]));
            if (args.Length == 3 && args[0] == "d14f-resave") return D14fResaveCheck.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            if (args.Length == 2 && args[0] is "d13-render" or "d13-textbox" or "d13-display" or "d13-import" or "d13-followup") return D13Check.Run(Path.GetFullPath(args[1]), args[0]);
            if (args.Length == 2 && args[0] is "d12" or "d12-logic" or "d12-crash-child") return D12Check.Run(Path.GetFullPath(args[1]), args[0]);
            if (args.Length == 2 && args[0] is "d11a" or "d11b" or "d11c" or "d11d" or "d11e" or "d11e-reopen" or "d11f" or "d11f-reopen")
            {
                Directory.CreateDirectory(Path.GetFullPath(args[1]));
                return D11Check.Run(Path.GetFullPath(args[1]), args[0]);
            }
            if (args.Length != 2 || args[0] is not ("create" or "reopen" or "measure" or "ui" or "d03" or "d04" or "d04-reopen" or "d04-pen" or "d05" or "d05-reopen" or "d05-keys" or "d05-pan" or "d06" or "d06-reopen" or "d06-caret" or "d06-paste" or "d06-create" or "d06-fixes" or "d06-fixes-logic" or "d04-dot" or "d07" or "d07-logic" or "d07-reopen" or "d08" or "d08-logic" or "d08-reopen" or "d09" or "d09-logic" or "d09-reopen" or "d10" or "d10-logic" or "d10-reopen"))
                throw new ArgumentException("Usage: PdfNotes.Probe <create|reopen|measure|ui|d03|d04> <output directory>");
            string folder = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(folder);
            if (args[0] == "create") Create(folder);
            else if (args[0] == "reopen") Reopen(folder);
            else if (args[0] == "measure") Measure(folder);
            else if (args[0] == "d04-dot") return D04Check.Run(folder, false, dotOnly: true);
            else if (args[0] == "d07") return D07Check.Run(folder, reopen: false, logicOnly: false);
            else if (args[0] == "d08") return D08Check.Run(folder, reopen: false, logicOnly: false);
            else if (args[0] == "d09") return D09Check.Run(folder, reopen: false, logicOnly: false);
            else if (args[0] == "d10") return D10Check.Run(folder, reopen: false, logicOnly: false);
            else if (args[0] == "d10-logic") return D10Check.Run(folder, reopen: false, logicOnly: true);
            else if (args[0] == "d10-reopen") return D10Check.Run(folder, reopen: true, logicOnly: false);
            else if (args[0] == "d09-logic") return D09Check.Run(folder, reopen: false, logicOnly: true);
            else if (args[0] == "d09-reopen") return D09Check.Run(folder, reopen: true, logicOnly: false);
            else if (args[0] == "d08-logic") return D08Check.Run(folder, reopen: false, logicOnly: true);
            else if (args[0] == "d08-reopen") return D08Check.Run(folder, reopen: true, logicOnly: false);
            else if (args[0] == "d07-logic") return D07Check.Run(folder, reopen: false, logicOnly: true);
            else if (args[0] == "d07-reopen") return D07Check.Run(folder, reopen: true, logicOnly: false);
            else if (args[0] == "d06-create") return D06Check.Run(folder, false, createOnly: true);
            else if (args[0] == "d06-fixes-logic") return D06Check.Run(folder, false, fixesOnly: true, logicOnly: true);
            else if (args[0] == "d06-fixes") return D06Check.Run(folder, false, fixesOnly: true);
            else if (args[0] == "d06-paste") return D06Check.Run(folder, false, pasteOnly: true);
            else if (args[0] == "d06-caret") return D06Check.Run(folder, false, caretOnly: true);
            else if (args[0] == "d06") return D06Check.Run(folder, false);
            else if (args[0] == "d06-reopen") return D06Check.Run(folder, true);
            else if (args[0] == "d03") return D03Check.Run(folder);
            else if (args[0] == "d04") return D04Check.Run(folder, reopenPhase: false);
            else if (args[0] == "d04-reopen") return D04Check.Run(folder, reopenPhase: true);
            else if (args[0] == "d04-pen") return D04Check.Run(folder, false, penOnly: true);
            else if (args[0] == "d05") return D05Check.Run(folder, false);
            else if (args[0] == "d05-reopen") return D05Check.Run(folder, true);
            else if (args[0] == "d05-keys") return D05Check.Run(folder, false, keysOnly: true);
            else if (args[0] == "d05-pan") return D05Check.Run(folder, false, panOnly: true);
            else Ui(folder);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Create(string folder)
    {
        var face = new Typeface("Malgun Gothic");
        if (!face.TryGetGlyphTypeface(out var glyph)) throw new InvalidOperationException("Sample requires Malgun Gothic.");
        if (glyph.EmbeddingRights.ToString().Contains("Restricted",StringComparison.Ordinal) ||
            glyph.EmbeddingRights.ToString().Contains("BitmapsOnly",StringComparison.Ordinal) ||
            glyph.EmbeddingRights.ToString().Contains("NoSubsetting",StringComparison.Ordinal))
            throw new InvalidOperationException("Sample font does not allow embedding.");
        var source = Path.Combine(folder,"source.pdf");
        ProbeSamplePdf.Create(source);
        var model = ProbeFactory.Create(source);
        string target = Path.Combine(folder,"editable.pdf");
        ProbePdfStore.SaveEditable(source,model,target);
        File.WriteAllText(Path.Combine(folder,"expected-model.json"),JsonSerializer.Serialize(model,ProbePdfStore.JsonOptions));
        RenderAll(target,folder,"editable");
        File.WriteAllText(Path.Combine(folder,"font-check.json"),JsonSerializer.Serialize(new
        {
            Font="Malgun Gothic", Rights=glyph.EmbeddingRights.ToString(), OutlineTolerancePt=.02,
            Appearance="WPF outlines, no font binary embedded in app annotations",
            Source="PDFsharp Unicode subset embedded by sample generator"
        }));
        Console.WriteLine($"Created editable proof: {target}; {model.Pages.Sum(p=>p.Annotations.Length)} annotations.");
    }

    private static void Reopen(string folder)
    {
        string editable = Path.Combine(folder,"editable.pdf");
        var reopened = ProbePdfStore.Open(editable);
        if (reopened.Model is null || reopened.Warnings.Length != 0) throw new InvalidDataException("Own annotations did not restore cleanly.");
        string expected = File.ReadAllText(Path.Combine(folder,"expected-model.json"));
        if (JsonSerializer.Serialize(reopened.Model,ProbePdfStore.JsonOptions) != expected) throw new InvalidDataException("Restart round-trip changed model values/IDs.");
        var edited = ProbeFactory.Edit(reopened.Model);
        string changed = Path.Combine(folder,"edited.pdf");
        ProbePdfStore.SaveEditable(editable,edited,changed);
        var again = ProbePdfStore.Open(changed);
        if (JsonSerializer.Serialize(again.Model,ProbePdfStore.JsonOptions) != JsonSerializer.Serialize(edited,ProbePdfStore.JsonOptions))
            throw new InvalidDataException("Edited values did not survive save.");
        string flat = Path.Combine(folder,"flattened.pdf");
        ProbePdfStore.ExportFlattened(changed,edited,flat);
        if (ProbePdfStore.Open(flat).Model is not null) throw new InvalidDataException("Flatten kept an editable model.");
        using var full = new PdfiumDocument(changed);
        using var flattened = new PdfiumDocument(flat);
        using var background = new PdfiumDocument(again.BackgroundPdf);
        var differences = new List<object>();
        var metrics=ProbePdfStore.GetMetrics(changed);
        for (int i=0;i<full.PageCount;i++)
        {
            var a = full.Render(i,userUnit:metrics[i].UserUnit); var b = flattened.Render(i,userUnit:metrics[i].UserUnit); var c = background.Render(i,userUnit:metrics[i].UserUnit);
            double mean = a.Bgra.Zip(b.Bgra,(x,y)=>Math.Abs(x-y)).Average();
            double hidden = a.Bgra.Zip(c.Bgra,(x,y)=>Math.Abs(x-y)).Average();
            if (mean > 1 || hidden < .05) throw new InvalidDataException($"Appearance/flatten/background comparison failed on page {i+1}.");
            string sourceText = string.Concat(flattened.ExtractText(i).Select(ch=>ch.Text));
            if (!sourceText.Contains("Original PDF") || !sourceText.Contains("한글")) throw new InvalidDataException("Flatten lost source text.");
            differences.Add(new {Page=i+1,MeanChannelDifferenceEditableToFlat=mean,MeanDifferenceAfterHideOwn=hidden});
            ProbeFactory.SavePng(c,Path.Combine(folder,$"background-{i+1}.png"));
            File.WriteAllText(Path.Combine(folder,$"text-quads-{i+1}.json"),JsonSerializer.Serialize(flattened.ExtractText(i)));
        }
        RenderAll(changed,folder,"edited"); RenderAll(flat,folder,"flattened");
        File.WriteAllText(Path.Combine(folder,"roundtrip-result.json"),JsonSerializer.Serialize(new
        {
            ReopenedInNewProcess=true, IdsAndValuesRestored=true, EditedAndResaved=true,
            NoEditableModelAfterFlatten=true, SourceKoreanAndEnglishTextPreserved=true, Differences=differences
        },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("Separate-process reopen / edit / save / flatten: passed.");
    }

    private static void RenderAll(string file,string folder,string prefix)
    {
        var metrics=ProbePdfStore.GetMetrics(file);
        using var doc = new PdfiumDocument(file);
        for(int i=0;i<doc.PageCount;i++) ProbeFactory.SavePng(doc.Render(i,userUnit:metrics[i].UserUnit),Path.Combine(folder,$"{prefix}-{i+1}.png"));
    }

    private static void Measure(string folder)
    {
        string path = Path.Combine(folder,"fifty-pages.pdf");
        ProbeSamplePdf.Create(path,50);
        var metrics=ProbePdfStore.GetMetrics(path);
        long before = GC.GetTotalMemory(true);
        var watch = Stopwatch.StartNew();
        var times = new List<double>();
        int maximumBytes=0;
        using(var doc = new PdfiumDocument(path))
        {
            for(int i=0;i<doc.PageCount;i++)
            {
                var start = Stopwatch.GetTimestamp();
                var render=doc.Render(i,.75,userUnit:metrics[i].UserUnit);
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                maximumBytes=Math.Max(maximumBytes,render.Bgra.Length);
                // Deliberately retain no page cache. Only the current result exists.
            }
        }
        watch.Stop();
        var process=Process.GetCurrentProcess();
        var ordered=times.Order().ToArray();
        File.WriteAllText(Path.Combine(folder,"measurements.json"),JsonSerializer.Serialize(new
        {
            Os=Environment.OSVersion.ToString(),Framework=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),LogicalProcessors=Environment.ProcessorCount,
            SamplePages=50,SampleBytes=new FileInfo(path).Length,PixelsPerPoint=.75,
            SequentialPageRenders=times.Count,FirstRenderMs=times[0],MedianRenderMs=ordered[25],P95RenderMs=ordered[47],TotalRenderLoopMs=watch.Elapsed.TotalMilliseconds,
            MaxOnePageBgraBytes=maximumBytes,RetainedPageCacheCount=0,ManagedBytesBefore=before,
            ManagedBytesAfterRelease=GC.GetTotalMemory(true),PrivateBytes=process.PrivateMemorySize64,
            NativePdfiumBytes=new FileInfo(Path.Combine(AppContext.BaseDirectory,"pdfium.dll")).Length,
            NotFinalD13Benchmark=true
        },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("50-page scoped sequential rendering / disposal measured.");
    }

    private static void Ui(string folder)
    {
        var application=new System.Windows.Application();
        var window=new PdfNotes.App.Prototype.ProbeWindow();
        window.Loaded += async (_,_) =>
        {
            try { await window.RunProbeUiCheck(folder); }
            catch(Exception e) { File.WriteAllText(Path.Combine(folder,"ui-error.txt"),e.ToString()); Environment.ExitCode=1; }
            finally { window.Close(); }
        };
        application.Run(window);
        if(Environment.ExitCode!=0) throw new InvalidOperationException("UI proof failed; see ui-error.txt.");
    }
}
