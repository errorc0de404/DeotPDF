using System.Runtime.ExceptionServices;
using System.Windows.Interop;
using PdfNotes.App;

namespace PdfNotes.Tests;

// Development-environment smoke check, not a product feature test.
public class WpfEnvironmentTests
{
    [Fact]
    public void CompiledXamlCanCreateNativeWindowOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                try
                {
                    var handle = new WindowInteropHelper(window).EnsureHandle();
                    Assert.NotEqual(nint.Zero, handle);
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }) { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF window creation timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
