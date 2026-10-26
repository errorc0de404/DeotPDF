using System.IO;
using System.Text.Json;

namespace PdfNotes.Probe;

/// <summary>
/// D14f: opens every PDF named in a list file and saves it again through <see cref="OpenDocument"/> — once as opened by
/// default and once with other apps' annotations converted — so the same files saved by the build before the platform split
/// can be compared with this build's (tools/resave-compare.py). No window, no OS input.
/// </summary>
internal static class D14fResaveCheck
{
    public static int Run(string listFile, string folder)
    {
        Directory.CreateDirectory(folder);
        var inputs = File.ReadAllLines(listFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var rows = new List<object>();
        int failed = 0;
        for (int i = 0; i < inputs.Length; i++)
        {
            foreach (bool foreign in new[] { false, true })
            {
                string output = Path.Combine(folder, $"{i:D3}{(foreign ? "-f" : "")}.pdf");
                try
                {
                    using var document = OpenDocument.OpenPdfAsync(inputs[i]).GetAwaiter().GetResult();
                    var data = document.Source!.AppData;
                    int converted = foreign ? document.ConvertForeignAsync().GetAwaiter().GetResult() : 0; // the document command (spec 14.6, D14g)
                    var model = document.Session.Current;
                    document.SaveAsync(output).GetAwaiter().GetResult();
                    rows.Add(new
                    {
                        Index = i, Foreign = foreign, Input = inputs[i], Output = Path.GetFileName(output),
                        Header = data.HeaderStatus.ToString(), data.RestoredCount, data.ReadOnlyCount,
                        ConvertedForeignCount = converted, Pages = model.Pages.Count,
                        Annotations = model.Pages.Sum(p => p.Annotations.Count),
                    });
                }
                catch (Exception e)
                {
                    failed++;
                    rows.Add(new { Index = i, Foreign = foreign, Input = inputs[i], Error = e.GetType().Name + ": " + e.Message });
                }
            }
        }
        File.WriteAllText(Path.Combine(folder, "resave.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"resaved {inputs.Length * 2 - failed} of {inputs.Length * 2}, failed {failed}");
        return 0;
    }
}
