using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.Core.Geometry;
using PdfNotes.App.Themes;
using PdfNotes.Editor.Input;

namespace PdfNotes.App.Tools;

/// <summary>Cursors over a selection (spec 5.3): move (✥), direction-aware resize and a rotation cursor.</summary>
public static class SelectionCursors
{
    public static Cursor Move => Cursors.SizeAll;

    /// <summary>The WPF cursor of a tool's cursor request (null: the view's default arrow).</summary>
    public static Cursor? For(ToolCursor? cursor) => cursor?.Shape switch
    {
        null => null,
        CursorShape.Arrow => Cursors.Arrow,
        CursorShape.Pen => Cursors.Pen,
        CursorShape.Cross => Cursors.Cross,
        CursorShape.IBeam => Cursors.IBeam,
        CursorShape.Hand => Cursors.Hand,
        CursorShape.Move => Move,
        CursorShape.SizeAll => Cursors.SizeAll,
        CursorShape.Rotate => RotateFor(cursor.Value.Scale),
        CursorShape.Resize => Resize(cursor.Value.Direction),
        CursorShape.Lock => LockFor(cursor.Value.Scale),
        _ => null
    };

    /// <summary>
    /// A circular arrow; Windows has no stock rotation cursor. Built once per monitor scale as a .cur of 32 px × the DPI scale
    /// (Windows does not scale a custom cursor for a per-monitor aware app, D13e) with the hotspot in the middle.
    /// </summary>
    public static Cursor RotateFor(double dpiScale) => RotateCursor(RotatePixels(dpiScale));

    /// <summary>Pixel size of the rotation cursor at <paramref name="dpiScale"/>.</summary>
    public static int RotatePixels(double dpiScale) => Math.Clamp((int)Math.Round(32 * (dpiScale > 0 ? dpiScale : 1)), 32, 128);

    private static readonly Dictionary<int, Cursor> rotate = [], locks = []; // shared by every window's thread: use under the lock
    private static readonly Lock cursorCache = new();

    /// <summary>A padlock (spec 5.2, D14g) over a locked annotation, 32 px × the DPI scale, hotspot in the middle.</summary>
    public static Cursor LockFor(double dpiScale)
    {
        int size = RotatePixels(dpiScale);
        lock (cursorCache)
        {
            if (!locks.TryGetValue(size, out var cursor)) locks[size] = cursor = CreateLock(size);
            return cursor;
        }
    }

    private static Cursor CreateLock(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0)); // the 32 px design grid
            Brush fill = Theme.Brush("Cursor.Fill"), edge = Theme.Brush("Cursor.Outline");
            var shackle = new StreamGeometry();
            using (var c = shackle.Open())
            {
                c.BeginFigure(new Point(11, 16), false, false);
                c.LineTo(new Point(11, 11), true, false);
                c.ArcTo(new Point(21, 11), new Size(5, 5), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(21, 16), true, false);
            }
            shackle.Freeze();
            var body = new RectangleGeometry(new Rect(8, 15, 16, 12), 2, 2);
            dc.DrawGeometry(null, new Pen(edge, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, shackle);
            dc.DrawGeometry(edge, new Pen(edge, 2.5), body);
            dc.DrawGeometry(null, new Pen(fill, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, shackle);
            dc.DrawGeometry(fill, null, body);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);
        Unpremultiply(pixels);
        return new Cursor(new MemoryStream(Cur(pixels, size, hotspot: size / 2)));
    }

    private static Cursor RotateCursor(int size)
    {
        lock (cursorCache)
        {
            if (!rotate.TryGetValue(size, out var cursor)) rotate[size] = cursor = CreateRotate(size);
            return cursor;
        }
    }

    /// <summary>
    /// The resize cursor whose arrows point along <paramref name="direction"/> (viewport DIPs, y down), from the frame
    /// center to the handle. A rotated selection gets the stock cursor nearest to its handle direction.
    /// </summary>
    public static Cursor Resize(Point2 direction)
    {
        double angle = Math.Atan2(direction.Y, direction.X) * 180 / Math.PI;
        angle = ((angle % 180) + 180) % 180; // the cursors are symmetric: only the axis matters
        return angle switch
        {
            < 22.5 or >= 157.5 => Cursors.SizeWE,
            < 67.5 => Cursors.SizeNWSE, // down-right / up-left
            < 112.5 => Cursors.SizeNS,
            _ => Cursors.SizeNESW
        };
    }

    private static Cursor CreateRotate(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0)); // drawn in the 32 px design grid
            var arc = new StreamGeometry();
            using (var c = arc.Open())
            {
                // 280° arc around the middle, open at the top right, then an arrow head at its end.
                double r = 9, cx = 16, cy = 16;
                Point At(double degrees) => new(cx + r * Math.Cos(degrees * Math.PI / 180), cy + r * Math.Sin(degrees * Math.PI / 180));
                c.BeginFigure(At(-60), false, false);
                c.ArcTo(At(220), new Size(r, r), 0, true, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            var tip = new Point(16 + 9 * Math.Cos(220 * Math.PI / 180), 16 + 9 * Math.Sin(220 * Math.PI / 180));
            var head = new StreamGeometry();
            using (var c = head.Open())
            {
                c.BeginFigure(new Point(tip.X - 5.5, tip.Y + 1.5), true, true);
                c.LineTo(new Point(tip.X + 3.5, tip.Y + 3.5), true, false);
                c.LineTo(new Point(tip.X + 0.5, tip.Y - 5.5), true, false);
            }
            head.Freeze();
            Brush fill = Theme.Brush("Cursor.Fill"), edge = Theme.Brush("Cursor.Outline");
            var outline = new Pen(edge, 4.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var line = new Pen(fill, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, outline, arc);
            dc.DrawGeometry(edge, new Pen(edge, 2.5) { LineJoin = PenLineJoin.Round }, head);
            dc.DrawGeometry(null, line, arc);
            dc.DrawGeometry(fill, null, head);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);
        Unpremultiply(pixels);
        return new Cursor(new MemoryStream(Cur(pixels, size, hotspot: size / 2)));
    }

    private static void Unpremultiply(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            if (a is 0 or 255) continue;
            for (int k = 0; k < 3; k++) bgra[i + k] = (byte)Math.Min(255, bgra[i + k] * 255 / a);
        }
    }

    /// <summary>A one-image .cur: 32-bit BGRA DIB (rows bottom-up, double height for the AND mask) with an empty AND mask.</summary>
    private static byte[] Cur(byte[] bgra, int size, int hotspot)
    {
        int maskRow = (size + 31) / 32 * 4;
        int image = 40 + bgra.Length + maskRow * size;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)1); // ICONDIR: cursor, one image
        w.Write((byte)size); w.Write((byte)size); w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)hotspot); w.Write((ushort)hotspot); w.Write(image); w.Write(22);
        w.Write(40); w.Write(size); w.Write(size * 2); w.Write((ushort)1); w.Write((ushort)32); // BITMAPINFOHEADER
        w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int row = size - 1; row >= 0; row--) w.Write(bgra, row * size * 4, size * 4);
        w.Write(new byte[maskRow * size]);
        w.Flush();
        return stream.ToArray();
    }
}
