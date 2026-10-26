using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using PdfNotes.Core.Commands;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Writing;
using PdfNotes.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfNotes.Probe;

/// <summary>
/// <c>d14a-text &lt;folder&gt;</c> (D14a): a fixed corpus of text boxes — fonts, sizes, styles, newlines, emoji, highlights,
/// fill/border, alignment, layout scale — laid out and saved through the editable writer. Writes each box's height,
/// character boxes and the exact text appearance stream to <c>d14a-text.json</c>, so the text engine boundary can be
/// compared byte for byte before and after a change (<c>python -I</c> diff or <c>fc</c>). No window, no input.
/// </summary>
internal static class D14aTextCheck
{
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var boxes = Corpus();
        var pages = boxes.Select(_ => new PageSetup(420, 620, 0, PageSource.Generated, PageBackground.Blank)).ToArray();
        var session = new DocumentSession(DocumentModel.Create(pages));
        for (int i = 0; i < boxes.Count; i++) session.Execute(DocumentEdits.AddAnnotation(session.Current, session.Current.Pages[i].Id, boxes[i]));
        byte[] pdf = EditablePdfWriter.Write(null, null, session.Current);
        string verify = "ok";
        try { EditablePdfWriter.Verify(pdf, session.Current); } catch (Exception e) { verify = e.Message; } // a missing font restores view-only by design
        File.WriteAllBytes(Path.Combine(folder, "d14a-text.pdf"), pdf);

        var streams = new List<string>();
        using (var stream = new MemoryStream(pdf))
        using (var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import))
            foreach (var item in doc.Internals.GetAllObjects().OrderBy(o => o.Reference?.ObjectNumber ?? 0))
                if (item is PdfDictionary d && d.Elements.ContainsKey("/PdfNotesText") && d.Stream is { } s)
                    streams.Add(Encoding.ASCII.GetString(s.UnfilteredValue));

        static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var result = boxes.Select((box, i) =>
        {
            var layout = TextLayout.Measure(box);
            return new
            {
                Index = i,
                Height = R(layout.Height),
                Characters = layout.Characters.Select(c => $"{c.Text}|{c.Line}|{c.Offset}|{R(c.Bounds.X)},{R(c.Bounds.Y)},{R(c.Bounds.Width)},{R(c.Bounds.Height)}").ToArray(),
            };
        }).ToArray();
        File.WriteAllText(Path.Combine(folder, "d14a-text.json"), JsonSerializer.Serialize(new { Verify = verify, Boxes = result, Streams = streams },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine($"PASS d14a-text: {boxes.Count} boxes, {streams.Count} text appearance streams, {result.Sum(r => r.Characters.Length)} characters, verify: {verify}");
        return streams.Count == boxes.Count(b => !b.IsBlank) ? 0 : 1;
    }

    private static TextRun Run(int start, int length, string font = "Malgun Gothic", double size = 12, RgbColor? color = null,
        bool bold = false, bool italic = false, bool underline = false, bool strike = false) =>
        new(start, length, font, size, color ?? RgbColor.Black, bold, italic, underline, strike);

    private static TextBoxAnnotation Box(int i, string text, double width, TextRun[]? runs = null, TextAlignment align = TextAlignment.Left,
        TextBoxStyle? style = null, TextHighlightRange[]? highlights = null) =>
        new(new Guid(i + 1, 0, 0, new byte[8]), false, Affine.Translation(20, 20), text, [.. runs ?? [Run(0, text.Length)]], align, width,
            style ?? TextBoxStyle.Default, [.. highlights ?? []]);

    private static List<TextBoxAnnotation> Corpus()
    {
        const string ko = "한글 문장은 줄바꿈이 어디서 일어나는지 확인합니다. 긴 단어와 공백, 문장부호까지.";
        const string en = "The quick brown fox jumps over the lazy dog; hyphen-ated words, numbers 1234567890 and (brackets).";
        const string mixed = "회의 메모: Q3 계획 — budget 12.5% 증가, 담당 김철수/이영희, 마감 10/31.";
        var list = new List<TextBoxAnnotation>();
        int n = 0;
        foreach (var font in new[] { "Malgun Gothic", "맑은 고딕", "Arial", "Times New Roman", "Segoe UI", "Gulim", "Batang", "Consolas", "NoSuchFont-D14a" })
            list.Add(Box(n++, mixed, 200, [Run(0, mixed.Length, font)]));
        foreach (double size in new[] { 6.0, 9, 11, 12, 14.5, 18, 24, 36, 72 })
            list.Add(Box(n++, ko, 230, [Run(0, ko.Length, size: size)]));
        foreach (double width in new[] { 16.0, 40, 90, 150, 333.3 })
            list.Add(Box(n++, en, width, [Run(0, en.Length, "Arial", 13)]));
        foreach (var align in new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right })
            list.Add(Box(n++, "첫 줄\n둘째 줄은 조금 더 깁니다\n\n넷째 줄 (셋째는 빈 줄)", 180, align: align));
        string styled = "굵게 기울임 밑줄 취소선 색 전부 English";
        var styles = new Func<int, int, TextRun>[] { (st, l) => Run(st, l, bold: true), (st, l) => Run(st, l, italic: true), (st, l) => Run(st, l, underline: true),
            (st, l) => Run(st, l, strike: true), (st, l) => Run(st, l, color: new(200, 30, 60), size: 20), (st, l) => Run(st, l, bold: true, italic: true, underline: true, strike: true, size: 16),
            (st, l) => Run(st, l, "Times New Roman", 15, new(20, 90, 200)) };
        var words = styled.Split(' ');
        var styledRuns = new List<TextRun>();
        for (int w = 0, at = 0; w < words.Length; w++)
        {
            int length = words[w].Length + (w < words.Length - 1 ? 1 : 0);
            styledRuns.Add(styles[w % styles.Length](at, length)); at += length;
        }
        list.Add(Box(n++, styled, 210, [.. styledRuns]));
        list.Add(Box(n++, "이모지 😀👍🏽 결합 문자 é é 한자 漢字 ㄱㄴㄷ", 200));
        list.Add(Box(n++, "      공백으로 시작하고\t탭과   여러 공백   ", 180));
        list.Add(Box(n++, "끝 줄바꿈\n", 150));
        list.Add(Box(n++, mixed, 220, style: new(new(255, 242, 168), .8, true, new(192, 57, 43), .9)));
        list.Add(Box(n++, mixed, 220, style: new(new(30, 30, 30), .35, true, new(0, 0, 255), .3)));
        list.Add(Box(n++, mixed, 220, highlights: [new(0, 5, HighlightHeightMode.Auto, null, new(255, 235, 59), .6), new(10, 20, HighlightHeightMode.Manual, 8, new(129, 199, 132), .5)]));
        list.Add(Box(n++, "투명한 글자", 160, [Run(0, 6, size: 20, color: new(0, 128, 0))]));
        foreach (double scale in new[] { 0.5, 0.73, 1.6 })
        {
            var b = Box(n++, ko + "\n" + en, 230 * scale, [Run(0, ko.Length + 1 + en.Length, size: 12 * scale)],
                highlights: [new(2, 6, HighlightHeightMode.Manual, 9 * scale, new(255, 235, 59), .6)]);
            list.Add(b with { LayoutScale = scale });
        }
        return list;
    }
}
