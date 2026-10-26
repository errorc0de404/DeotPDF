using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfNotes.Pdf.Engine;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.App.Prototype;

public static class ProbePreview
{
    // Prototype compositing only. D03/D09 replace page-sized per-annotation buffers with live DrawingVisuals.
    public static RenderedPage Compose(PdfiumDocument background, int pageIndex, PageMetrics metrics, ProbePage page)
    {
        var image = background.Render(pageIndex,userUnit:metrics.UserUnit);
        var tl = background.PageToPixel(pageIndex,new(metrics.CropLeft,metrics.CropTop),image.Width,image.Height);
        var tr = background.PageToPixel(pageIndex,new(metrics.CropRight,metrics.CropTop),image.Width,image.Height);
        var bl = background.PageToPixel(pageIndex,new(metrics.CropLeft,metrics.CropBottom),image.Width,image.Height);
        var transform = new Matrix((tr.X-tl.X)/metrics.WidthPt,(tr.Y-tl.Y)/metrics.WidthPt,
            (bl.X-tl.X)/metrics.HeightPt,(bl.Y-tl.Y)/metrics.HeightPt,tl.X,tl.Y);
        foreach(var annotation in page.Annotations)
        {
            var visual = new DrawingVisual();
            using(var context = visual.RenderOpen())
            {
                context.PushTransform(new MatrixTransform(transform));
                foreach(var path in annotation.Paths)
                {
                    var geometry = Geometry(path);
                    var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(path.Opacity*255),path.Color.R,path.Color.G,path.Color.B));
                    if(path.Fill) context.DrawGeometry(brush,null,geometry);
                    else context.DrawGeometry(null,new Pen(brush,path.Width){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round},geometry);
                }
                if(annotation.Image is { } png)
                {
                    using var stream = new MemoryStream(png.Png);
                    var decoder = BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                    context.DrawImage(decoder.Frames[0],new Rect(png.X,png.Y,png.Width,png.Height));
                }
                context.Pop();
            }
            var raster = new RenderTargetBitmap(image.Width,image.Height,96,96,PixelFormats.Pbgra32);
            raster.Render(visual);
            var overlay = new byte[image.Bgra.Length];
            raster.CopyPixels(overlay,image.Stride,0);
            for(int i=0;i<overlay.Length;i+=4)
            {
                double alpha=overlay[i+3]/255d;
                for(int color=0;color<3;color++)
                {
                    double result=annotation.Type=="highlight"
                        ? image.Bgra[i+color]*(1-alpha+overlay[i+color]/255d)
                        : overlay[i+color]+image.Bgra[i+color]*(1-alpha);
                    image.Bgra[i+color]=(byte)Math.Clamp(Math.Round(result),0,255);
                }
            }
        }
        return image;
    }

    private static StreamGeometry Geometry(ProbePath path)
    {
        var geometry=new StreamGeometry{FillRule=path.EvenOdd?FillRule.EvenOdd:FillRule.Nonzero};
        using(var builder=geometry.Open())
        {
            for(int i=0;i<path.Segments.Length;i++)
            {
                var s=path.Segments[i];
                Point P(int n)=>new(s.Points[n].X,s.Points[n].Y);
                switch(s.Kind)
                {
                    case "M":
                        bool closed=false;
                        for(int j=i+1;j<path.Segments.Length && path.Segments[j].Kind!="M";j++) if(path.Segments[j].Kind=="Z") closed=true;
                        builder.BeginFigure(P(0),path.Fill,closed); break;
                    case "L": builder.LineTo(P(0),true,false); break;
                    case "C": builder.BezierTo(P(0),P(1),P(2),true,false); break;
                    case "Z": break;
                    default: throw new InvalidDataException("Unknown prototype path.");
                }
            }
        }
        geometry.Freeze(); return geometry;
    }
}
