using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Helmsman.Services.Interfaces;

namespace Helmsman.Services;

/// <summary>
/// Deletes the server's own capture files from its default captures folder (%TEMP%\Helmsman\captures, where a capture
/// written to a file without outputPath goes) once they are older than <see cref="Retention"/>, so the folder does not
/// grow without bound. <see cref="CaptureFileSweepStartup"/> runs it in the background, never inside a tool call.
/// </summary>
/// <remarks>
/// <para>
/// What it deletes, and nothing else: a regular file directly in <see cref="Folder"/> whose name
/// <see cref="CaptureFileWriter.IsOwnFileName"/> recognises as one the writer gives, last created or written more than
/// <see cref="Retention"/> ago. A file of another name or extension, a sub-folder and what is in it, a link (junction or
/// symbolic link), and a file marked read-only, hidden or system stay. Nothing outside the folder is touched: not a
/// caller's outputPath, not the other folders of %TEMP%\Helmsman, not the folders of the server's earlier names. The name
/// is all that tells the server's files apart, so a file a caller wrote into this folder under such a name (an outputPath
/// that copies the writer's naming) counts as the server's.
/// </para>
/// <para>
/// %TEMP% belongs to the user, so the folder, or %TEMP%\Helmsman above it, could be a junction or symbolic link to
/// another folder. Before it lists the folder, and again just before each delete, the sweep checks
/// <see cref="Folder"/> and every folder above it up to <see cref="ServerTempRoot"/>, and stops when one of them is a
/// link or is gone; it deletes only by the full path of a listed name under <see cref="Folder"/>. A folder replaced by a
/// link in the moment between that check and the delete would let one delete go through it, to a file of the writer's
/// exact name; whoever can replace folders in the user's %TEMP% already runs as the user and can delete such a file
/// directly. %TEMP% itself and the folders above it are the user's (some move them with a junction) and are not checked.
/// </para>
/// <para>
/// Several servers (one per client session) share the folder and each sweeps it: a file another server wrote is
/// <see cref="Retention"/> away from being deleted, and two sweeps that meet at a file both carry on. A file that cannot be
/// deleted now (in use, read-only, gone, any I/O error) is left for a later sweep and logged at Debug only.
/// </para>
/// </remarks>
public sealed class CaptureFileSweep
{
    /// <summary>
    /// How long the server keeps a capture file it wrote to its default folder, after it was last written. A caller gets the
    /// path in the capture's result and reads the file within its task, usually seconds later; a day leaves room for a session
    /// that is left overnight and resumed, while the folder holds at most a day of captures (and an hour more, see
    /// <see cref="Interval"/>). To keep a capture, pass an outputPath outside this folder: those files are never deleted.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>
    /// How long after the server starts it sweeps first: not while the client is still connecting, and a server that only
    /// starts to answer a list of its tools sweeps nothing.
    /// </summary>
    public static readonly TimeSpan FirstSweepDelay = TimeSpan.FromMinutes(1);

    /// <summary>How often a running server sweeps again, so a file is deleted at most this long after its retention ends.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>The attributes of a file the sweep leaves alone: the writer never gives a file any of them.</summary>
    private const FileAttributes KeptAttributes = FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device |
                                                  FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;

