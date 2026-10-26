using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using PdfNotes.App.Prototype;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Tests.Pdf;

public class ProbePdfTests
{
    private static void Sta(Action action)
    {
        Exception? failure=null;
        var thread=new Thread(()=> {try{action();}catch(Exception e){failure=e;}}){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)),"PDF probe timed out.");
        if(failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Sample(Action<string,string,ProbeModel> action) => Sta(()=>
    {
        string folder=Path.Combine(Path.GetTempPath(),"PdfNotes-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source=Path.Combine(folder,"source.pdf");
            ProbeSamplePdf.Create(source);
            action(folder,source,ProbeFactory.Create(source));
        }
        finally { Directory.Delete(folder,true); }
    });

    [Fact]
    public void EditableRoundTripPreservesIdentityStylesPngAndCurvesWithoutHidingForeignAnnotation() => Sample((folder,source,model)=>
    {
        string target=Path.Combine(folder,"notes.pdf");
        ProbePdfStore.SaveEditable(source,model,target);
        var opened=ProbePdfStore.Open(target);
        Assert.Empty(opened.Warnings);
        Assert.Equal(JsonSerializer.Serialize(model),JsonSerializer.Serialize(opened.Model));
        using var background=new MemoryStream(opened.BackgroundPdf);
        using var pdf=PdfReader.Open(background,PdfDocumentOpenMode.Modify);
        foreach(PdfPage page in pdf.Pages)
        {
            var annots=page.Elements.GetArray("/Annots");
            Assert.NotNull(annots); Assert.Single(annots.Elements);
            Assert.Equal("Foreign-D01",((PdfDictionary)((PdfReference)annots.Elements[0]).Value).Elements.GetString("/NM"));
        }
        using var reader=new PdfiumDocument(target);
        Assert.Contains("한글",string.Concat(reader.ExtractText(0).Select(c=>c.Text)));
        var changed=ProbeFactory.Edit(opened.Model!);
        ProbePdfStore.SaveEditable(target,changed,target);
        var reopened=ProbePdfStore.Open(target);
        Assert.Equal(JsonSerializer.Serialize(changed),JsonSerializer.Serialize(reopened.Model));
        Assert.Equal(new ProbeColor(10,150,85),reopened.Model!.Pages[0].Annotations.Single(a=>a.Type=="ink").Paths[0].Color);
    });

    [Fact]
    public void FlattenRetainsOriginalTextAndVectorContentRemovesAllPrivateStreamsAndLooksTheSame() => Sample((folder,source,model)=>
    {
        string editable=Path.Combine(folder,"notes.pdf"),flat=Path.Combine(folder,"flat.pdf");
        ProbePdfStore.SaveEditable(source,model,editable);
        ProbePdfStore.ExportFlattened(editable,model,flat);
        Assert.Null(ProbePdfStore.Open(flat).Model);
        using var original=PdfReader.Open(source,PdfDocumentOpenMode.Modify);
        using var result=PdfReader.Open(flat,PdfDocumentOpenMode.Modify);
        for(int i=0;i<result.PageCount;i++)
        {
            var originalCode=original.Pages[i].Contents.CreateSingleContent().Stream.UnfilteredValue;
            var flattenedCode=result.Pages[i].Contents.CreateSingleContent().Stream.UnfilteredValue;
            Assert.Contains(Encoding.ASCII.GetString(originalCode),Encoding.ASCII.GetString(flattenedCode));
            Assert.Contains("BT",Encoding.ASCII.GetString(flattenedCode));
            Assert.Contains(" re",Encoding.ASCII.GetString(flattenedCode));
        }
        foreach(var dictionary in result.Internals.GetAllObjects().OfType<PdfDictionary>())
        {
            Assert.False(dictionary.Elements.ContainsKey("/PdfNotesData"));
            if(dictionary.Stream is not null) Assert.DoesNotContain("PdfNotes.D01",Encoding.UTF8.GetString(dictionary.Stream.UnfilteredValue));
        }
        using var a=new PdfiumDocument(editable); using var b=new PdfiumDocument(flat);
        var metrics=ProbePdfStore.GetMetrics(flat);
        for(int i=0;i<3;i++)
        {
            var x=a.Render(i,userUnit:metrics[i].UserUnit); var y=b.Render(i,userUnit:metrics[i].UserUnit);
            Assert.Equal(x.Width,y.Width); Assert.Equal(x.Height,y.Height);
            Assert.True(x.Bgra.Zip(y.Bgra,(p,q)=>Math.Abs(p-q)).Average()<.05);
            Assert.Contains("Original PDF",string.Concat(b.ExtractText(i).Select(c=>c.Text)));
        }
    });

    [Fact]
    public void CorruptAnnotationMetadataIsNotHiddenAndUnsupportedHeaderRemainsReadOnly() => Sample((folder,source,model)=>
    {
        string editable=Path.Combine(folder,"notes.pdf"),corrupt=Path.Combine(folder,"corrupt.pdf"),unknown=Path.Combine(folder,"unknown.pdf");
        ProbePdfStore.SaveEditable(source,model,editable);
        using(var doc=PdfReader.Open(editable,PdfDocumentOpenMode.Modify))
        {
            var annotations=doc.Pages[0].Elements.GetArray("/Annots")!;
            var annotation=(PdfDictionary)((PdfReference)annotations.Elements[1]).Value;
            annotation.Elements.GetDictionary("/PdfNotesData")!.Stream.Value=Encoding.UTF8.GetBytes("{broken JSON");
            doc.Save(corrupt);
        }
        var opened=ProbePdfStore.Open(corrupt);
        Assert.Single(opened.Warnings);
        Assert.Equal(model.Pages[0].Annotations.Length-1,opened.Model!.Pages[0].Annotations.Length);
        using(var bg=new MemoryStream(opened.BackgroundPdf))
        using(var doc=PdfReader.Open(bg,PdfDocumentOpenMode.Modify)) Assert.Equal(2,doc.Pages[0].Elements.GetArray("/Annots")!.Elements.Count);
        using(var doc=PdfReader.Open(editable,PdfDocumentOpenMode.Modify))
        {
            doc.Internals.Catalog.Elements.GetDictionary("/PdfNotes")!.Stream.Value=Encoding.UTF8.GetBytes("{\"App\":\"PdfNotes.D01\",\"Version\":999}");
            doc.Save(unknown);
        }
        var unsupported=ProbePdfStore.Open(unknown);
        Assert.Null(unsupported.Model); Assert.Single(unsupported.Warnings);
        using var unknownRenderer=new PdfiumDocument(unsupported.BackgroundPdf);
        using var expectedRenderer=new PdfiumDocument(editable);
        Assert.Equal(expectedRenderer.Render(0).Bgra,unknownRenderer.Render(0).Bgra);
    });

    [Fact]
    public void RotationCropOffsetAndUserUnitKeepReconstructedOverlayAligned() => Sample((folder,source,model)=>
    {
        string editable=Path.Combine(folder,"notes.pdf");
        ProbePdfStore.SaveEditable(source,model,editable);
        var opened=ProbePdfStore.Open(editable);
        var metrics=ProbePdfStore.GetMetrics(editable);
        using var background=new PdfiumDocument(opened.BackgroundPdf);
        using var saved=new PdfiumDocument(editable);
        for(int i=0;i<3;i++)
        {
            var actual=ProbePreview.Compose(background,i,metrics[i],opened.Model!.Pages[i]);
            var expected=saved.Render(i,userUnit:metrics[i].UserUnit);
            Assert.Equal(expected.Width,actual.Width); Assert.Equal(expected.Height,actual.Height);
            // Different rasterizers may disagree on antialiased edges, but geometry must align.
            Assert.True(actual.Bgra.Zip(expected.Bgra,(a,b)=>Math.Abs(a-b)).Average()<.7);
            var ink=model.Pages[i].Annotations.Single(a=>a.Type=="ink");
            var pt=ink.Paths[0].Segments[0].Points[0];
            var pixel=saved.PageToPixel(i,new(metrics[i].CropLeft+pt.X/metrics[i].UserUnit,
                metrics[i].CropTop-pt.Y/metrics[i].UserUnit),actual.Width,actual.Height);
            int offset=((int)pixel.Y*actual.Stride)+(int)pixel.X*4;
            Assert.True(expected.Bgra[offset+2]>expected.Bgra[offset+1]); // red stroke starts at the canonical point
        }
        Assert.Equal(1380,saved.Render(1,userUnit:metrics[1].UserUnit).Width);
        Assert.Equal(1100,saved.Render(1,userUnit:metrics[1].UserUnit).Height);
    });

    [Fact]
    public void RepeatedSavingDoesNotDuplicateAnnotationsOrGrowTheFileWithoutBound() => Sample((folder,source,model)=>
    {
        string path=Path.Combine(folder,"notes.pdf");
        ProbePdfStore.SaveEditable(source,model,path);
        long initial=new FileInfo(path).Length;
        for(int i=0;i<5;i++) ProbePdfStore.SaveEditable(path,model,path);
        using var doc=PdfReader.Open(path,PdfDocumentOpenMode.Modify);
        Assert.Equal(model.Pages[0].Annotations.Length+1,doc.Pages[0].Elements.GetArray("/Annots")!.Elements.Count);
        Assert.InRange(new FileInfo(path).Length,initial-2048,initial+2048);
    });

    [Fact]
    public void FailedReplacementLeavesExistingBytesAndRemovesTemporaryFile() => Sample((folder,source,model)=>
    {
        string path=Path.Combine(folder,"locked.pdf");
        ProbePdfStore.SaveEditable(source,model,path);
        var expected=File.ReadAllBytes(path);
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            Assert.Throws<IOException>(()=>ProbePdfStore.SaveEditable(source,model,path));
        Assert.Equal(expected,File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(folder,"*.tmp"));
    });

    [Fact]
    public void NativeHandlesCloseAndCancelledOrOversizedRenderDoesNotPoisonTheNextRequest() => Sample((_,source,_)=>
    {
        for(int i=0;i<10;i++)
        {
            using var document=new PdfiumDocument(source);
            using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(()=>document.Render(0,cancellationToken:cancellation.Token));
            Assert.Throws<ArgumentOutOfRangeException>(()=>document.Render(0,100));
            Assert.NotEmpty(document.Render(0,.25).Bgra);
        }
        var closed=new PdfiumDocument(source); closed.Dispose(); closed.Dispose();
        Assert.Throws<ObjectDisposedException>(()=>closed.ExtractText(0));
    });
}
