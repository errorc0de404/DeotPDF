using PdfNotes.Pdf.Engine;

namespace PdfNotes.Pdf.Prototype;

// D01 proof data only. D02 defines the production Core schema and commands.
public record ProbeColor(byte R, byte G, byte B);
public record ProbeSegment(string Kind, ProbePoint[] Points);
public record ProbePath(ProbeSegment[] Segments, ProbeColor Color, bool Fill,
    double Width = 1, double Opacity = 1, bool EvenOdd = false);
public record ProbeTextRun(string Text, string Font, double Size, ProbeColor Color,
    bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false);
public record ProbeImage(double X, double Y, double Width, double Height,
    int PixelWidth, int PixelHeight, byte[] Rgba, byte[] Png);
public record ProbeAnnotation(Guid Id, string Type, ProbePath[] Paths,
    ProbeTextRun[]? TextRuns = null, ProbeImage? Image = null);
public record ProbePage(Guid Id, ProbeAnnotation[] Annotations);
public record ProbeModel(Guid DocumentId, ProbePage[] Pages);
public record ProbeOpenResult(ProbeModel? Model, byte[] BackgroundPdf, string[] Warnings);

public record PageMetrics(double WidthPt, double HeightPt, double CropLeft,
    double CropBottom, double CropRight, double CropTop, int Rotation, double UserUnit);
