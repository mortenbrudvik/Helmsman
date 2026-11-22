using System.Globalization;
using WinDesktopMcp.Models;

namespace WinDesktopMcp.Services;

/// <summary>
/// Writes capture images to files: to the path a capture asks for, or to a new file in <see cref="DefaultCaptureFolder"/>.
/// </summary>
public static class CaptureFileWriter
{
    /// <summary>Where captures go when output writes a file and no outputPath is given.</summary>
    public static readonly string DefaultCaptureFolder =
        Path.Combine(Path.GetTempPath(), LayoutStorageOptions.AppDataFolderName, "captures");

    /// <summary>
    /// Writes the encoded image to the requested path, or to a new file in
    /// <see cref="DefaultCaptureFolder"/>. An existing file is replaced only with overwrite; the
    /// folder of an explicit path must already exist.
    /// </summary>
    internal static string WriteFile(byte[] data, ImageEncoding encoding, CaptureRequest request)
    {
        var path = request.OutputPath;
        if (path == null)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            path = Path.Combine(DefaultCaptureFolder, $"screenshot_{timestamp}_{Guid.NewGuid():N}{CaptureRequest.Extension(encoding)}");
        }

        var mode = request.Overwrite && request.OutputPath != null ? FileMode.Create : FileMode.CreateNew;
        try
        {
            if (request.OutputPath == null)
            {
                // The server's own folder; explicit paths must name an existing folder.
                Directory.CreateDirectory(DefaultCaptureFolder);
            }

            using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.None);
            stream.Write(data);
        }
        catch (IOException) when (mode == FileMode.CreateNew && File.Exists(path))
        {
            // Created between the check in CaptureRequest and this write.
            throw CaptureRequest.FileExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CaptureException(ScreenshotErrorCode.FileWriteFailed, $"Could not write '{path}': {ex.Message}");
        }

        return path;
    }
}
