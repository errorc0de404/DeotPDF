using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.Core.Geometry;
using PdfNotes.Core.Models;
using PdfNotes.Pdf.Engine;

namespace PdfNotes.Cli;

/// <summary>The WPF imaging of the shipped Windows CLI (needs the STA thread <see cref="CliApp.Run"/> provides).</summary>
public static partial class PageRaster
{
    /// <summary>A new image drawn through <see cref="IRasterCanvas"/>.</summary>
    public static RenderedPage Draw(int width, int height, Action<IRasterCanvas> draw)
    {
        var image = Compose(width, height, context => draw(new WpfCanvas(context)));
        var bgra = new byte[width * height * 4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(bgra, width * 4, 0);
        return new RenderedPage(width, height, width * 4, bgra);
    }

    public static void SavePng(RenderedPage page, string path) => SavePng(ToBitmap(page), path);

    public static BitmapSource ToBitmap(RenderedPage page)
    {
        var bitmap = BitmapSource.Create(page.Width, page.Height, 96, 96, PixelFormats.Bgra32, null, page.Bgra, page.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A new image drawn by <paramref name="draw"/> at 1 DIP = 1 pixel.</summary>
    public static BitmapSource Compose(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) draw(context);
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    public static void SavePng(BitmapSource image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class WpfCanvas(DrawingContext context) : IRasterCanvas
    {
        public void Fill(int x, int y, int width, int height, RgbColor color) =>
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)), null, new Rect(x, y, width, height));

        public void DrawImage(RenderedPage image, int x, int y) => context.DrawImage(ToBitmap(image), new Rect(x, y, image.Width, image.Height));

        public void Polygon(IReadOnlyList<Point2> points, RgbColor color, double opacity)
        {
            var fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)), 1);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(points[0].X, points[0].Y), true, true);
                for (int i = 1; i < points.Count; i++) g.LineTo(new Point(points[i].X, points[i].Y), true, false);
            }
            geometry.Freeze();
            context.DrawGeometry(fill, pen, geometry);
        }

        public void Label(string text, double x, double y, double size)
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.Black, 1.0);
            context.DrawText(formatted, new Point(x, y));
        }
    }
}

/// <summary>Image files for <c>add image</c>: pixel size and horizontal DPI from the WPF decoder.</summary>
public static class ImageProbe
{
    public static (int Width, int Height, double DpiX) Read(byte[] bytes)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight, frame.DpiX);
    }
}
