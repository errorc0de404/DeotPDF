using System.Text;

namespace PdfNotes.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length > 0 && args[0] == "mcp") return Mcp.DeotMcpServer.Run(args[1..]);
        // The document a DeotPDF window has open instead of a file (MCP_LIVE_PLAN).
        if (args.Length > 0 && args[0] == "live" || args.Contains("--live")) return LiveClient.Run(args, Console.Out, LiveClient.DefaultClient);
        return CliApp.Run(args, Console.Out, Console.Error);
    }
}
