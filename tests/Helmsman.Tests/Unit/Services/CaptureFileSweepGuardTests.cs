namespace Helmsman.Tests.Unit.Services;

/// <summary>
/// CaptureFileSweep's guards, step by step, through a fake file system: a link where a file or folder should be (also a file
/// link, which needs a privilege to make for real), a folder replaced by a link between the listing and a delete, and every
/// failure the file system can give. The sweep deletes only by the full path of a listed name under its folder, and never
/// throws. Nothing here touches the disk.
/// </summary>
public sealed class CaptureFileSweepGuardTests
{
    private const string Root = @"C:\Users\me\AppData\Local\Temp\Helmsman";
    private const string Folder = Root + @"\captures";

    private readonly ManualClock _clock = new();
    private readonly FakeFileSystem _files = new();

    public CaptureFileSweepGuardTests()
    {
        _files.Attributes[Root] = FileAttributes.Directory;
        _files.Attributes[Folder] = FileAttributes.Directory;
    }

    private CaptureFileSweep CreateSut() => new(_files, Folder, Root, _clock);

    /// <summary>Adds a file of the writer's name to the folder, old enough to be deleted, and returns its full path.</summary>
    private string AddOldFile(FileAttributes attributes = FileAttributes.Archive)
    {
        var name = CaptureFileWriter.NewFileName(DateTime.Now, Guid.NewGuid(), ImageEncoding.Png);
        var old = _clock.GetUtcNow().UtcDateTime - CaptureFileSweep.Retention - TimeSpan.FromMinutes(1);
        _files.Entries.Add(new CaptureFolderEntry(name, attributes, old, old));
        var path = Path.Combine(Folder, name);
        _files.Attributes[path] = attributes;
        return path;
    }

    [Fact]
    public void Sweep_DeletesAnOldFileOfTheWritersName_ByItsFullPathInTheFolder()
    {
        // Arrange
        var path = AddOldFile();

        // Act
        var deleted = CreateSut().Sweep();

        // Assert
        deleted.Should().Be(1);
        _files.Deleted.Should().Equal(path);
    }

    [Fact]
    public void Sweep_KeepsAFileJustInsideTheRetention()
    {
        // Arrange
        var name = CaptureFileWriter.NewFileName(DateTime.Now, Guid.NewGuid(), ImageEncoding.Jpeg);
        var young = _clock.GetUtcNow().UtcDateTime - CaptureFileSweep.Retention + TimeSpan.FromSeconds(1);
        _files.Entries.Add(new CaptureFolderEntry(name, FileAttributes.Archive, young, young));
        _files.Attributes[Path.Combine(Folder, name)] = FileAttributes.Archive;

        // Act
        CreateSut().Sweep();

        // Assert
        _files.Deleted.Should().BeEmpty();
    }

