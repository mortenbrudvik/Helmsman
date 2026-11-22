namespace Helmsman.Services.Interfaces;

/// <summary>
/// The file system as the capture file sweep (CaptureFileSweep) sees it: the attributes of one path, the files directly in
/// one folder, and the delete of one file. None of them follows a link: a junction or symbolic link is reported, listed and
/// deleted as the link itself. Separate from the sweep so tests can put a link, a held file or a failure at any step.
/// </summary>
public interface ICaptureFolderFileSystem
{
    /// <summary>
    /// The attributes of the file or folder at <paramref name="path"/> itself (a link's own, with ReparsePoint, never its
    /// target's); null when nothing is there. Throws (IOException, UnauthorizedAccessException, ...) when they cannot be read.
    /// </summary>
    FileAttributes? GetAttributes(string path);

    /// <summary>
    /// The file at <paramref name="path"/> as it is now, in one read: its own attributes and times (a link's, never its
    /// target's); null when no file is there (also for a folder). Throws (IOException, UnauthorizedAccessException, ...) when
    /// it cannot be read.
    /// </summary>
    CaptureFolderEntry? GetFile(string path);

    /// <summary>
    /// The files directly in <paramref name="folder"/>, as they are now: no sub-folder, nothing inside one, and a link to a
    /// file listed as the link. Throws (IOException, UnauthorizedAccessException, ...) when the folder cannot be listed.
    /// </summary>
    IReadOnlyList<CaptureFolderEntry> ListFiles(string folder);

    /// <summary>
    /// Deletes the file at <paramref name="path"/> (a link itself, never what it leads to). Nothing happens when it is gone.
    /// Throws (IOException for a file in use, UnauthorizedAccessException for a read-only one, ...) when it cannot be deleted.
    /// </summary>
    void DeleteFile(string path);
}

/// <summary>A file of a folder as <see cref="ICaptureFolderFileSystem.ListFiles"/> lists it.</summary>
/// <param name="Name">Its name, without the folder.</param>
/// <param name="Attributes">Its own attributes (a link's, with ReparsePoint).</param>
/// <param name="CreationTimeUtc">When it was created.</param>
/// <param name="LastWriteTimeUtc">When it was last written.</param>
public sealed record CaptureFolderEntry(string Name, FileAttributes Attributes, DateTime CreationTimeUtc, DateTime LastWriteTimeUtc);
