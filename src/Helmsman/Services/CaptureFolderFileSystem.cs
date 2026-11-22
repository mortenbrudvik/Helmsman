using Helmsman.Services.Interfaces;

namespace Helmsman.Services;

/// <summary>
/// <see cref="ICaptureFolderFileSystem"/> on the real file system. File.GetAttributes reads a link's own attributes, the
/// listing does not recurse and lists a link to a file as a file with ReparsePoint (a junction or a link to a folder is a
/// folder, which it leaves out), and File.Delete removes a link itself.
/// </summary>
public sealed class CaptureFolderFileSystem : ICaptureFolderFileSystem
{
    private static readonly EnumerationOptions FilesDirectlyInTheFolder = new()
    {
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        // Every file is listed, hidden and system ones too: the sweep decides what it keeps
        AttributesToSkip = 0,
        MatchType = MatchType.Simple,
        IgnoreInaccessible = true
    };

    public FileAttributes? GetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public IReadOnlyList<CaptureFolderEntry> ListFiles(string folder) =>
        new DirectoryInfo(folder).EnumerateFiles("*", FilesDirectlyInTheFolder)
            .Select(file => new CaptureFolderEntry(file.Name, file.Attributes, file.CreationTimeUtc, file.LastWriteTimeUtc))
            .ToList();

    public void DeleteFile(string path) => File.Delete(path);
}
