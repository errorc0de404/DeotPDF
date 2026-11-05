using System.IO.Pipes;
using System.Runtime.InteropServices;
using PdfNotes.Editor.Services;

namespace PdfNotes.Desktop.Platform;

/// <summary>
/// Brings the window of another running DeotPDF forward (spec 4.6: a file already open in a window is not opened
/// twice). Each window's session marker (<see cref="BackupStore.SetOpenDocument"/>) names it by <see cref="WindowId"/>:
/// on Windows the window handle, as the WPF app writes it, so either app brings the other's window forward; on macOS
/// and Linux, where one process cannot raise another's window, the process number, and the window listens on a local
/// pipe named after it (<see cref="Listen"/>) and comes forward itself when asked.
/// </summary>
public static class WindowActivation
{
    private const string Request = "activate";

    /// <summary>The pipe a window process listens on (<see cref="Listen"/>).</summary>
    public static string PipeName(long processId) => $"deotpdf-{processId}";

    /// <summary>What this window writes into its session marker.</summary>
    public static long WindowId(Avalonia.Controls.Window window) =>
        OperatingSystem.IsWindows() ? window.TryGetPlatformHandle()?.Handle ?? 0 : Environment.ProcessId;

    /// <summary>Asks the window <paramref name="id"/> (from <see cref="BackupStore.WindowShowing"/>) to come forward.</summary>
    public static void Bring(long id)
    {
        if (OperatingSystem.IsWindows()) BringWindow((nint)id);
        else Signal(PipeName(id));
    }

    /// <summary>
    /// "DeotPDF file.pdf" for a file another window shows (spec 4.6): that window comes forward and this process does not
    /// start. Returns true when it was handed over.
    /// </summary>
    public static bool HandOver(string[] args)
    {
        if (args is not [{ } path] || !File.Exists(path)) return false;
        if (BackupStore.WindowShowing(AppSettings.Folder, path) is not { } other) return false;
        Bring(other);
        return true;
    }

    /// <summary>
    /// Listens on <paramref name="name"/> until <paramref name="stop"/> is cancelled and calls <paramref name="activate"/>
    /// for each request (on the pipe's thread). Used on macOS and Linux; tests use it on every system.
    /// </summary>
    public static async Task Listen(string name, Action activate, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop);
                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync(stop) == Request) activate();
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { } // a client that went away: wait for the next
        }
    }

    /// <summary>Sends one request to <paramref name="name"/>; false when nobody listens there (within a second).</summary>
    public static bool Signal(string name)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1000);
            using var writer = new StreamWriter(client);
            writer.WriteLine(Request);
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    private static void BringWindow(nint window)
    {
        if (!OperatingSystem.IsWindows()) return;
        const int restore = 9; // SW_RESTORE
        if (IsIconic(window)) ShowWindow(window, restore);
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
