namespace Helmsman.Models;

/// <summary>
/// The size limits of every image the server makes of the screen, a window or a browser page, in one place. Two kinds:
/// the pixel budget of one capture, which holds the pixels in memory, any file and any intermediate bitmap; and the
/// smaller inline bound of an image returned in a tool result, which goes into the client's context. A call over either is
/// refused with its error code, before the pixels are read where their size is known first (a monitor, a region, the
/// virtual screen), otherwise before the image is made or, for the encoded size, before it is returned or written. Nothing
/// is cropped or scaled to fit: imageToScreen and captureId always describe the image as asked for.
/// </summary>
public static class CaptureLimits
{
    /// <summary>
    /// The longest edge of one capture, in pixels: GDI's limit for a bitmap the server reads the screen into, and Direct3D
    /// 11's largest texture, which a Windows.Graphics.Capture frame is.
    /// </summary>
    public const int MaxCaptureEdge = 16384;

    /// <summary>
    /// The pixel budget of one capture: 64 megapixels (for example 8192 x 8192), 256 MB of 32-bit pixels. It holds the
    /// pixels read, any image made of them (a zoom enlarges up to it), every file written and every bitmap in between, so a
    /// capture can never take gigabytes of the user's memory.
    /// </summary>
    public const long MaxCapturePixels = 64L * 1024 * 1024;

    /// <summary>
    /// The longest edge of an image returned in a tool result, in pixels: the Claude API refuses an image over 8000 pixels on
    /// an edge (other vision models take less), and an image the client cannot hand its model fails every later request of
    /// the conversation that holds it. It is still more than an 8K display's 7680.
    /// </summary>
    public const int MaxInlineEdge = 8000;

    /// <summary>
    /// The most pixels of an image returned in a tool result: 32 megapixels (33,554,432), so an 8K display (7680 x 4320,
    /// 33,177,600) at full resolution still fits, while several monitors at full resolution belong in a file. Its bitmap
    /// takes at most 128 MB while it is encoded.
    /// </summary>
    public const long MaxInlinePixels = 32L * 1024 * 1024;

    /// <summary>
    /// The most bytes of an encoded image returned in a tool result: 10 MB (10,485,760), at most about 14 MB once base64
    /// encodes it into the result. No image at the default size can reach it, whatever it shows: a PNG of pure noise at
    /// 1568 x 1568 (the default long edge) is 8.4 MB, a JPEG of it at quality 100 4.9 MB. Only a larger maxDimension, or
    /// maxWidth alone, makes an image that can.
    /// </summary>
    public const long MaxInlineBytes = 10L * 1024 * 1024;

    /// <summary>The pixel budget as a reader sees it: "64 megapixels, 16384 pixels on an edge".</summary>
    public static string CaptureBudgetText => $"{MaxCapturePixels / (1024 * 1024)} megapixels, {MaxCaptureEdge} pixels on an edge";

    /// <summary>The inline pixel bound as a reader sees it: "8000 pixels on an edge, 32 megapixels".</summary>
    public static string InlineBoundText => $"{MaxInlineEdge} pixels on an edge, {MaxInlinePixels / (1024 * 1024)} megapixels";

    /// <summary>The inline byte bound as a reader sees it: "10 MB".</summary>
    public static string InlineBytesText => $"{MaxInlineBytes / (1024 * 1024)} MB";

    /// <summary>True when a <paramref name="width"/> x <paramref name="height"/> capture is within the pixel budget.</summary>
    public static bool WithinCaptureBudget(long width, long height) =>
        width <= MaxCaptureEdge && height <= MaxCaptureEdge && width * height <= MaxCapturePixels;

    /// <summary>True when a <paramref name="width"/> x <paramref name="height"/> image may be returned in a tool result.</summary>
    public static bool FitsInline(long width, long height) =>
        width <= MaxInlineEdge && height <= MaxInlineEdge && width * height <= MaxInlinePixels;
}
