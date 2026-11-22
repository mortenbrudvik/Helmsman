namespace WinDesktopMcp.Tests.Unit.Services;

/// <summary>
/// save_workspace over a workspace of the same name is all or nothing (roadmap M7.5): the preset is captured, completed with
/// the folders and pages, and written once, so a save that fails part-way leaves the workspace saved before exactly as it
/// was. The layout service is real, on a temp folder; the windows, the shell and the browser are mocks.
/// </summary>
public class WorkspaceServiceSaveTests : IDisposable
{
    private const nint Explorer = 0x100, Edge = 0x200;
    private const string Name = "morning";

    private readonly string _layoutsPath = Path.Combine(Path.GetTempPath(), $"WorkspaceServiceSaveTests_{Guid.NewGuid():N}");
    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService(
    [
        TestDataFactory.CreateWindowInfo((int)Explorer, "Reports", "explorer", 1100),
        TestDataFactory.CreateWindowInfo((int)Edge, "Inbox - Mail - Microsoft Edge", "msedge", 1200)
    ]);
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService().WithWindowMonitors();
    private readonly Mock<IProcessService> _processes = TestDataFactory.CreateMockProcessService();
    private readonly Mock<INativeShellWindowsWrapper> _shell = new();
    private readonly Mock<IBrowserReadingService> _browsers = new();
    private readonly Mock<ITargetPolicy> _policy = new();
    private readonly Mock<ILayoutService> _layouts = new();
    private readonly LayoutService _storage;
    private bool _failWrites;

    public WorkspaceServiceSaveTests()
    {
        _storage = Storage();

        // The layout service as the workspace service sees it: the real one, except that a test can make its writes fail as a
        // full disk would
        _layouts.Setup(l => l.CaptureLayout(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string[]?>(), It.IsAny<string[]?>(), It.IsAny<bool>()))
            .Returns((string name, string? description, string[]? include, string[]? exclude, bool minimized) =>
                _storage.CaptureLayout(name, description, include, exclude, minimized));
        _layouts.Setup(l => l.CaptureCurrentLayoutAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string[]?>(), It.IsAny<string[]?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((string name, string? description, string[]? include, string[]? exclude, bool minimized, CancellationToken token) =>
                _storage.CaptureCurrentLayoutAsync(name, description, include, exclude, minimized, token));
        _layouts.Setup(l => l.GetStorageError()).Returns(() => _storage.GetStorageError());
        _layouts.Setup(l => l.SavePresetAsync(It.IsAny<LayoutPreset>(), It.IsAny<CancellationToken>()))
            .Returns((LayoutPreset preset, CancellationToken token) => _failWrites
                ? Task.FromResult(new LayoutSaveResult(false, preset, true,
                    $"Failed to save layout preset '{preset.Name}': There is not enough space on the disk.", nameof(LayoutErrorCode.SaveFailed)))
                : _storage.SavePresetAsync(preset, token));

        _policy.Setup(p => p.Evaluate(It.IsAny<IReadOnlyCollection<string?>>())).Returns(TargetPolicyVerdict.Allow);
        _browsers.Setup(b => b.IsBrowserWindow(Edge)).Returns(true);
        Shows(@"C:\Users\me\Reports\2025", "https://mail.example.com/inbox");
    }

    public void Dispose()
    {
        if (Directory.Exists(_layoutsPath))
            Directory.Delete(_layoutsPath, recursive: true);
    }

    private LayoutService Storage()
    {
        var foreground = TestDataFactory.CreateMockForegroundService().Object;
        return WiredLayoutService.Create(_windows.Object, _monitors.Object, _processes.Object, foreground, LayoutStorageOptions.ForPath(_layoutsPath),
            new LaunchPlacementService(_windows.Object, foreground, Permissive.Session));
    }

    private WorkspaceService CreateSut() =>
        new(_layouts.Object, _windows.Object, new Mock<INativeWindowWrapper>().Object, _browsers.Object, _shell.Object, new Mock<IApplicationLauncher>().Object,
            _policy.Object, new Mock<ICdpTabService>().Object);

    /// <summary>The folder File Explorer shows and the page the browser shows (without a debug port, so the only one kept).</summary>
    private void Shows(string folder, string page)
    {
        _shell.Setup(s => s.GetExplorerFolders()).Returns(new ExplorerFolderListing([new ExplorerFolder(Explorer, folder)], 0));
        _browsers.Setup(b => b.GetBrowserUrlAsync(Edge, It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserUrlResult(true, page, "edge", "Inbox", null, BrowserErrorCode.None));
    }

    /// <summary>The single preset file in the layouts folder.</summary>
    private string PresetFile() => Directory.GetFiles(_layoutsPath, "*.json").Should().ContainSingle().Which;

    /// <summary>The workspace as a restore would load it: read by a layout service of its own, as after a restart.</summary>
    private LayoutPreset Stored()
    {
        var preset = Storage().GetPreset(Name);
        preset.Should().NotBeNull("the workspace must still load");
        return preset!;
    }

    private static void ShouldShow(LayoutPreset workspace, string folder, string page)
    {
        workspace.Placements.Should().HaveCount(2);
        workspace.Placements[0].Content.Should().Be(new WorkspaceContent(Folder: folder));
        workspace.Placements[1].Content!.Urls.Should().Equal(page);
        workspace.Placements[1].Content!.UrlsPartial.Should().BeTrue();
    }

    private async Task<string> SaveTheFirstWorkspaceAsync()
    {
        var first = await CreateSut().SaveAsync(Name, "Monday", null, null, includeMinimized: false);
        first.Success.Should().BeTrue(first.Error);
        ShouldShow(Stored(), @"C:\Users\me\Reports\2025", "https://mail.example.com/inbox");

        // The next save would keep other content
        Shows(@"C:\Users\me\Reports\2026", "https://mail.example.com/sent");
        return File.ReadAllText(PresetFile());
    }

    [Fact]
    public async Task ASaveWhoseWriteFails_ReportsIt_AndLeavesTheWorkspaceSavedBefore_AsItWas()
    {
        // Arrange
        var before = await SaveTheFirstWorkspaceAsync();
        _failWrites = true;

        // Act
        var result = await CreateSut().SaveAsync(Name, "Tuesday", null, null, includeMinimized: false);

        // Assert - the failure is reported, and the workspace on disk is the first one, byte for byte, with its content
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.SaveFailed));
        result.Error.Should().Contain("not enough space");
        File.ReadAllText(PresetFile()).Should().Be(before);
        ShouldShow(Stored(), @"C:\Users\me\Reports\2025", "https://mail.example.com/inbox");
        Stored().Description.Should().Be("Monday");
    }