    [Theory]
    [InlineData(FileAttributes.ReparsePoint)] // a symbolic link of the writer's name
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Archive)]
    [InlineData(FileAttributes.Directory)]
    [InlineData(FileAttributes.ReadOnly)]
    [InlineData(FileAttributes.Hidden)]
    [InlineData(FileAttributes.System)]
    public void Sweep_LeavesAFileListedAsALinkOrMarkedToBeKept(FileAttributes attributes)
    {
        // Arrange
        AddOldFile(attributes);

        // Act
        CreateSut().Sweep();

        // Assert
        _files.Deleted.Should().BeEmpty();
    }

    [Fact]
    public void Sweep_LeavesAFileThatBecameALinkAfterTheListing()
    {
        // Arrange - listed as a plain file, a symbolic link by the time of its delete
        var path = AddOldFile();
        _files.Attributes[path] = FileAttributes.ReparsePoint | FileAttributes.Archive;

        // Act
        CreateSut().Sweep();

        // Assert
        _files.Deleted.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Folder)]
    [InlineData(Root)]
    public void Sweep_WhenTheFolderOrTheServersTempFolderIsALink_ListsAndDeletesNothing(string link)
    {
        // Arrange
        AddOldFile();
        _files.Attributes[link] = FileAttributes.Directory | FileAttributes.ReparsePoint;

        // Act
        var deleted = CreateSut().Sweep();

        // Assert
        deleted.Should().Be(0);
        _files.ListCalls.Should().Be(0, "a folder behind a link is not even listed");
        _files.Deleted.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Folder)]
    [InlineData(Root)]
    public void Sweep_WhenAFolderIsReplacedByALinkAfterTheListing_StopsBeforeTheNextDelete(string link)
    {
        // Arrange - the first delete goes through; then the folder (or the one above it) becomes a link
        AddOldFile();
        AddOldFile();
        AddOldFile();
        _files.AfterDelete = _ => _files.Attributes[link] = FileAttributes.Directory | FileAttributes.ReparsePoint;

        // Act
        var deleted = CreateSut().Sweep();

        // Assert
        deleted.Should().Be(1);
        _files.Deleted.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(Folder)]
    [InlineData(Root)]
    public void Sweep_WhenAFolderIsGoneOrIsAFile_DoesNothing(string path)
    {
        // Arrange
        AddOldFile();
        _files.Attributes.Remove(path);

        // Act
        var deletedWhenGone = CreateSut().Sweep();
        _files.Attributes[path] = FileAttributes.Archive;
        var deletedWhenAFile = CreateSut().Sweep();

        // Assert
        deletedWhenGone.Should().Be(0);
        deletedWhenAFile.Should().Be(0);
        _files.Deleted.Should().BeEmpty();
    }

    public static TheoryData<Exception> FileSystemFailures() => new()
    {
        new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020)),
        new UnauthorizedAccessException("Access to the path is denied."),
        new FileNotFoundException("gone"),
        new DirectoryNotFoundException("gone"),
        new PathTooLongException(),
        new IOException("The device is not ready.")
    };

    [Theory]
    [MemberData(nameof(FileSystemFailures))]
    public void Sweep_WhenADeleteFails_SkipsThatFile_AndDeletesTheRest(Exception failure)
    {
        // Arrange
        var failing = AddOldFile();
        var other = AddOldFile();
        _files.DeleteFailure = path => path == failing ? failure : null;

        // Act
        var act = () => CreateSut().Sweep();

        // Assert
        act.Should().NotThrow().Which.Should().Be(1);
        _files.Deleted.Should().Equal(other);
    }

    [Theory]
    [MemberData(nameof(FileSystemFailures))]
    public void Sweep_WhenAFilesAttributesCannotBeRead_SkipsThatFile(Exception failure)
    {
        // Arrange
        var failing = AddOldFile();
        var other = AddOldFile();
        _files.AttributesFailure = path => path == failing ? failure : null;

        // Act
        var act = () => CreateSut().Sweep();

        // Assert
        act.Should().NotThrow();
        _files.Deleted.Should().Equal(other);
    }

    [Theory]
    [MemberData(nameof(FileSystemFailures))]
    public void Sweep_WhenTheFolderCannotBeListedOrChecked_DeletesNothing_AndDoesNotThrow(Exception failure)
    {
        // Arrange
        AddOldFile();
        _files.ListFailure = failure;
        var listing = CreateSut();
        var checking = new CaptureFileSweep(new FakeFileSystem { AttributesFailure = _ => failure }, Folder, Root, _clock);

        // Act
        var whenListed = () => listing.Sweep();
        var whenChecked = () => checking.Sweep();

        // Assert
        whenListed.Should().NotThrow().Which.Should().Be(0);
        whenChecked.Should().NotThrow().Which.Should().Be(0);
        _files.Deleted.Should().BeEmpty();
    }

    [Fact]
    public void Sweep_DeletesOnlyNamesTheWriterGives_SoNoDeleteLeavesTheFolder()
    {
        // Arrange - listed names that would lead elsewhere if they were joined to the folder (a fake can list anything)
        var old = _clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(2);
        foreach (var name in new[] { @"..\layouts.json", @"..\..\" + CaptureFileWriter.NewFileName(DateTime.Now, Guid.NewGuid(), ImageEncoding.Png),
                     @"C:\Windows\" + CaptureFileWriter.NewFileName(DateTime.Now, Guid.NewGuid(), ImageEncoding.Png), "notes.txt" })
        {
            _files.Entries.Add(new CaptureFolderEntry(name, FileAttributes.Archive, old, old));
        }

        var own = AddOldFile();

        // Act
        CreateSut().Sweep();

        // Assert
        _files.Deleted.Should().Equal(own);
        _files.Deleted.Should().OnlyContain(path => Path.GetDirectoryName(path) == Folder);
    }

    /// <summary>A file system of attributes and one folder's listing, with failures and a hook after each delete.</summary>
    private sealed class FakeFileSystem : ICaptureFolderFileSystem
    {
        public Dictionary<string, FileAttributes> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<CaptureFolderEntry> Entries { get; } = [];

        public List<string> Deleted { get; } = [];

        public int ListCalls { get; private set; }

        public Exception? ListFailure { get; set; }

        public Func<string, Exception?>? AttributesFailure { get; set; }

        public Func<string, Exception?>? DeleteFailure { get; set; }

        public Action<string>? AfterDelete { get; set; }

        public FileAttributes? GetAttributes(string path)
        {
            if (AttributesFailure?.Invoke(path) is { } failure)
            {
                throw failure;
            }

            return Attributes.TryGetValue(path, out var attributes) ? attributes : null;
        }

        public IReadOnlyList<CaptureFolderEntry> ListFiles(string folder)
        {
            ListCalls++;
            if (ListFailure != null)
            {
                throw ListFailure;
            }

            folder.Should().Be(Folder, "the sweep lists its own folder only");
            return Entries.ToList();
        }

        public void DeleteFile(string path)
        {
            if (DeleteFailure?.Invoke(path) is { } failure)
            {
                throw failure;
            }

            Deleted.Add(path);
            Attributes.Remove(path);
            AfterDelete?.Invoke(path);
        }
    }
}
