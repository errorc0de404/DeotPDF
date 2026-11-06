using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace PdfNotes.UI.Canvas;

/// <summary>
/// The two cursors no system has (roadmap P4 A5, the WPF SelectionCursors): a circular arrow over a rotation handle and a
/// padlock over a locked annotation (spec 5.2, D14g). Drawn once per pixel size in the 32 px design grid of the WPF app,
/// black with a white outline (Cursor.Fill, Cursor.Outline), hotspot in the middle.
/// </summary>
public static class CanvasCursors
{
    private enum Kind { Rotate, Lock, Pen }

    private static readonly Dictionary<(Kind Kind, int Size), Cursor> Cache = [];
    private static readonly SKColor Fill = SKColors.Black, Outline = SKColors.White;

    /// <summary>32 px times the screen scale (Windows does not scale a custom cursor for a per-monitor aware app, D13e).</summary>
    public static int Pixels(double scale) => Math.Clamp((int)Math.Round(32 * (scale > 0 ? scale : 1)), 32, 128);

    public static Cursor Rotate(double scale) => Get(Kind.Rotate, Pixels(scale));

    public static Cursor Lock(double scale) => Get(Kind.Lock, Pixels(scale));

    /// <summary>
    /// The pen of the drawing tools (the WPF app shows the system's Cursors.Pen; Avalonia has no such stock cursor): a pen
    /// tilted to the lower left, the hotspot at its tip.
    /// </summary>
    public static Cursor Pen(double scale) => Get(Kind.Pen, Pixels(scale));

    private static Cursor Get(Kind kind, int size)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue((kind, size), out var cursor))
            {
                using var bitmap = kind == Kind.Pen ? DrawPen(size) : Draw(kind == Kind.Lock, size);
                var hotspot = kind == Kind.Pen ? new PixelPoint((int)Math.Round(PenTip.X * size / 32), (int)Math.Round(PenTip.Y * size / 32)) : new PixelPoint(size / 2, size / 2);
                Cache[(kind, size)] = cursor = new Cursor(bitmap, hotspot);
            }
            return cursor;
        }
    }

    private static readonly SKPoint PenTip = new(3, 29);

    /// <summary>The pen cursor picture: a slim body from the tip at the lower left to the top right, black with a white outline.</summary>
    public static Bitmap DrawPen(int size)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        c.Scale(size / 32f);
        using var body = new SKPath();
        // Along the axis from the tip (3,29) to the end (27,5): a cone to the nib, then the barrel.
        body.MoveTo(PenTip);
        body.LineTo(5.5f, 22.5f);
        body.LineTo(22.5f, 5.5f);
        body.LineTo(26.5f, 9.5f);
        body.LineTo(9.5f, 26.5f);
        body.Close();
        using (var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, StrokeJoin = SKStrokeJoin.Round, Color = Outline }) c.DrawPath(body, outline);
        using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = Fill }) c.DrawPath(body, fill);
        using (var band = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Outline }) c.DrawLine(7.5f, 20.5f, 11.5f, 24.5f, band); // where the nib starts
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(png.ToArray());
        return new Bitmap(stream);
    }

    /// <summary>The cursor picture (tests look at it).</summary>
    public static Bitmap Draw(bool padlock, int size)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        c.Scale(size / 32f);
        static SKPaint Stroke(SKColor color, float width) => new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color, StrokeWidth = width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        static SKPaint Solid(SKColor color) => new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        if (padlock)
        {
            using var shackle = new SKPath();
            shackle.MoveTo(11, 16);
            shackle.LineTo(11, 11);
            shackle.ArcTo(new SKRect(11, 6, 21, 16), 180, 180, false);
            shackle.LineTo(21, 16);
            var body = new SKRoundRect(new SKRect(8, 15, 24, 27), 2, 2);
            using (var p = Stroke(Outline, 5)) c.DrawPath(shackle, p);
            using (var p = Stroke(Outline, 2.5f)) c.DrawRoundRect(body, p);
            using (var p = Solid(Outline)) c.DrawRoundRect(body, p);
            using (var p = Stroke(Fill, 2.5f)) c.DrawPath(shackle, p);
            using (var p = Solid(Fill)) c.DrawRoundRect(body, p);
        }
        else
        {
            // A 280° arc around the middle, open at the top right, with an arrow head at its end.
            const float r = 9, cx = 16, cy = 16;
            using var arc = new SKPath();
            arc.AddArc(new SKRect(cx - r, cy - r, cx + r, cy + r), -60, 280);
            var tip = new SKPoint(cx + r * MathF.Cos(220 * MathF.PI / 180), cy + r * MathF.Sin(220 * MathF.PI / 180));
            using var head = new SKPath();
            head.MoveTo(tip.X - 5.5f, tip.Y + 1.5f);
            head.LineTo(tip.X + 3.5f, tip.Y + 3.5f);
            head.LineTo(tip.X + 0.5f, tip.Y - 5.5f);
            head.Close();
            using (var p = Stroke(Outline, 4.5f)) c.DrawPath(arc, p);
            using (var p = Stroke(Outline, 2.5f)) c.DrawPath(head, p);
            using (var p = Solid(Outline)) c.DrawPath(head, p);
            using (var p = Stroke(Fill, 2)) c.DrawPath(arc, p);
            using (var p = Solid(Fill)) c.DrawPath(head, p);
        }
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(png.ToArray());
        return new Bitmap(stream);
    }
}
