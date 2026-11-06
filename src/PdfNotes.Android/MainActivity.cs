using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace PdfNotes.Android;

/// <summary>The Android application: starts Avalonia with <see cref="App"/>.</summary>
[Application]
public sealed class AndroidApp(IntPtr handle, JniHandleOwnership ownership) : AvaloniaAndroidApplication<App>(handle, ownership)
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder);
}

/// <summary>
/// The one screen. Turning the tablet or folding the keyboard keeps the activity (and the open document). A PDF opened
/// with DeotPDF from another app ("open with", "share") arrives here (roadmap P5b).
/// </summary>
[Activity(Label = "DeotPDF", Theme = "@style/Theme.AppCompat.DayNight.NoActionBar", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Density | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataMimeType = "application/pdf")]
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "application/pdf")]
public sealed class MainActivity : AvaloniaMainActivity
{
    /// <summary>The activity shown now (dialogs need it).</summary>
    public static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);
        Received(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Received(intent);
    }

    private const int CreateDocumentRequest = 0x5D0C;
    private TaskCompletionSource<global::Android.Net.Uri?>? creating;

    /// <summary>
    /// The system's "save as": a new PDF document named <paramref name="name"/> in the folder the user picks, or null when
    /// cancelled. The type is given, so the document is a PDF to other apps and a name taken gets " (1)" before ".pdf".
    /// </summary>
    public Task<global::Android.Net.Uri?> CreateDocumentAsync(string name)
    {
        creating?.TrySetResult(null);
        creating = new();
        var intent = new Intent(Intent.ActionCreateDocument).AddCategory(Intent.CategoryOpenable).SetType("application/pdf").PutExtra(Intent.ExtraTitle, name);
        StartActivityForResult(intent, CreateDocumentRequest);
        return creating.Task;
    }

    protected override void OnActivityResult(int requestCode, [GeneratedEnum] Result resultCode, Intent? data)
    {
        if (requestCode == CreateDocumentRequest)
        {
            creating?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
            creating = null;
            return;
        }
        base.OnActivityResult(requestCode, resultCode, data);
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        base.OnDestroy();
    }

    /// <summary>A PDF sent to the app: the content URI of "open with", or the stream of a share.</summary>
    private void Received(Intent? intent)
    {
        global::Android.Net.Uri? uri = intent?.Action switch
        {
            Intent.ActionView => intent.Data,
            Intent.ActionSend => OperatingSystem.IsAndroidVersionAtLeast(33)
                ? intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(global::Android.Net.Uri))) as global::Android.Net.Uri
#pragma warning disable CA1422 // the old call for Android 8–12
                : intent.GetParcelableExtra(Intent.ExtraStream) as global::Android.Net.Uri,
#pragma warning restore CA1422
            _ => null
        };
        if (uri is null) return;
        if (intent!.Action == Intent.ActionView) intent.SetData(null); // opened once, not again when the activity is recreated
        App.Open(new ContentLocation(this, uri));
    }
}
