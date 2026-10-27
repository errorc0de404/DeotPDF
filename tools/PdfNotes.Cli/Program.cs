using System.Text;

namespace PdfNotes.Cli;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length > 0 && args[0] == "mcp") return Mcp.DeotMcpServer.Run(args[1..]);
        return CliApp.Run(args, Console.Out, Console.Error);
    }
}
