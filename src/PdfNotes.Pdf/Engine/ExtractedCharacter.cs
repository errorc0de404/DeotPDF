namespace PdfNotes.Pdf.Engine;

/// <summary>A point in PDF user space or in device pixels, as PDFium gives it.</summary>
public record ProbePoint(double X, double Y);

/// <summary>Angle: PDFium's character rotation in radians (clockwise in PDF user space), 0 when unknown.</summary>
public record ExtractedCharacter(int Index, string Text, ProbePoint[] Quad, double Angle = 0);
