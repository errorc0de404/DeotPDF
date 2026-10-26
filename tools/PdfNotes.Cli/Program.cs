using System.Text;

namespace PdfNotes.Cli;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        return CliApp.Run(args, Console.Out, Console.Error);
    }
}
