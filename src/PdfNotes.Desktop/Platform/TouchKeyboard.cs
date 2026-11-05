using System.Runtime.InteropServices;

namespace PdfNotes.Desktop.Platform;

/// <summary>
/// Shows the Windows touch keyboard (spec 7.6): a text box edit started with a finger tap brings it up, as the OS does for
/// its own text fields (the WPF app's, roadmap P4 A6). Uses the InputPane of the window (Windows 10 1803 and later) through raw COM calls; anything that
/// fails is ignored (no touch keyboard on this PC, an older Windows) — typing with a hardware keyboard still works.
/// </summary>
public static class TouchKeyboard
{
    private static readonly Guid InputPaneInterop = new("75CF2C57-9195-4931-8332-F0B409E916AF");
    private static readonly Guid InputPane2 = new("8A6B3F26-7090-4793-944C-C3F2CDE26276");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetForWindow(IntPtr self, IntPtr window, in Guid riid, out IntPtr pane);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TryShow(IntPtr self, out byte shown);

    /// <summary>True when the request was accepted (the keyboard may still decide not to show, e.g. with a keyboard attached in desktop mode).</summary>
    public static bool Show(IntPtr window)
    {
        // macOS has no touch screen; on Linux the desktop's own on-screen keyboard follows the focused input.
        if (window == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134)) return false;
        IntPtr name = IntPtr.Zero, factory = IntPtr.Zero, pane = IntPtr.Zero;
        try
        {
            const string ClassName = "Windows.UI.ViewManagement.InputPane"; // WinRT class id, not UI text (D14e)
            if (WindowsCreateString(ClassName, ClassName.Length, out name) != 0) return false;
            var iid = InputPaneInterop;
            if (RoGetActivationFactory(name, ref iid, out factory) != 0 || factory == IntPtr.Zero) return false;
            // IInputPaneInterop: IUnknown (3) + IInspectable (3), then GetForWindow.
            var get = Marshal.GetDelegateForFunctionPointer<GetForWindow>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 6 * IntPtr.Size));
            if (get(factory, window, InputPane2, out pane) != 0 || pane == IntPtr.Zero) return false;
            // IInputPane2: IUnknown (3) + IInspectable (3), then TryShow, TryHide.
            var show = Marshal.GetDelegateForFunctionPointer<TryShow>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pane), 6 * IntPtr.Size));
            return show(pane, out byte shown) == 0 && shown != 0;
        }
        catch (Exception e) when (e is COMException or EntryPointNotFoundException or DllNotFoundException or SEHException)
        {
            return false;
        }
        finally
        {
            if (pane != IntPtr.Zero) Marshal.Release(pane);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (name != IntPtr.Zero) WindowsDeleteString(name);
        }
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
}
