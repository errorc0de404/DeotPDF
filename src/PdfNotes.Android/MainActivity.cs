using Android.App;
using Android.Content.PM;
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

/// <summary>The one screen. Turning the tablet or folding the keyboard keeps the activity (and the open document).</summary>
[Activity(Label = "DeotPDF", Theme = "@style/Theme.AppCompat.DayNight.NoActionBar", MainLauncher = true, Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Density | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
public sealed class MainActivity : AvaloniaMainActivity
{
}
