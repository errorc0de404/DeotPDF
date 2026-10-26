using System.Runtime.InteropServices;
using PdfNotes.Pdf.Prototype;

namespace PdfNotes.Pdf.Engine;

/// <summary>BGRA pixels (premultiplied over an opaque white page) returned by the engine. Never shared with native code.</summary>
public sealed record RenderedPage(int Width, int Height, int Stride, byte[] Bgra);

/// <summary>Rectangle in whole device pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public long Area => (long)Width * Height;
}

/// <summary>A link of a page in raw PDF user space (spec 24.3): a page of the same file (with an optional point) or a URI.</summary>
public sealed record PdfiumLink(double Left, double Bottom, double Right, double Top, int? TargetPage, double? TargetX, double? TargetY, string? Uri);

/// <summary>An outline item (spec 24.4) with its target like <see cref="PdfiumLink"/>.</summary>
public sealed record PdfiumBookmark(string Title, int? TargetPage, double? TargetX, double? TargetY, string? Uri, PdfiumBookmark[] Children);

/// <summary>PDFium FPDF_GetLastError codes that callers distinguish.</summary>
public enum PdfiumError { Unknown = 1, File = 2, Format = 3, Password = 4, Security = 5, Page = 6 }

public sealed class PdfiumOpenException(PdfiumError error)
    : IOException($"PDFium open failed ({(int)error}).")
{
    public PdfiumError Error { get; } = error;
}

/// <summary>Serialized native boundary. Page/text/bitmap handles never escape a call.</summary>
public sealed class PdfiumDocument : IDisposable
{
    /// <summary>Largest bitmap one request may allocate (width × height).</summary>
    public const long MaxBitmapPixels = 64_000_000;

    private static readonly object Gate = new();
    private static int users;
    private nint document;
    private GCHandle pinned;
    public int PageCount { get; }

    public PdfiumDocument(string path) : this(File.ReadAllBytes(path)) { }

    /// <summary>Loads from memory. The array is pinned for the lifetime of this object and must not be modified.</summary>
    public PdfiumDocument(byte[] pdf)
    {
        lock (Gate)
        {
            if (users == 0) Native.FPDF_InitLibrary();
            users++;
            try
            {
                pinned = GCHandle.Alloc(pdf, GCHandleType.Pinned);
                document = Native.FPDF_LoadMemDocument64(pinned.AddrOfPinnedObject(), (nuint)pdf.Length, null);
                if (document == 0)
                {
                    uint code = Native.FPDF_GetLastError();
                    throw new PdfiumOpenException(Enum.IsDefined(typeof(PdfiumError), (int)code) ? (PdfiumError)code : PdfiumError.Unknown);
                }
                PageCount = Native.FPDF_GetPageCount(document);
            }
            catch
            {
                if(pinned.IsAllocated) Release();
                else if(--users==0) Native.FPDF_DestroyLibrary();
                throw;
            }
        }
    }

    /// <summary>True when the file uses any security handler (even without a user password).</summary>
    public bool IsEncrypted
    {
        get { lock (Gate) { ThrowIfDisposed(); return Native.FPDF_GetSecurityHandlerRevision(document) != -1; } }
    }