    [Fact]
    public async Task ASaveThatIsCancelledWhileItReadsThePages_LeavesTheWorkspaceSavedBefore_AsItWas()
    {
        // Arrange - the windows were captured; the client cancels while the browser's pages are read
        var before = await SaveTheFirstWorkspaceAsync();
        _browsers.Setup(b => b.GetBrowserUrlAsync(Edge, It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act
        var act = () => CreateSut().SaveAsync(Name, "Tuesday", null, null, includeMinimized: false);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        File.ReadAllText(PresetFile()).Should().Be(before);
        ShouldShow(Stored(), @"C:\Users\me\Reports\2025", "https://mail.example.com/inbox");
    }

    [Fact]
    public async Task AnOverwrite_StoresExactlyOneCompletePreset_WrittenOnce()
    {
        // Arrange
        await SaveTheFirstWorkspaceAsync();

        // Act
        var result = await CreateSut().SaveAsync(Name, "Tuesday", null, null, includeMinimized: false);

        // Assert - one file, one preset of that name, with the new content; each save wrote the preset once, complete
        result.Should().BeEquivalentTo(new WorkspaceSaveResult(true, Name, 2, Folders: 1, BrowserWindows: 1, Urls: 1, PartialBrowsers: 1),
            o => o.Excluding(r => r.Warnings));
        PresetFile();
        Storage().GetAllPresets().Should().ContainSingle(p => p.Name == Name);
        ShouldShow(Stored(), @"C:\Users\me\Reports\2026", "https://mail.example.com/sent");
        Stored().Description.Should().Be("Tuesday");
        _layouts.Verify(l => l.SavePresetAsync(It.Is<LayoutPreset>(p => p.Placements.All(pl => pl.Content != null)), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        _layouts.Verify(l => l.CaptureCurrentLayoutAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string[]?>(), It.IsAny<string[]?>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never, "a capture that saves writes the preset without its content first");
    }
}
