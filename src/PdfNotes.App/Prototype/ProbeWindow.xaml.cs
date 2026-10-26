using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.App.Prototype;

/// <summary>D01 storage proof window, kept for tools/PdfNotes.Probe "ui". Not part of the product UI.</summary>
public partial class ProbeWindow : Window
{
    private string folder = Path.Combine(Path.GetTempPath(), "PdfNotes-D01", Guid.NewGuid().ToString("N"));
    private string? current;
    private ProbeModel? model;
    private bool busy;
    private bool suppressSelection;
    private Task lastCommand=Task.CompletedTask;
    public ProbeWindow()
    {
        InitializeComponent();
    }

    private async Task Perform(Func<Task> action)
    {
        if(busy) return;
        busy=true;
        CreateButton.IsEnabled=OpenButton.IsEnabled=EditButton.IsEnabled=FlattenButton.IsEnabled=false;
        try { await action(); }
        catch(Exception e) { Status.Text="검증 오류: "+e.Message; throw; }
        finally
        {
            busy=false;
            CreateButton.IsEnabled=OpenButton.IsEnabled=EditButton.IsEnabled=FlattenButton.IsEnabled=true;
        }
    }

    public async Task CreateProof()
    {
        Directory.CreateDirectory(folder);
        string source=Path.Combine(folder,"source.pdf");
        await Task.Run(()=>ProbeSamplePdf.Create(source));
        // WPF text layout must run on the STA UI thread.
        model=ProbeFactory.Create(source);
        current=Path.Combine(folder,"editable.pdf");
        await Task.Run(()=>ProbePdfStore.SaveEditable(source,model,current));
        await ReopenProof();
    }

    public async Task ReopenProof()
    {
        if(current is null) throw new InvalidOperationException("먼저 샘플을 생성하세요.");
        var reopened=await Task.Run(()=>ProbePdfStore.Open(current));
        model=reopened.Model ?? throw new InvalidDataException("편집 데이터가 없습니다.");
        await RefreshPreview();
        Status.Text=$"재열기 성공 · {model.Pages.Sum(p=>p.Annotations.Length)}개 요소 · {current}";
        var missing=ProbeFactory.MissingFonts(model);
        if(missing.Length>0) Status.Text+=" · 없는 글꼴: "+string.Join(", ",missing)+". 저장 외형은 유지되며 글자 편집 시 대체 글꼴을 선택해야 합니다.";
    }

    public async Task EditProof()
    {
        if(model is null || current is null) throw new InvalidOperationException("먼저 샘플을 생성하세요.");
        var changed=ProbeFactory.Edit(model);
        string target=Path.Combine(folder,"edited.pdf");
        await Task.Run(()=>ProbePdfStore.SaveEditable(current,changed,target));
        current=target;
        await ReopenProof();
        Status.Text="획을 녹색 / 12pt로 변경하고 재저장했습니다. · "+target;
    }

    public async Task FlattenProof()
    {
        if(model is null || current is null) throw new InvalidOperationException("먼저 샘플을 생성하세요.");
        string target=Path.Combine(folder,"flattened.pdf");
        await Task.Run(()=>ProbePdfStore.ExportFlattened(current,model,target));
        Status.Text="별도 Flatten 생성 완료 · 편집 원본 유지 · "+target;
    }

    private async Task RefreshPreview()
    {
        if(current is null || model is null) return;
        int page=Math.Max(0,PageChoice.SelectedIndex);
        var opened=await Task.Run(()=>ProbePdfStore.Open(current));
        var metrics=await Task.Run(()=>ProbePdfStore.GetMetrics(current));
        using var background=new PdfiumDocument(opened.BackgroundPdf);
        // Deliberately simple D01 preview; production async rendering belongs to D03.
        var composed=ProbePreview.Compose(background,page,metrics[page],model.Pages[page]);
        Preview.Source=ProbeFactory.Bitmap(composed);
    }

    private async void CreateClick(object sender,RoutedEventArgs e) { try { lastCommand=Perform(CreateProof); await lastCommand; } catch { } }
    private async void OpenClick(object sender,RoutedEventArgs e) { try { lastCommand=Perform(ReopenProof); await lastCommand; } catch { } }
    private async void EditClick(object sender,RoutedEventArgs e) { try { lastCommand=Perform(EditProof); await lastCommand; } catch { } }
    private async void FlattenClick(object sender,RoutedEventArgs e) { try { lastCommand=Perform(FlattenProof); await lastCommand; } catch { } }
    private async void PageChanged(object sender,SelectionChangedEventArgs e)
    { if(!IsInitialized || busy || suppressSelection || model is null) return; try { await Perform(RefreshPreview); } catch { } }

    public async Task RunProbeUiCheck(string directory)
    {
        folder=directory;
        foreach(var button in new[]{CreateButton,OpenButton,EditButton,FlattenButton})
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await lastCommand;
        }
        suppressSelection=true;
        for(int i=0;i<3;i++)
        {
            PageChoice.SelectedIndex=i;
            await RefreshPreview();
            ProbeFactory.SavePng((BitmapSource)Preview.Source,Path.Combine(folder,$"wpf-overlay-{i+1}.png"));
        }
        PageChoice.SelectedIndex=0;
        await RefreshPreview();
        suppressSelection=false;
        UpdateLayout();
        var screenshot=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32);
        screenshot.Render(this);
        ProbeFactory.SavePng(screenshot,Path.Combine(folder,"wpf-window.png"));
        File.WriteAllText(Path.Combine(folder,"ui-result.json"),JsonSerializer.Serialize(new
        {
            WindowShown=IsVisible,CreateOpenEditFlattenHandlersExecuted=true,PagesRendered=3,
            CurrentEditablePath=current,ModelStillEditableAfterFlatten=model is not null,
            Screenshot="wpf-window.png",ManualMouseInteractionVerified=false
        }));
    }
}
