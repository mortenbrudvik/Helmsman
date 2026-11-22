using System.Diagnostics;

namespace Helmsman.Tests.Unit.Services;

/// <summary>
/// CaptureFileSweep on the real file system: which files of the default captures folder it deletes (the writer's names,
/// older than the retention) and everything it leaves alone (other names, sub-folders, links, held and read-only files,
/// a caller's outputPath, the server's other folders and those of its earlier names). Each test works in a folder of its
/// own under %TEMP% (a stand-in %TEMP% inside it holds Helmsman\captures), never in the real %TEMP%\Helmsman; junctions
/// are made with mklink /J, which needs no privilege. CaptureFileSweepGuardTests puts links and failures at each step
/// through a fake file system.
/// </summary>
public sealed class CaptureFileSweepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"helmsman-capture-sweep-{Guid.NewGuid():N}");
    private readonly string _temp;
    private readonly string _serverRoot;
    private readonly string _captures;
    private readonly List<string> _links = [];

    /// <summary>A day and an hour from now: every file the test makes is past the retention unless it says otherwise.</summary>
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow + CaptureFileSweep.Retention + TimeSpan.FromHours(1));

    public CaptureFileSweepTests()
    {
        _temp = Path.Combine(_root, "temp");
        _serverRoot = Path.Combine(_temp, LayoutStorageOptions.AppDataFolderName);
        _captures = Path.Combine(_serverRoot, "captures");
        Directory.CreateDirectory(_captures);
    }

    public void Dispose()
    {
        // The links first, as links: deleting the folder after them never reaches what they lead to
        foreach (var link in _links)
        {
            try
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }
                else
                {
                    File.Delete(link);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private CaptureFileSweep CreateSut() => new(new CaptureFolderFileSystem(), _captures, _serverRoot, _clock);

    private static string OwnName(ImageEncoding encoding = ImageEncoding.Png) =>
        CaptureFileWriter.NewFileName(DateTime.Now, Guid.NewGuid(), encoding);

    private static string Write(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }

    /// <summary>Makes <paramref name="path"/> younger than the retention on the test's clock.</summary>
    private void MakeYoung(string path, TimeSpan age)
    {
        var time = _clock.GetUtcNow().UtcDateTime - age;
        File.SetCreationTimeUtc(path, time);
        File.SetLastWriteTimeUtc(path, time);
    }

    [Fact]
    public void Sweep_DeletesTheServersOwnFilesOlderThanTheRetention_AndKeepsTheYoungerOnes()
    {
        // Arrange
        var oldPng = Write(_captures, OwnName(ImageEncoding.Png));
        var oldJpg = Write(_captures, OwnName(ImageEncoding.Jpeg));
        var young = Write(_captures, OwnName());
        MakeYoung(young, CaptureFileSweep.Retention - TimeSpan.FromMinutes(5));
        var writtenLately = Write(_captures, OwnName());
        File.SetLastWriteTimeUtc(writtenLately, _clock.GetUtcNow().UtcDateTime - TimeSpan.FromHours(1));

        // Act
        var deleted = CreateSut().Sweep();

        // Assert
        deleted.Should().Be(2);
        File.Exists(oldPng).Should().BeFalse();
        File.Exists(oldJpg).Should().BeFalse();
        File.Exists(young).Should().BeTrue("a caller may still read a file it was handed within the retention");
        File.Exists(writtenLately).Should().BeTrue("its age counts from its last write");
        Directory.Exists(_captures).Should().BeTrue("only files are deleted");
    }

    [Fact]
    public void Sweep_LeavesEveryOtherFileAndEverySubFolderAlone()
    {
        // Arrange - all of them as old as the deleted file
        var own = Write(_captures, OwnName());
        var others = new[]
        {
            Write(_captures, "notes.txt"),
            Write(_captures, "shot.png"),
            Write(_captures, OwnName().Replace(".png", ".jpeg")),
            Write(_captures, OwnName() + ".txt"),
            Write(_captures, OwnName().ToUpperInvariant()),
            Write(_captures, "screenshot_20261122_153005_0123456789abcdef0123456789abcde.png"),
            Write(Path.Combine(_captures, "sub"), OwnName()),
            Write(Path.Combine(_captures, OwnName()), OwnName()) // a folder with a capture's name
        };

        // Act
        var deleted = CreateSut().Sweep();

        // Assert
        deleted.Should().Be(1);
        File.Exists(own).Should().BeFalse();
        others.Should().OnlyContain(path => File.Exists(path));
        Directory.Exists(Path.Combine(_captures, "sub")).Should().BeTrue();
    }

    [Fact]
    public void Sweep_NeverTouchesTheServersOtherFolders_OrTheFoldersOfItsEarlierNames()
    {
        // Arrange - the writer's names, old, in every folder beside the captures folder
        var own = Write(_captures, OwnName());
        var untouched = new[]
        {
            Write(_serverRoot, OwnName()),
            Write(Path.Combine(_serverRoot, "browser-debug"), OwnName()),
            Write(Path.Combine(_temp, LayoutStorageOptions.PreviousAppDataFolderName, "captures"), OwnName()),
            Write(Path.Combine(_temp, LayoutStorageOptions.OriginalAppDataFolderName, "captures"), OwnName()),
            Write(_temp, OwnName())
        };

        // Act
        CreateSut().Sweep();

        // Assert
        File.Exists(own).Should().BeFalse("precondition: the sweep ran");
        untouched.Should().OnlyContain(path => File.Exists(path));
    }

    [Fact]
    public void Sweep_NeverTouchesACaptureWrittenToTheCallersOutputPath()
    {
        // Arrange - a capture the caller wrote with outputPath, under a name the writer would give, a day and more ago
        var callerFolder = Path.Combine(_root, "caller");
        Directory.CreateDirectory(callerFolder);
        var outputPath = Path.Combine(callerFolder, OwnName());
        var screenshots = new ScreenshotService(TestDataFactory.CreateMockScreenCaptureWrapper().Object,
            TestDataFactory.CreateMockMonitorService().Object,
            Mock.Of<INativeWindowWrapper>(w => w.IsWindowVisible(It.IsAny<nint>()) == true),
            Permissive.CapturePolicy, Permissive.TargetPolicy, Permissive.SecurityProbe);
        var capture = screenshots.CaptureRegion(0, 0, 200, 100, new CaptureOptions { Output = CaptureOutputMode.File, OutputPath = outputPath });
        var own = Write(_captures, OwnName());

        // Act
        CreateSut().Sweep();

        // Assert
        capture.Metadata.FilePath.Should().Be(outputPath, "precondition: the capture went to the caller's path");
        File.Exists(own).Should().BeFalse("precondition: the sweep ran");
        File.Exists(outputPath).Should().BeTrue();
    }

    [Fact]
    public void Sweep_WhenTheCapturesFolderIsAJunction_DeletesNothing()
    {
        // Arrange - the captures folder replaced by a junction to another folder that holds files of the writer's names
        var outside = Path.Combine(_root, "outside");
        var target = Write(outside, OwnName());
        Directory.Delete(_captures);
        CreateJunction(_captures, outside);

        // Act
        var deleted = CreateSut().Sweep();

        // Assert
        deleted.Should().Be(0);
        File.Exists(target).Should().BeTrue();
    }

    [Fact]
    public void Sweep_WhenTheServersTempFolderIsAJunction_DeletesNothing()
    {
        // Arrange - %TEMP%\Helmsman replaced by a junction to a folder with a captures folder of its own
        var outside = Path.Combine(_root, "outside");
        var target = Write(Path.Combine(outside, "captures"), OwnName());
        Directory.Delete(_serverRoot, recursive: true);
        CreateJunction(_serverRoot, outside);

        // Act
        var deleted = CreateSut().Sweep();

        // Assert
        deleted.Should().Be(0);
        File.Exists(target).Should().BeTrue();
    }

    [SymbolicLinkFact]
    public void Sweep_WhenTheCapturesFolderIsADirectorySymbolicLink_DeletesNothing()
    {
        // Arrange - skipped without the privilege to create symbolic links; CaptureFileSweepGuardTests covers the same
        // through the fake file system
        var outside = Path.Combine(_root, "outside");
        var target = Write(outside, OwnName());
        Directory.Delete(_captures);
        CreateSymbolicLink(_captures, outside, directory: true);

        // Act
        var deleted = CreateSut().Sweep();

        // Assert
        deleted.Should().Be(0);
        File.Exists(target).Should().BeTrue();
    }

    [SymbolicLinkFact]
    public void Sweep_LeavesAFileThatIsASymbolicLink_AndWhatItLeadsTo()
    {
        // Arrange - a link of the writer's name to a file elsewhere (see the test above about the privilege)
        var target = Write(Path.Combine(_root, "outside"), "keep.png");
        var link = Path.Combine(_captures, OwnName());
        CreateSymbolicLink(link, target, directory: false);

        var own = Write(_captures, OwnName());

        // Act
        CreateSut().Sweep();

        // Assert
        File.Exists(own).Should().BeFalse("precondition: the sweep ran");
        File.GetAttributes(link).Should().HaveFlag(FileAttributes.ReparsePoint, "the link itself is kept");
        File.Exists(target).Should().BeTrue();
    }

    [Fact]
    public void Sweep_SkipsAFileInUse_AndDeletesTheOthers()
    {
        // Arrange - a viewer holds one file open without sharing its delete
        var held = Write(_captures, OwnName());
        var other = Write(_captures, OwnName());
        using var viewer = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Act
        var act = () => CreateSut().Sweep();

        // Assert
        act.Should().NotThrow().Which.Should().Be(1);
        File.Exists(held).Should().BeTrue();
        File.Exists(other).Should().BeFalse();
    }

    [Fact]
    public void Sweep_LeavesAReadOnlyFile()
    {
        // Arrange - the writer never marks a file read-only: whoever did wants to keep it
        var readOnly = Write(_captures, OwnName());
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        try
        {
            // Act
            var deleted = CreateSut().Sweep();

            // Assert
            deleted.Should().Be(0);
            File.Exists(readOnly).Should().BeTrue();
        }
        finally
        {
            File.SetAttributes(readOnly, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Sweep_WithoutTheCapturesFolder_DoesNothing_AndCreatesNothing()
    {
        // Arrange
        Directory.Delete(_captures);

        // Act
        var deleted = CreateSut().Sweep();

        // Assert
        deleted.Should().Be(0);
        Directory.Exists(_captures).Should().BeFalse();
    }

    [Fact]
    public async Task SweepsThatRace_AsServersSharingTheFolderDo_NeverThrow_AndDeleteEveryOldFile()
    {
        // Arrange
        var files = Enumerable.Range(0, 200).Select(_ => Write(_captures, OwnName())).ToList();
        var young = Write(_captures, OwnName());
        MakeYoung(young, TimeSpan.FromSeconds(1)); // another server wrote it a moment ago
        var sweeps = Enumerable.Range(0, 4).Select(_ => CreateSut()).ToList();
        using var start = new Barrier(sweeps.Count);

        // Act
        var counts = await Task.WhenAll(sweeps.Select(sweep => Task.Run(() =>
        {
            start.SignalAndWait();
            return sweep.Sweep();
        })));

        // Assert
        counts.Should().OnlyContain(count => count <= files.Count);
        files.Should().OnlyContain(path => !File.Exists(path));
        File.Exists(young).Should().BeTrue();
    }

    [Fact]
    public void ForDefaultFolder_SweepsTheWritersDefaultFolder_UnderTheServersTempFolder()
    {
        // Creating it touches no file
        var sweep = CaptureFileSweep.ForDefaultFolder(new CaptureFolderFileSystem());

        sweep.Folder.Should().Be(CaptureFileWriter.DefaultCaptureFolder);
        sweep.ServerTempRoot.Should().Be(Path.TrimEndingDirectorySeparator(CaptureFileWriter.ServerTempRoot));
    }

    [Fact]
    public void TheCaptureToolsOutputPathDescription_StatesTheRetention()
    {
        // The agent learns from the description how long a file it is handed stays; it must say what the sweep does
        var description = (string)typeof(Helmsman.Tools.ScreenshotTools)
            .GetField("OutputPathDescription", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()!;

        description.Should().Contain($"captures, kept for {CaptureFileSweep.Retention.TotalHours:0} hours.");
    }

    [Theory]
    [InlineData(@"C:\elsewhere\captures")]
    [InlineData(@"C:\temp\Helmsman")]
    [InlineData(@"C:\temp\HelmsmanOther\captures")]
    public void AFolderNotInsideTheServersTempFolder_IsRefused(string folder)
    {
        var act = () => new CaptureFileSweep(new CaptureFolderFileSystem(), folder, @"C:\temp\Helmsman");

        act.Should().Throw<ArgumentException>();
    }

    private void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var mklink = Process.Start(start)!;
        var output = mklink.StandardOutput.ReadToEnd() + mklink.StandardError.ReadToEnd();
        mklink.WaitForExit();
        _links.Add(link);
        File.GetAttributes(link).Should().HaveFlag(FileAttributes.ReparsePoint, "mklink /J made the junction: {0}", output);
    }

    /// <summary>Makes a symbolic link; only [SymbolicLinkFact] tests call it, which run only where links can be made.</summary>
    private void CreateSymbolicLink(string link, string target, bool directory)
    {
        if (directory)
        {
            Directory.CreateSymbolicLink(link, target);
        }
        else
        {
            File.CreateSymbolicLink(link, target);
        }

        _links.Add(link);
        File.GetAttributes(link).Should().HaveFlag(FileAttributes.ReparsePoint);
    }
}

/// <summary>
/// A fact that makes symbolic links: skipped, with the reason, where this user cannot make one (it needs Developer Mode or
/// the Create symbolic links privilege). The probe makes one file link in a folder of its own under %TEMP% and deletes it.
/// </summary>
public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Unavailable = new(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), $"helmsman-symlink-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, "target.txt");
            File.WriteAllText(target, "probe");
            File.CreateSymbolicLink(Path.Combine(folder, "link.txt"), target);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"This user cannot create symbolic links (turn on Developer Mode, or run with the Create symbolic links " +
                   $"privilege): {ex.Message} CaptureFileSweepGuardTests covers the same guard through a fake file system.";
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    });

    public SymbolicLinkFactAttribute()
    {
        if (Unavailable.Value is { } reason)
        {
            Skip = reason;
        }
    }
}