    /// <summary>
    /// A copy of the file without its security handler (FPDF_SaveAsCopy with FPDF_REMOVE_SECURITY): how a PDF that opens
    /// without a password but carries permission restrictions becomes editable (spec 24.6, D13h). The original stays unchanged.
    /// </summary>
    public byte[] SaveCopyWithoutSecurity()
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            using var output = new MemoryStream();
            Native.WriteBlock write = (_, data, size) =>
            {
                if (size == 0) return 1;
                var chunk = new byte[size];
                Marshal.Copy(data, chunk, 0, (int)size);
                output.Write(chunk);
                return 1;
            };
            var file = new Native.FileWrite { Version = 1, WriteBlockCallback = Marshal.GetFunctionPointerForDelegate(write) };
            int ok = Native.FPDF_SaveAsCopy(document, ref file, Native.FPDF_REMOVE_SECURITY);
            GC.KeepAlive(write);
            if (ok == 0 || output.Length == 0) throw new InvalidDataException("PDFium could not write the copy.");
            return output.ToArray();
        }
    }

    /// <summary>
    /// Link annotations of a page (spec 24.3) in raw PDF user space: an internal destination (page index in this file and
    /// the point when the destination has one, in the target page's user space) or a URI. Other actions are left out.
    /// </summary>
    public PdfiumLink[] Links(int index)
    {
        lock (Gate)
        {
            var page = LoadPage(index);
            try
            {
                var result = new List<PdfiumLink>();
                int position = 0;
                for (int guard = 0; guard < 10000 && Native.FPDFLink_Enumerate(page, ref position, out nint link) != 0; guard++)
                {
                    if (link == 0 || Native.FPDFLink_GetAnnotRect(link, out var r) == 0) continue;
                    var (target, x, y, uri) = Target(Native.FPDFLink_GetDest(document, link), Native.FPDFLink_GetAction(link));
                    if (target is null && uri is null) continue;
                    result.Add(new(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top), target, x, y, uri));
                }
                return result.ToArray();
            }
            finally { Native.FPDF_ClosePage(page); }
        }
    }

    /// <summary>The document outline (bookmarks, spec 24.4) as a tree; cycles and very large outlines are cut off.</summary>
    public PdfiumBookmark[] Bookmarks()
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            var seen = new HashSet<nint>();
            int budget = 5000;
            PdfiumBookmark[] Children(nint parent, int depth)
            {
                var items = new List<PdfiumBookmark>();
                if (depth > 32) return [];
                for (nint b = Native.FPDFBookmark_GetFirstChild(document, parent); b != 0 && budget-- > 0 && seen.Add(b); b = Native.FPDFBookmark_GetNextSibling(document, b))
                {
                    uint bytes = Native.FPDFBookmark_GetTitle(b, 0, 0);
                    string title = "";
                    if (bytes > 2)
                    {
                        var item = b;
                        var buffer = Filled(bytes, address => Native.FPDFBookmark_GetTitle(item, address, bytes));
                        title = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)bytes - 2);
                    }
                    var (target, x, y, uri) = Target(Native.FPDFBookmark_GetDest(document, b), Native.FPDFBookmark_GetAction(b));
                    items.Add(new(title, target, x, y, uri, Children(b, depth + 1)));
                }
                return items.ToArray();
            }
            return Children(0, 0);
        }
    }

    private static byte[] Filled(uint length, Func<nint, uint> fill)
    {
        var buffer = new byte[length];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { fill(handle.AddrOfPinnedObject()); }
        finally { handle.Free(); }
        return buffer;
    }

    /// <summary>Where a destination or action points: a page of this file (with its point) or a URI.</summary>
    private (int? Page, double? X, double? Y, string? Uri) Target(nint dest, nint action)
    {
        if (dest == 0 && action != 0)
        {
            uint type = Native.FPDFAction_GetType(action);
            if (type == Native.PDFACTION_GOTO) dest = Native.FPDFAction_GetDest(document, action);
            else if (type == Native.PDFACTION_URI)
            {
                uint length = Native.FPDFAction_GetURIPath(document, action, 0, 0);
                if (length <= 1 || length > 8192) return (null, null, null, null);
                var buffer = Filled(length, address => Native.FPDFAction_GetURIPath(document, action, address, length));
                return (null, null, null, System.Text.Encoding.ASCII.GetString(buffer, 0, (int)length - 1));
            }
        }
        if (dest == 0) return (null, null, null, null);
        int page = Native.FPDFDest_GetDestPageIndex(document, dest);
        if (page < 0 || page >= PageCount) return (null, null, null, null);
        if (Native.FPDFDest_GetLocationInPage(dest, out int hasX, out int hasY, out _, out float x, out float y, out _) != 0)
            return (page, hasX != 0 && float.IsFinite(x) ? x : null, hasY != 0 && float.IsFinite(y) ? y : null, null);
        return (page, null, null, null);
    }

    /// <summary>Number of signature fields that carry a signature value.</summary>
    public int SignatureCount
    {
        get { lock (Gate) { ThrowIfDisposed(); return Math.Max(0, Native.FPDF_GetSignatureCount(document)); } }
    }

    /// <summary>Renders the whole page at its own /Rotate. Size comes from PDFium's page size × UserUnit.</summary>
    public RenderedPage Render(int index, double pixelsPerPoint = 1, bool annotations = true,
        CancellationToken cancellationToken = default, double userUnit = 1)
    {
        if (!double.IsFinite(pixelsPerPoint) || pixelsPerPoint <= 0 || !double.IsFinite(userUnit) || userUnit <= 0) throw new ArgumentOutOfRangeException(nameof(pixelsPerPoint));
        float width, height;
        lock (Gate)
        {
            var page = LoadPage(index);
            try { (width, height) = (Native.FPDF_GetPageWidthF(page), Native.FPDF_GetPageHeightF(page)); }
            finally { Native.FPDF_ClosePage(page); }
        }
        // PDFium dimensions/char boxes use raw PDF user units, not physical points.
        // Caller supplies the page's /UserUnit from the document geometry boundary.
        double w = Math.Ceiling(width * pixelsPerPoint * userUnit), h = Math.Ceiling(height * pixelsPerPoint * userUnit);
        if (w <= 0 || h <= 0 || w * h > MaxBitmapPixels)
            throw new ArgumentOutOfRangeException(nameof(pixelsPerPoint), "Render exceeds the per-request pixel budget.");
        return RenderRegion(index, (int)w, (int)h, 0, new PixelRect(0, 0, (int)w, (int)h), annotations, cancellationToken);
    }

    /// <summary>
    /// Renders <paramref name="region"/> of the page scaled to <paramref name="fullWidth"/> × <paramref name="fullHeight"/>
    /// device pixels after the page's own /Rotate plus <paramref name="extraQuarterTurns"/> clockwise quarter turns.
    /// Only the region's bitmap is allocated, so a high zoom can be rendered tile by tile.
    /// </summary>
    public RenderedPage RenderRegion(int index, int fullWidth, int fullHeight, int extraQuarterTurns, PixelRect region,
        bool annotations = true, CancellationToken cancellationToken = default)
    {
        if (fullWidth <= 0 || fullHeight <= 0) throw new ArgumentOutOfRangeException(nameof(fullWidth));
        if (extraQuarterTurns is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(extraQuarterTurns));
        if (region.Width <= 0 || region.Height <= 0 || region.X < 0 || region.Y < 0 ||
            (long)region.X + region.Width > fullWidth || (long)region.Y + region.Height > fullHeight)
            throw new ArgumentOutOfRangeException(nameof(region));
        if (region.Area > MaxBitmapPixels) throw new ArgumentOutOfRangeException(nameof(region), "Render exceeds the per-request pixel budget.");
        lock (Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = LoadPage(index);
            nint bitmap = 0;
            try
            {
                bitmap = Native.FPDFBitmap_Create(region.Width, region.Height, 1);
                if (bitmap == 0) throw new InvalidOperationException("PDFium bitmap allocation failed.");
                Native.FPDFBitmap_FillRect(bitmap, 0, 0, region.Width, region.Height, 0xffffffff);
                // FPDF_ANNOT = 1. Page's intrinsic /Rotate is honored by PDFium; `rotate` adds to it.
                // The display rectangle may exceed the bitmap; PDFium clips to the bitmap.
                Native.FPDF_RenderPageBitmap(bitmap, page, -region.X, -region.Y, fullWidth, fullHeight, extraQuarterTurns, annotations ? 1 : 0);
                cancellationToken.ThrowIfCancellationRequested(); // synchronous native render cannot be interrupted mid-call
                int stride = Native.FPDFBitmap_GetStride(bitmap);
                var pixels = new byte[checked(stride * region.Height)];
                Marshal.Copy(Native.FPDFBitmap_GetBuffer(bitmap), pixels, 0, pixels.Length);
                return new(region.Width, region.Height, stride, pixels);
            }
            finally
            {
                if (bitmap != 0) Native.FPDFBitmap_Destroy(bitmap);
                Native.FPDF_ClosePage(page);
            }
        }
    }

    public ExtractedCharacter[] ExtractText(int index)
    {
        lock (Gate)
        {
            var page = LoadPage(index);
            nint text = 0;
            try
            {
                text = Native.FPDFText_LoadPage(page);
                if (text == 0) throw new InvalidDataException("PDFium text page failed.");
                var result = new List<ExtractedCharacter>();
                for (int i = 0; i < Native.FPDFText_CountChars(text); i++)
                {
                    uint scalar = Native.FPDFText_GetUnicode(text, i);
                    // ToUnicode supplementary characters may be exposed as a UTF-16 pair by PDFium.
                    if (scalar is >= 0xd800 and <= 0xdbff && i + 1 < Native.FPDFText_CountChars(text))
                    {
                        uint low = Native.FPDFText_GetUnicode(text, i + 1);
                        if (low is >= 0xdc00 and <= 0xdfff)
                        {
                            scalar = 0x10000 + ((scalar - 0xd800) << 10) + low - 0xdc00;
                            // Both parts describe the same encoded glyph; its first box is the position.
                            if (Native.FPDFText_GetCharBox(text, i, out double sl, out double sr, out double sb, out double st) != 0)
                                result.Add(new(i, char.ConvertFromUtf32((int)scalar), [new(sl, st), new(sr, st), new(sl, sb), new(sr, sb)], Angle(text, i)));
                            i++; continue;
                        }
                    }
                    if (scalar == 0 || scalar > 0x10ffff || scalar is >= 0xd800 and <= 0xdfff) continue;
                    if (Native.FPDFText_GetCharBox(text, i, out double l, out double r, out double b, out double t) == 0) continue;
                    // PDF user-space quad. D02 converts it to canonical CropBox points.
                    result.Add(new(i, char.ConvertFromUtf32((int)scalar), [new(l,t), new(r,t), new(l,b), new(r,b)], Angle(text, i)));
                }
                return result.ToArray();
            }
            finally
            {
                if (text != 0) Native.FPDFText_ClosePage(text);
                Native.FPDF_ClosePage(page);
            }
        }
    }

    private static double Angle(nint text, int index)
    {
        float angle = Native.FPDFText_GetCharAngle(text, index);
        return angle >= 0 && float.IsFinite(angle) ? angle : 0;
    }

    public ProbePoint PageToPixel(int index, ProbePoint pdfPoint, int width, int height)
    {
        lock (Gate)
        {
            var page = LoadPage(index);
            try
            {
                if (Native.FPDF_PageToDevice(page, 0, 0, width, height, 0, pdfPoint.X, pdfPoint.Y, out int x, out int y) == 0)
                    throw new InvalidDataException("Coordinate conversion failed.");
                return new(x, y);
            }
            finally { Native.FPDF_ClosePage(page); }
        }
    }

    /// <summary>
    /// Visible box as PDFium uses it (CropBox intersected with MediaBox, raw user units) and /Rotate in degrees.
    /// PDFium ignores /UserUnit; callers combine this with the page's UserUnit (see Reading.PdfPageGeometryReader).
    /// </summary>
    public (double Left, double Bottom, double Right, double Top, int Rotation) GetPageBox(int index)
    {
        lock (Gate)
        {
            var page = LoadPage(index);
            try
            {
                if (Native.FPDF_GetPageBoundingBox(page, out var box) == 0) throw new InvalidDataException("PDFium page box failed.");
                int quarterTurns = Native.FPDFPage_GetRotation(page);
                if (quarterTurns is < 0 or > 3) throw new InvalidDataException("PDFium page rotation failed.");
                return (box.Left, box.Bottom, box.Right, box.Top, quarterTurns * 90);
            }
            finally { Native.FPDF_ClosePage(page); }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(document == 0, this);

    private nint LoadPage(int index)
    {
        ThrowIfDisposed();
        if (index < 0 || index >= PageCount) throw new ArgumentOutOfRangeException(nameof(index));
        nint page = Native.FPDF_LoadPage(document, index);
        return page != 0 ? page : throw new InvalidDataException("PDFium page load failed.");
    }

    public void Dispose() { lock (Gate) Release(); GC.SuppressFinalize(this); }
    ~PdfiumDocument() { lock (Gate) Release(); }
    private void Release()
    {
        bool held = pinned.IsAllocated;
        if (document != 0) Native.FPDF_CloseDocument(document);
        document = 0;
        if (held) pinned.Free();
        if (held && --users == 0) Native.FPDF_DestroyLibrary();
    }

    // Stable C API from official PDFium public headers. Windows x64 only.
    private static class Native
    {
        // FS_RECTF field order from fpdfview.h: left, top, right, bottom.
        [StructLayout(LayoutKind.Sequential)]
        internal struct RectF { public float Left, Top, Right, Bottom; }

        private const string Dll = "pdfium";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_InitLibrary();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_DestroyLibrary();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDF_LoadMemDocument64(nint data, nuint length, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint FPDF_GetLastError();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_CloseDocument(nint doc);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDF_GetPageCount(nint doc);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDF_GetSecurityHandlerRevision(nint doc);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDF_GetSignatureCount(nint doc);
        internal const uint FPDF_REMOVE_SECURITY = 3;
        internal const uint PDFACTION_GOTO = 1, PDFACTION_URI = 3;
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFLink_Enumerate(nint page, ref int startPos, out nint link);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFLink_GetAnnotRect(nint link, out RectF rect);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFLink_GetDest(nint doc, nint link);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFLink_GetAction(nint link);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint FPDFAction_GetType(nint action);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFAction_GetDest(nint doc, nint action);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint FPDFAction_GetURIPath(nint doc, nint action, nint buffer, uint length);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFDest_GetDestPageIndex(nint doc, nint dest);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFDest_GetLocationInPage(nint dest, out int hasX, out int hasY, out int hasZoom, out float x, out float y, out float zoom);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFBookmark_GetFirstChild(nint doc, nint bookmark);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFBookmark_GetNextSibling(nint doc, nint bookmark);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint FPDFBookmark_GetTitle(nint bookmark, nint buffer, uint length);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFBookmark_GetDest(nint doc, nint bookmark);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFBookmark_GetAction(nint bookmark);
        [StructLayout(LayoutKind.Sequential)] internal struct FileWrite { public int Version; public nint WriteBlockCallback; }
        /// <summary>FPDF_FILEWRITE.WriteBlock; <c>unsigned long</c> is 32-bit on Windows.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int WriteBlock(nint self, nint data, uint size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDF_SaveAsCopy(nint doc, ref FileWrite file, uint flags);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDF_LoadPage(nint doc, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_ClosePage(nint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern float FPDF_GetPageWidthF(nint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern float FPDF_GetPageHeightF(nint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDFBitmap_Destroy(nint bitmap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFBitmap_FillRect(nint bitmap, int x, int y, int width, int height, uint color);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int x, int y, int width, int height, int rotate, int flags);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFBitmap_GetStride(nint bitmap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFBitmap_GetBuffer(nint bitmap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFText_LoadPage(nint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDFText_ClosePage(nint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFText_CountChars(nint text);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint FPDFText_GetUnicode(nint text, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern float FPDFText_GetCharAngle(nint text, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFText_GetCharBox(nint text, int index, out double left, out double right, out double bottom, out double top);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDF_GetPageBoundingBox(nint page, out RectF rect);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPage_GetRotation(nint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDF_PageToDevice(nint page, int x, int y, int width, int height, int rotation, double pageX, double pageY, out int deviceX, out int deviceY);
    }
}
