using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinDesktopMcp.Services.Interfaces;

namespace WinDesktopMcp.Services;

/// <summary>
/// The bitmap work of a capture: cropping captured pixels, a bitmap over them, scaling and encoding. The screenshot service
/// and the browser tab capture share it.
/// </summary>
internal static class CaptureImaging
{
    /// <summary>The part of <paramref name="capture"/> inside <paramref name="screenRectangle"/>, which must lie inside it.</summary>
    internal static CaptureResult Crop(CaptureResult capture, Rectangle screenRectangle)
    {
        var left = screenRectangle.X - capture.ScreenOriginX;
        var top = screenRectangle.Y - capture.ScreenOriginY;
        var rowBytes = screenRectangle.Width * 4;
        var pixels = GC.AllocateUninitializedArray<byte>(rowBytes * screenRectangle.Height);
        for (var row = 0; row < screenRectangle.Height; row++)
        {
            Buffer.BlockCopy(capture.Bgra, ((top + row) * capture.Width + left) * 4, pixels, row * rowBytes, rowBytes);
        }

        return capture with
        {
            Bgra = pixels,
            Width = screenRectangle.Width,
            Height = screenRectangle.Height,
            ScreenOriginX = screenRectangle.X,
            ScreenOriginY = screenRectangle.Y
        };
    }

    /// <summary>
    /// Runs <paramref name="use"/> on a bitmap over the captured pixels themselves, without copying them: the pixel
    /// array is pinned while the bitmap exists. The bitmap ignores the 4th byte (Format32bppRgb): GDI leaves that byte
    /// undefined (often 0), so reading it as alpha would make parts of a PNG transparent. Overlays drawn on the bitmap
    /// change <see cref="CaptureResult.Bgra"/>.
    /// </summary>
    /// <remarks>
    /// This method owns the pin and the bitmap: both are released here when <paramref name="use"/> returns or throws, so
    /// <paramref name="use"/> (the screenshot service's rendering) must not keep the bitmap.
    /// </remarks>
    internal static T WithBitmap<T>(CaptureResult capture, Func<Bitmap, T> use)
    {
        var pinned = GCHandle.Alloc(capture.Bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(capture.Width, capture.Height, capture.Width * 4, PixelFormat.Format32bppRgb,
                pinned.AddrOfPinnedObject());
            return use(bitmap);
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <remarks>
    /// The caller owns the returned bitmap and disposes it (the screenshot service's rendering and the browser tab capture
    /// do, with using); it is disposed here only when it cannot be returned.
    /// </remarks>
    internal static Bitmap ResizeImage(Bitmap original, int width, int height)
    {
        var resized = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(resized);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;

            // Without TileFlipXY the bicubic filter samples outside the source and darkens the edges.
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(
                original,
                new Rectangle(0, 0, width, height),
                0, 0, original.Width, original.Height,
                GraphicsUnit.Pixel,
                attributes);
            return resized;
        }
        catch
        {
            resized.Dispose();
            throw;
        }
    }

    internal static byte[] Encode(Bitmap bitmap, ImageEncoding encoding, int quality)
    {
        using var stream = new MemoryStream();

        if (encoding == ImageEncoding.Png)
        {
            bitmap.Save(stream, ImageFormat.Png);
        }
        else
        {
            var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var encoderParameters = new EncoderParameters(1);
            encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
            bitmap.Save(stream, encoder, encoderParameters);
        }

        return stream.ToArray();
    }
}
