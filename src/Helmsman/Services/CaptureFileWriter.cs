using System.Globalization;
using Helmsman.Models;

namespace Helmsman.Services;

/// <summary>
/// Writes capture images to files: to the path a capture asks for, or to a new file in <see cref="DefaultCaptureFolder"/>,
/// named by <see cref="NewFileName"/>. <see cref="CaptureFileSweep"/> deletes the files of that folder whose names
/// <see cref="IsOwnFileName"/> recognises once they are old.
/// </summary>
public static class CaptureFileWriter
{
    /// <summary>The server's own folder under %TEMP%: %TEMP%\Helmsman.</summary>
    public static readonly string ServerTempRoot = Path.Combine(Path.GetTempPath(), LayoutStorageOptions.AppDataFolderName);

    /// <summary>Where captures go when output writes a file and no outputPath is given.</summary>
    public static readonly string DefaultCaptureFolder = Path.Combine(ServerTempRoot, "captures");

    private const string FileNamePrefix = "screenshot_";
    private const string TimestampFormat = "yyyyMMdd_HHmmss";
    private const string IdFormat = "N";

    /// <summary>
    /// The name of a new file in <see cref="DefaultCaptureFolder"/>: screenshot_, the local time of the write, an underscore,
    /// the 32 lowercase hex digits of <paramref name="id"/>, and the encoding's extension (screenshot_20261122_153000_
    /// 0123456789abcdef0123456789abcdef.png).
    /// </summary>
    public static string NewFileName(DateTime timestamp, Guid id, ImageEncoding encoding) =>
        FileNamePrefix + timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture) + "_" + id.ToString(IdFormat) +
        CaptureRequest.Extension(encoding);

    /// <summary>
    /// Whether <paramref name="name"/> is a name <see cref="NewFileName"/> gives, exactly: it is read back into a time, an
    /// id and an encoding, and must be the name those make again. Anything else, a near miss or another case included, is not.
    /// </summary>
    public static bool IsOwnFileName(string? name)
    {
        if (name == null || !name.StartsWith(FileNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var encoding in Enum.GetValues<ImageEncoding>())
        {
            var extension = CaptureRequest.Extension(encoding);
            // Between the prefix and the extension: the time, an underscore and the id's 32 digits
            var middle = name.Length - FileNamePrefix.Length - extension.Length;
            if (middle != TimestampFormat.Length + 1 + 32 || !name.EndsWith(extension, StringComparison.Ordinal))
            {
                continue;
            }

            var timestamp = name.Substring(FileNamePrefix.Length, TimestampFormat.Length);
            var id = name.Substring(FileNamePrefix.Length + TimestampFormat.Length + 1, 32);
            if (DateTime.TryParseExact(timestamp, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) &&
                Guid.TryParseExact(id, IdFormat, out var guid) &&
                string.Equals(name, NewFileName(time, guid, encoding), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

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
            path = Path.Combine(DefaultCaptureFolder, NewFileName(DateTime.Now, Guid.NewGuid(), encoding));
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