    private readonly ICaptureFolderFileSystem _files;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <param name="files">The file system.</param>
    /// <param name="folder">The folder swept: the server's default captures folder.</param>
    /// <param name="serverTempRoot">The server's own folder that holds it; it and every folder between are checked for links.</param>
    /// <param name="clock">The clock the files' ages are measured with; default the system clock.</param>
    /// <param name="logger">Gets what was deleted, and what was not and why, at Debug.</param>
    /// <exception cref="ArgumentException"><paramref name="folder"/> is not inside <paramref name="serverTempRoot"/>.</exception>
    public CaptureFileSweep(
        ICaptureFolderFileSystem files,
        string folder,
        string serverTempRoot,
        TimeProvider? clock = null,
        ILogger<CaptureFileSweep>? logger = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        Folder = DebugBrowserProfiles.Normalize(folder ?? throw new ArgumentNullException(nameof(folder)));
        ServerTempRoot = DebugBrowserProfiles.Normalize(serverTempRoot ?? throw new ArgumentNullException(nameof(serverTempRoot)));
        if (!Folder.StartsWith(ServerTempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The captures folder '{Folder}' is not inside '{ServerTempRoot}'.", nameof(folder));
        }

        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>The server's: %TEMP%\Helmsman\captures under %TEMP%\Helmsman (<see cref="CaptureFileWriter"/>).</summary>
    public static CaptureFileSweep ForDefaultFolder(ICaptureFolderFileSystem files, ILogger<CaptureFileSweep>? logger = null) =>
        new(files, CaptureFileWriter.DefaultCaptureFolder, CaptureFileWriter.ServerTempRoot, TimeProvider.System, logger);

    /// <summary>The folder swept, as a full path.</summary>
    public string Folder { get; }

    /// <summary>The server's own folder above <see cref="Folder"/>, as a full path.</summary>
    public string ServerTempRoot { get; }

    /// <summary>
    /// Deletes the server's capture files older than <see cref="Retention"/> and returns how many. Never throws for the
    /// file system: what cannot be read or deleted now is left for the next sweep.
    /// </summary>
    public int Sweep()
    {
        try
        {
            if (Redirection() is { } reason)
            {
                _logger.LogDebug("The capture files are not swept: {Reason}", reason);
                return 0;
            }

            var before = _clock.GetUtcNow().UtcDateTime - Retention;
            var deleted = 0;
            foreach (var entry in _files.ListFiles(Folder))
            {
                if (!IsExpiredOwnFile(entry, before))
                {
                    continue;
                }

                // The name is one the writer gives, so it names a file directly in the folder and nothing else
                var path = Path.Combine(Folder, entry.Name);
                try
                {
                    // Again just before the delete, so a folder replaced by a link since the listing is not followed
                    if (Redirection() is { } since)
                    {
                        _logger.LogDebug("The capture file sweep stopped: {Reason}", since);
                        break;
                    }

                    if (_files.GetAttributes(path) is not { } attributes || (attributes & KeptAttributes) != 0)
                    {
                        continue;
                    }

                    _files.DeleteFile(path);
                    deleted++;
                }
                catch (Exception ex) when (IsFileSystemFailure(ex))
                {
                    _logger.LogDebug(ex, "The capture file '{File}' was not deleted; a later sweep tries again", path);
                }
            }

            if (deleted > 0)
            {
                _logger.LogDebug("Deleted {Count} capture files older than {Hours} hours from '{Folder}'", deleted, Retention.TotalHours, Folder);
            }

            return deleted;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            _logger.LogDebug(ex, "The capture files in '{Folder}' could not be swept; a later sweep tries again", Folder);
            return 0;
        }
    }

    /// <summary>Whether the listed <paramref name="entry"/> is a plain file the writer named, last created or written before <paramref name="before"/>.</summary>
    private static bool IsExpiredOwnFile(CaptureFolderEntry entry, DateTime before) =>
        CaptureFileWriter.IsOwnFileName(entry.Name) &&
        (entry.Attributes & KeptAttributes) == 0 &&
        entry.CreationTimeUtc < before && entry.LastWriteTimeUtc < before;

    /// <summary>
    /// Why the folder may not be swept now, or null: it, or a folder above it up to <see cref="ServerTempRoot"/>, is gone, is
    /// not a folder, or is a link (junction or symbolic link) that could lead anywhere.
    /// </summary>
    private string? Redirection()
    {
        for (var path = Folder; ; path = Path.GetDirectoryName(path)!)
        {
            var attributes = _files.GetAttributes(path);
            if (attributes == null)
            {
                return $"'{path}' does not exist.";
            }

            if (!attributes.Value.HasFlag(FileAttributes.Directory))
            {
                return $"'{path}' is not a folder.";
            }

            if (attributes.Value.HasFlag(FileAttributes.ReparsePoint))
            {
                return $"'{path}' is a link (junction or symbolic link) to another folder, so nothing under it is deleted.";
            }

            if (string.Equals(path, ServerTempRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
    }

    private static bool IsFileSystemFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;
}
