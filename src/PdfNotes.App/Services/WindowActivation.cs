using System.Runtime.InteropServices;

namespace PdfNotes.App.Services;

/// <summary>Brings another PdfNotes window forward (spec 4.6, D14g: a file already open in a window is not opened twice).</summary>
internal static class WindowActivation
{
    private const int RestoreCommand = 9; // SW_RESTORE

    public static void Bring(long handle)
    {
        var window = (nint)handle;
        if (IsIconic(window)) ShowWindow(window, RestoreCommand);
        SetForegroundWindow(window);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
}
