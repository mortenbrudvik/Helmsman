using System.Globalization;

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
    /// The longest edge of an image returned in a tool result, in pixels. Anthropic's vision documentation
    /// (https://platform.claude.com/docs/en/build-with-claude/vision, "Image limits and costs", "Request limits"): "The
    /// maximum dimensions for each image are 8000x8000 px." An image the client cannot hand its model fails every later
    /// request of the conversation that holds it. It is still more than an 8K display's 7680. The same page adds a stricter limit when one request holds
    /// more than 20 images ("resize each image so that neither dimension exceeds 3,000 px"), which a server cannot know of:
    /// the default size (1568) is within it.
    /// </summary>
    public const int MaxInlineEdge = 8000;

    /// <summary>
    /// The most pixels of an image returned in a tool result: 32 megapixels (33,554,432), so an 8K display (7680 x 4320,
    /// 33,177,600) at full resolution still fits, while several monitors at full resolution belong in a file. Its bitmap
    /// takes at most 128 MB while it is encoded.
    /// </summary>
    public const long MaxInlinePixels = 32L * 1024 * 1024;

    /// <summary>
    /// The most characters of base64 an image returned in a tool result may take, 10,000,000. Anthropic's vision
    /// documentation (https://platform.claude.com/docs/en/build-with-claude/vision, "Image limits and costs", "Request
    /// limits"): "The maximum size per image is: 10 MB (base64-encoded) on the Claude API and Claude Models on Google Cloud
    /// (Google managed). 5 MB (base64-encoded) on Amazon Bedrock." The limit is on the base64 text the result carries, not on the encoded file, and
    /// "10 MB" is read as 10,000,000 (the smaller of 10^7 and 2^20 x 10). Base64 takes 4 characters for every 3 bytes, so the
    /// encoded image may be at most <see cref="MaxInlineEncodedBytes"/>, 7,500,000 bytes. With the default format (JPEG at
    /// quality 80, PNG only up to a 512-pixel long edge) no image at the default size (1568) can reach it, whatever it shows:
    /// a JPEG of pure noise at 1568 x 1568 is 1,606,112 bytes (2,141,484 base64), 4,860,317 at quality 100, and a PNG of noise
    /// at 512 x 512 897,140. An explicit PNG can: one of noise at 1568 x 1568 is 8,410,435 bytes (11,213,916 base64).
    /// Bedrock's 5 MB is not held to: a client there takes less.
    /// </summary>
    public const long MaxInlineBase64Length = 10_000_000;

    /// <summary>
    /// The most bytes an encoded image returned in a tool result may have: the largest n whose base64 length,
    /// 4 x ceil(n / 3), is at most <see cref="MaxInlineBase64Length"/>: 10,000,000 / 4 x 3 = 7,500,000.
    /// </summary>
    public const long MaxInlineEncodedBytes = MaxInlineBase64Length / 4 * 3;

    /// <summary>The pixel budget as a reader sees it: "64 megapixels, 16384 pixels on an edge".</summary>
    public static string CaptureBudgetText => $"{MaxCapturePixels / (1024 * 1024)} megapixels, {MaxCaptureEdge} pixels on an edge";

    /// <summary>The inline pixel bound as a reader sees it: "8000 pixels on an edge, 32 megapixels".</summary>
    public static string InlineBoundText => $"{MaxInlineEdge} pixels on an edge, {MaxInlinePixels / (1024 * 1024)} megapixels";

    /// <summary>The inline size bound as a reader sees it: "10,000,000 characters of base64".</summary>
    public static string InlineBase64Text => $"{MaxInlineBase64Length.ToString("N0", CultureInfo.InvariantCulture)} characters of base64";

    /// <summary>The length of <paramref name="byteCount"/> bytes in base64, with padding: 4 x ceil(n / 3).</summary>
    public static long Base64Length(long byteCount) => 4 * ((byteCount + 2) / 3);

    /// <summary>
    /// The largest maxDimension, 1 to <see cref="MaxInlineEdge"/>, whose image fits the inline pixel bound, by the caller's
    /// own sizing (<paramref name="outputSizeFor"/>: the image's size at a maxDimension, with every other limit and its
    /// rounding as the capture applies them), so the value suggested, fed back in, passes. A binary search: a larger
    /// maxDimension never makes an image smaller. The sizing must keep the image's long edge within the maxDimension, as the
    /// capture tools' OutputSize and browser_screenshot's ScaledSize do, so every maxDimension up to 5792 fits (5792 x 5792
    /// is 33,547,264 pixels) and there is always an answer: the search starts from 1, which fits.
    /// </summary>
    public static int LargestFittingMaxDimension(Func<int, (int Width, int Height)> outputSizeFor)
    {
        ArgumentNullException.ThrowIfNull(outputSizeFor);
        bool Fits(int maxDimension)
        {
            var (width, height) = outputSizeFor(maxDimension);
            return FitsInline(width, height);
        }

        int low = 1, high = MaxInlineEdge;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (Fits(middle))
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    /// <summary>True when an encoded image of <paramref name="byteCount"/> bytes may be returned in a tool result.</summary>
    public static bool FitsInlineBase64(long byteCount) => Base64Length(byteCount) <= MaxInlineBase64Length;

    /// <summary>True when a <paramref name="width"/> x <paramref name="height"/> capture is within the pixel budget.</summary>
    public static bool WithinCaptureBudget(long width, long height) =>
        width <= MaxCaptureEdge && height <= MaxCaptureEdge && width * height <= MaxCapturePixels;

    /// <summary>True when a <paramref name="width"/> x <paramref name="height"/> image may be returned in a tool result.</summary>
    public static bool FitsInline(long width, long height) =>
        width <= MaxInlineEdge && height <= MaxInlineEdge && width * height <= MaxInlinePixels;
}
