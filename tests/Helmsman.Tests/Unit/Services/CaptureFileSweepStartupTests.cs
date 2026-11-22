namespace Helmsman.Tests.Unit.Services;

/// <summary>
/// CaptureFileSweepStartup: when the server sweeps its captures folder (a minute after it starts, then every hour, on a timer
/// the test's clock fires) and that no sweep blocks the start, overlaps another, or lets an exception out of its timer. The
/// sweep runs on a fake file system; nothing touches the disk.
/// </summary>
public sealed class CaptureFileSweepStartupTests
{
    private const string Root = @"C:\Users\me\AppData\Local\Temp\Helmsman";
    private const string Folder = Root + @"\captures";

    private readonly ManualTimerClock _clock = new();
    private readonly CountingFileSystem _files = new();

    private CaptureFileSweepStartup CreateSut() => new(new CaptureFileSweep(_files, Folder, Root, _clock), clock: _clock);

    [Fact]
    public async Task StartAsync_ReturnsAtOnce_AndTheFirstSweepComesAfterTheDelay_ThenOneEveryInterval()
    {
        // Arrange
        using var sut = CreateSut();

        // Act & Assert
        await sut.StartAsync(CancellationToken.None);
        _files.ListCalls.Should().Be(0, "starting the server sweeps nothing");

        _clock.Advance(CaptureFileSweep.FirstSweepDelay - TimeSpan.FromSeconds(1));
        _files.ListCalls.Should().Be(0);

        _clock.Advance(TimeSpan.FromSeconds(1));
        _files.ListCalls.Should().Be(1);

        _clock.Advance(CaptureFileSweep.Interval * 3);
        _files.ListCalls.Should().Be(4);
    }

    [Fact]
    public async Task StopAsync_EndsTheSweeps()
    {
        // Arrange
        using var sut = CreateSut();
        await sut.StartAsync(CancellationToken.None);
        _clock.Advance(CaptureFileSweep.FirstSweepDelay);

        // Act
        await sut.StopAsync(CancellationToken.None);
        _clock.Advance(CaptureFileSweep.Interval * 5);

        // Assert
        _files.ListCalls.Should().Be(1);
    }

    [Fact]
    public void Creating_StartsNoTimer_AndTouchesNoFile()
    {
        // Arrange & Act - as the DI tests resolve it
        using var sut = CreateSut();
        _clock.Advance(CaptureFileSweep.Interval * 2);

        // Assert
        _files.ListCalls.Should().Be(0);
        _files.AttributeReads.Should().Be(0);
    }

    [Fact]
    public async Task ASweepThatThrows_StaysOnItsTimer_AndTheNextSweepRuns()
    {
        // Arrange - an exception the sweep does not expect from a file system: on a timer thread it would end the server
        using var sut = CreateSut();
        _files.ListFailure = new InvalidOperationException("broken");
        await sut.StartAsync(CancellationToken.None);

        // Act
        var act = () => _clock.Advance(CaptureFileSweep.FirstSweepDelay + CaptureFileSweep.Interval);

        // Assert
        act.Should().NotThrow();
        _files.ListCalls.Should().Be(2);
    }

    [Fact]
    public async Task ASweepThatIsDue_WhileTheLastStillRuns_IsLeftOut()
    {
        // Arrange - the next sweep falls due while the first one lists the folder
        using var sut = CreateSut();
        var advanced = false;
        _files.WhileListing = () =>
        {
            if (!advanced)
            {
                advanced = true;
                _clock.Advance(CaptureFileSweep.Interval);
            }
        };
        await sut.StartAsync(CancellationToken.None);

        // Act
        _clock.Advance(CaptureFileSweep.FirstSweepDelay);

        // Assert
        _files.ListCalls.Should().Be(1, "the sweep that fell due during the first one did not run beside it");
    }

    /// <summary>Every folder exists; the captures folder is empty. Counts the reads.</summary>
    private sealed class CountingFileSystem : ICaptureFolderFileSystem
    {
        public int ListCalls { get; private set; }

        public int AttributeReads { get; private set; }

        public Exception? ListFailure { get; set; }

        public Action? WhileListing { get; set; }

        public FileAttributes? GetAttributes(string path)
        {
            AttributeReads++;
            return FileAttributes.Directory;
        }

        public IReadOnlyList<CaptureFolderEntry> ListFiles(string folder)
        {
            ListCalls++;
            WhileListing?.Invoke();
            return ListFailure != null ? throw ListFailure : [];
        }

        public void DeleteFile(string path) => throw new InvalidOperationException("nothing is listed, so nothing is deleted");
    }
}
