using System.Drawing;

namespace Helmsman.Tests.Unit.Services;

/// <summary>
/// The capture warnings about an image that may be an older frame: CaptureUnchangedAfterInput (the pixels are identical to
/// the previous capture of the same target although input reached it in between) and ExclusiveFullscreen (Windows reports a
/// full-screen 3D app, the window in front fills its monitor, and the image shows it). No screen is read.
/// </summary>
public class CaptureFreshnessWarningTests
{
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;
    private const long WsVisible = 0x10000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_SHOWMINNOACTIVE = 7;

    private static readonly CaptureOptions FullResolution = new() { MaxDimension = 0, Format = CaptureFormat.Png };

    private static readonly nint Window = new(0x5000);
    private static readonly nint Other = new(0x6000);

    private readonly Mock<IScreenCaptureWrapper> _capture = TestDataFactory.CreateMockScreenCaptureWrapper(captureWidth: 100, captureHeight: 80);
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IWindowCallRunner> _calls = new();
    private readonly CaptureFreshness _freshness = new();
    private readonly MonitorInfo _monitor = TestDataFactory.CreateMonitorInfo();

    public CaptureFreshnessWarningTests()
    {
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Window)).Returns(42u);
        _calls.Setup(c => c.Run(It.IsAny<nint>(), It.IsAny<Func<CaptureResult>>()))
            .Returns((nint _, Func<CaptureResult> call) => call());
        _calls.Setup(c => c.Run(It.IsAny<nint>(), It.IsAny<Func<bool>>()))
            .Returns((nint _, Func<bool> call) => call());

        // Pixels that are not one color, so ImageBlank stays out of the way
        _capture.Setup(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()))
            .Returns(() => new CaptureResult(Pattern(100, 80), 100, 80, 0, 0, "PrintWindow"));
        _capture.Setup(c => c.CaptureWindowFromScreen(It.IsAny<nint>(), It.IsAny<bool>()))
            .Returns(() => new CaptureResult(Pattern(100, 80), 100, 80, 0, 0, "ScreenBitBlt"));
        _capture.Setup(c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int x, int y, int w, int h) => new CaptureResult(Pattern(w, h), w, h, x, y, "ScreenBitBlt"));
    }

    private ScreenshotService CreateSut() =>
        new(_capture.Object, _monitors.Object, _windows.Object, Permissive.CapturePolicy, Permissive.TargetPolicy,
            Permissive.SecurityProbe, _calls.Object, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100),
            captureFreshness: _freshness);

    private static bool IsUnchanged(string warning) => warning.StartsWith("CaptureUnchangedAfterInput:", StringComparison.Ordinal);

    private static bool IsFullscreen(string warning) => warning.StartsWith("ExclusiveFullscreen:", StringComparison.Ordinal);

    private void PointerHidden() => _capture.Setup(c => c.GetPointer()).Returns(new PointerState(50, 40, Visible: false));

    private void PointerVisible() => _capture.Setup(c => c.GetPointer()).Returns(new PointerState(50, 40, Visible: true));

    #region CaptureUnchangedAfterInput: window captures

    [Fact]
    public void CaptureWindow_TheSamePixelsAfterInputToTheWindow_WarnsCaptureUnchangedAfterInput()
    {
        // Arrange
        var sut = CreateSut();
        sut.CaptureWindow(Window, includeFrame: true, FullResolution);
        _freshness.InputSent(Window, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().ContainSingle(w => IsUnchanged(w)).Which.Should().Be(
            "CaptureUnchangedAfterInput: the image is identical to the previous capture of this window although input was sent " +
            "to it in between. Either the input changed nothing visible, or this is an older frame (a fullscreen game in " +
            "front can keep returning one). If such a game is in front, call focus_window on your own terminal or chat " +
            "window (get_server_info host.windows), then capture_window of the game. Your next key, or a click no window " +
            "covers, with its windowHandle brings it back. No need to ask the user.");
    }

    [Fact]
    public void CaptureWindow_TheFirstCapture_DoesNotWarn()
    {
        // Arrange
        _freshness.InputSent(Window, pointerMoveOnly: false);

        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_TheSamePixelsWithoutInput_DoesNotWarn()
    {
        // Arrange
        var sut = CreateSut();
        sut.CaptureWindow(Window, includeFrame: true, FullResolution);

        // Act
        var capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_OtherPixelsAfterInput_DoesNotWarn()
    {
        // Arrange
        var seed = 0;
        _capture.Setup(c => c.CaptureWindow(Window, true))
            .Returns(() => new CaptureResult(Pattern(100, 80, seed++), 100, 80, 0, 0, "PrintWindow"));
        var sut = CreateSut();
        sut.CaptureWindow(Window, includeFrame: true, FullResolution);
        _freshness.InputSent(Window, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_TheSamePixelsReadAnotherWay_DoesNotWarn()
    {
        // Arrange - the first read copied the screen, the second asked the window to draw itself
        var sut = CreateSut();
        sut.CaptureWindow(Window, includeFrame: true, FullResolution with { CaptureMethod = WindowCaptureMethod.Screen });
        _freshness.InputSent(Window, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_TheSamePixelsAfterInputToAnotherWindow_DoesNotWarn()
    {
        // Arrange
        var sut = CreateSut();
        sut.CaptureWindow(Window, includeFrame: true, FullResolution);
        _freshness.InputSent(Other, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_OfAControl_AfterInputToItsTopLevelWindow_Warns()
    {
        // Arrange - input goes to the top-level window that receives it
        var control = new nint(0x5010);
        _windows.Setup(w => w.GetAncestor(control, WindowAncestor.Root)).Returns(Window);
        var sut = CreateSut();
        sut.CaptureWindow(control, includeFrame: false, FullResolution);
        _freshness.InputSent(Window, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureWindow(control, includeFrame: false, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().ContainSingle(w => IsUnchanged(w));
    }

    [Fact]
    public void CaptureWindow_OnlyAPointerMoveWithThePointerShown_DoesNotWarn()
    {
        // Arrange - in an ordinary application a move often changes nothing
        PointerVisible();
        var sut = CreateSut();
        sut.CaptureWindow(Window, includeFrame: true, FullResolution);
        _freshness.InputSent(Window, pointerMoveOnly: true);

        // Act
        var capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_OnlyAPointerMoveWithThePointerHidden_Warns()
    {
        // Arrange - the application draws its own pointer, so a move must change pixels
        PointerHidden();
        var sut = CreateSut();
        sut.CaptureWindow(Window, includeFrame: true, FullResolution);
        _freshness.InputSent(Window, pointerMoveOnly: true);

        // Act
        var capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().ContainSingle(w => IsUnchanged(w));
    }

    [Fact]
    public void CaptureWindowArea_CroppedToARef_NeverWarns()
    {
        // Arrange
        var sut = CreateSut();
        var area = new BoundsDto(10, 10, 20, 20);
        sut.CaptureWindowArea(Window, includeFrame: true, area, FullResolution);
        _freshness.InputSent(Window, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureWindowArea(Window, includeFrame: true, area, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureWindowArea_CroppedToARef_IsNotRememberedForTheWholeWindow()
    {
        // Arrange - a crop the size of the whole window reads the same pixels, but is not the window's capture
        var sut = CreateSut();
        sut.CaptureWindowArea(Window, includeFrame: true, new BoundsDto(0, 0, 100, 80), FullResolution);
        _freshness.InputSent(Window, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_OfAWindowRestoredForIt_NeverWarns()
    {
        // Arrange - restoreIfMinimized shows the window for the capture only
        var minimized = true;
        _windows.Setup(w => w.IsIconic(Window)).Returns(() => minimized);
        _windows.Setup(w => w.ShowWindow(Window, It.IsAny<int>())).Returns((nint _, int command) =>
        {
            minimized = command == SW_SHOWMINNOACTIVE;
            return true;
        });
        var sut = CreateSut();
        ScreenCapture<ScreenshotResult> capture;
        using (sut.RestoreForCapture(Window, Window, includeFrame: true))
        {
            sut.CaptureWindow(Window, includeFrame: true, FullResolution);
        }

        _freshness.InputSent(Window, pointerMoveOnly: false);

        // Act
        using (sut.RestoreForCapture(Window, Window, includeFrame: true))
        {
            capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution);
        }

        // Assert
        _windows.Verify(w => w.ShowWindow(Window, SW_SHOWNOACTIVATE), Times.AtLeastOnce);
        capture.Metadata.Success.Should().BeTrue();
        (capture.Metadata.Warnings ?? []).Should().NotContain(w => IsUnchanged(w));
    }

    [Fact]
    public void CaptureWindowWithElements_TheSamePixelsAfterInput_Warns()
    {
        // Arrange
        var sut = CreateSut();
        sut.CaptureWindowWithElements(Window, "App", [], includeFrame: true, ElementOverlayOptions.Default, FullResolution);
        _freshness.InputSent(Window, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureWindowWithElements(Window, "App", [], includeFrame: true, ElementOverlayOptions.Default, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().ContainSingle(w => IsUnchanged(w));
    }

    [Fact]
    public void CaptureWindow_WithoutTheStore_NeverWarns()
    {
        // Arrange - a service built without the freshness store, as tests and tools that read pixels for themselves are
        var sut = new ScreenshotService(_capture.Object, _monitors.Object, _windows.Object, Permissive.CapturePolicy,
            Permissive.TargetPolicy, Permissive.SecurityProbe, _calls.Object);
        sut.CaptureWindow(Window, includeFrame: true, FullResolution);
        _freshness.InputSent(Window, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    #endregion

    #region CaptureUnchangedAfterInput: monitor and region captures

    [Fact]
    public void CaptureRegion_TheSamePixelsAfterInputToAWindowOverIt_WarnsForTheRegion()
    {
        // Arrange
        _windows.Setup(w => w.GetWindowRect(Other)).Returns((0, 0, 50, 50));
        var sut = CreateSut();
        sut.CaptureRegion(10, 10, 100, 80, FullResolution);
        _freshness.InputSent(Other, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureRegion(10, 10, 100, 80, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().ContainSingle(w => IsUnchanged(w)).Which.Should().Be(
            "CaptureUnchangedAfterInput: the image is identical to the previous capture of this region although input was sent " +
            "to it in between. Either the input changed nothing visible, or this is an older frame (a fullscreen game in " +
            "front can keep returning one). If such a game is in front, call focus_window on your own terminal or chat " +
            "window (get_server_info host.windows), then capture_window of the game. Your next key, or a click no window " +
            "covers, with its windowHandle brings it back. No need to ask the user.");
    }

    [Fact]
    public void CaptureRegion_TheSamePixelsAfterInputToAWindowElsewhere_DoesNotWarn()
    {
        // Arrange
        _windows.Setup(w => w.GetWindowRect(Other)).Returns((500, 500, 600, 600));
        var sut = CreateSut();
        sut.CaptureRegion(10, 10, 100, 80, FullResolution);
        _freshness.InputSent(Other, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureRegion(10, 10, 100, 80, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureRegion_AnotherRegion_DoesNotWarn()
    {
        // Arrange - the same pixels (the pattern starts at the region's corner) of a region one pixel to the right
        _windows.Setup(w => w.GetWindowRect(Other)).Returns((0, 0, 500, 500));
        var sut = CreateSut();
        sut.CaptureRegion(10, 10, 100, 80, FullResolution);
        _freshness.InputSent(Other, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureRegion(11, 10, 100, 80, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureMonitor_TheSamePixelsAfterInputToAWindowOnIt_Warns()
    {
        // Arrange
        _windows.Setup(w => w.GetWindowRect(Other)).Returns((100, 100, 400, 300));
        var sut = CreateSut();
        sut.CaptureMonitor(0, FullResolution);
        _freshness.InputSent(Other, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureMonitor(0, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().ContainSingle(w => IsUnchanged(w)).Which.Should().Contain("of this region");
    }

    [Fact]
    public void CaptureRegion_OnlyAPointerMoveWithThePointerShown_DoesNotWarn()
    {
        // Arrange
        PointerVisible();
        _windows.Setup(w => w.GetWindowRect(Other)).Returns((0, 0, 50, 50));
        var sut = CreateSut();
        sut.CaptureRegion(10, 10, 100, 80, FullResolution);
        _freshness.InputSent(Other, pointerMoveOnly: true);

        // Act
        var capture = sut.CaptureRegion(10, 10, 100, 80, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    #endregion

    #region CaptureUnchangedAfterInput: what does and does not change the comparison

    [Fact]
    public void CaptureWindow_InputSentWhileItsPixelsWereRead_CountsAsAfterThem()
    {
        // Arrange - a click a parallel call sent while the first capture read the pixels, before it was remembered
        var reads = 0;
        _capture.Setup(c => c.CaptureWindow(Window, true)).Returns(() =>
        {
            if (reads++ == 0)
            {
                _freshness.InputSent(Window, pointerMoveOnly: false);
            }

            return new CaptureResult(Pattern(100, 80), 100, 80, 0, 0, "PrintWindow");
        });
        var sut = CreateSut();
        sut.CaptureWindow(Window, includeFrame: true, FullResolution);

        // Act
        var capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().ContainSingle(w => IsUnchanged(w));
    }

    [Fact]
    public void CaptureWindow_AnotherTargetCapturedInBetween_StillComparesWithTheWindowsPreviousCapture()
    {
        // Arrange
        var sut = CreateSut();
        sut.CaptureWindow(Window, includeFrame: true, FullResolution);
        sut.CaptureRegion(500, 500, 100, 80, FullResolution);
        sut.CaptureWindow(Other, includeFrame: true, FullResolution);
        _freshness.InputSent(Window, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().ContainSingle(w => IsUnchanged(w));
    }

    [Fact]
    public void CaptureWindow_WithIncludeCursorTheSecondTime_StillWarns()
    {
        // Arrange - the pointer is drawn into the image after the pixels were compared
        PointerVisible();
        _capture.Setup(c => c.DrawPointer(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>())).Returns((nint hdc, int _, int _) =>
        {
            using var graphics = Graphics.FromHdc(hdc);
            graphics.FillRectangle(Brushes.Red, 40, 30, 12, 12);
            return true;
        });
        var sut = CreateSut();
        sut.CaptureWindow(Window, includeFrame: true, FullResolution);
        _freshness.InputSent(Window, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution with { IncludeCursor = true });

        // Assert
        capture.Metadata.Cursor!.Drawn.Should().BeTrue();
        capture.Metadata.Warnings.Should().ContainSingle(w => IsUnchanged(w));
    }

    [Fact]
    public void CaptureWindow_OfOneColorAfterInput_WarnsOnlyImageBlank()
    {
        // Arrange - a solid image is identical to the previous one by nature; ImageBlank says enough
        _capture.Setup(c => c.CaptureWindow(Window, true)).Returns(() => new CaptureResult(new byte[100 * 80 * 4], 100, 80, 0, 0, "PrintWindow"));
        var sut = CreateSut();
        sut.CaptureWindow(Window, includeFrame: true, FullResolution);
        _freshness.InputSent(Window, pointerMoveOnly: false);

        // Act
        var capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().ContainSingle(w => w.StartsWith("ImageBlank:", StringComparison.Ordinal));
        capture.Metadata.Warnings.Should().NotContain(w => IsUnchanged(w));
    }

    #endregion

    #region ExclusiveFullscreen

    /// <summary>
    /// Windows reports a full-screen 3D app (<paramref name="state"/>, 3 by default), and <paramref name="game"/>, in front,
    /// fills the whole monitor, its taskbar included.
    /// </summary>
    private void FullscreenGameInFront(nint game, (int Left, int Top, int Right, int Bottom)? rect = null, int? state = 3)
    {
        _capture.Setup(c => c.GetUserNotificationState()).Returns(state);
        _windows.Setup(w => w.GetForegroundWindow()).Returns(game);
        _windows.Setup(w => w.GetWindowRect(game)).Returns(rect ?? (0, 0, 1920, 1080));
        _windows.Setup(w => w.GetWindowThreadProcessId(game)).Returns(42u);
        _monitors.Setup(m => m.GetMonitorForWindow(game)).Returns(_monitor);
    }

    [Fact]
    public void CaptureWindow_OfAFullscreenWindowInFront_WhileWindowsReportsAFullScreen3DApp_WarnsExclusiveFullscreen()
    {
        // Arrange
        FullscreenGameInFront(Window);

        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().ContainSingle(w => IsFullscreen(w)).Which.Should().Be(
            "ExclusiveFullscreen: the window in front has its monitor to itself (Windows reports a full-screen 3D app). While " +
            "it stays in front, captures of that monitor can keep returning the frame from when it came to the front: to see " +
            "the current frame, call focus_window on your own terminal or chat window (get_server_info host.windows), then " +
            "capture_window of the fullscreen window. Your next key, or a click no window covers, with its windowHandle " +
            "brings it back. No need to ask the user.");
    }

    [Fact]
    public void CaptureWindow_OfAFullscreenWindowWithACaptionAndThePointerShown_StillWarns()
    {
        // Arrange - an SDL borderless window keeps its caption bits, and the game need not hide the pointer
        FullscreenGameInFront(Window);
        _windows.Setup(w => w.GetWindowLongPtr(Window, GwlStyle)).Returns((nint)(WsCaption | WsVisible));
        PointerVisible();

        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().ContainSingle(w => IsFullscreen(w));
    }

    [Fact]
    public void CaptureMonitor_UnderAFullscreenWindow_Warns()
    {
        // Arrange
        FullscreenGameInFront(Other);

        // Act
        var capture = CreateSut().CaptureMonitor(0, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().ContainSingle(w => IsFullscreen(w));
    }

    [Fact]
    public void CaptureRegion_OverAFullscreenWindow_Warns()
    {
        // Arrange
        FullscreenGameInFront(Other);

        // Act
        var capture = CreateSut().CaptureRegion(10, 10, 100, 80, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().ContainSingle(w => IsFullscreen(w));
    }

    [Fact]
    public void CaptureWindowWithElements_OfAFullscreenWindow_Warns()
    {
        // Arrange
        FullscreenGameInFront(Window);

        // Act
        var capture = CreateSut().CaptureWindowWithElements(Window, "Game", [], includeFrame: true, ElementOverlayOptions.Default,
            FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().ContainSingle(w => IsFullscreen(w));
    }

    [Fact]
    public void CaptureWindow_OfAnotherWindowCopiedFromTheScreenOverTheFullscreenWindow_Warns()
    {
        // Arrange - the screen copy shows what is in front there: the fullscreen window
        FullscreenGameInFront(Other);

        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution with { CaptureMethod = WindowCaptureMethod.Screen });

        // Assert
        capture.Metadata.CaptureMethod.Should().Be("ScreenBitBlt");
        capture.Metadata.Warnings.Should().ContainSingle(w => IsFullscreen(w));
    }

    [Theory]
    [InlineData(2)] // QUNS_BUSY: a full-monitor GDI or Direct3D 11 window in front, or the game in the background
    [InlineData(5)] // QUNS_ACCEPTS_NOTIFICATIONS
    [InlineData(null)] // the state could not be read
    public void CaptureWindow_OfAFullscreenWindow_WhileWindowsReportsNoFullScreen3DApp_DoesNotWarn(int? state)
    {
        // Arrange
        FullscreenGameInFront(Window, state: state);

        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_WithNoWindowInFront_DoesNotWarn()
    {
        // Arrange
        FullscreenGameInFront(Window);
        _windows.Setup(w => w.GetForegroundWindow()).Returns(nint.Zero);

        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureRegion_WhileTheWindowInFrontIsMinimized_DoesNotWarn()
    {
        // Arrange - the foreground window can be minimized for a moment, so the region shows what is behind it
        FullscreenGameInFront(Other);
        _windows.Setup(w => w.IsIconic(Other)).Returns(true);

        // Act
        var capture = CreateSut().CaptureRegion(10, 10, 100, 80, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_OfAWindowThatDoesNotFillItsMonitor_DoesNotWarn()
    {
        // Arrange - the work area only (the taskbar shows)
        FullscreenGameInFront(Window, rect: (0, 0, 1920, 1040));

        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_OfAnotherWindowThatDrewItself_DoesNotWarn()
    {
        // Arrange - PrintWindow shows only the captured window, not the fullscreen one in front
        FullscreenGameInFront(Other);

        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution with { CaptureMethod = WindowCaptureMethod.PrintWindow });

        // Assert
        capture.Metadata.CaptureMethod.Should().Be("PrintWindow");
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_OfAnotherWindowFromTheCompositor_DoesNotWarn()
    {
        // Arrange - Windows.Graphics.Capture shows only the captured window too
        FullscreenGameInFront(Other);
        var graphics = new Mock<IGraphicsCaptureWrapper>();
        graphics.Setup(g => g.Availability).Returns(GraphicsCaptureAvailability.Available);
        graphics.Setup(g => g.CaptureWindow(Window, It.IsAny<bool>()))
            .Returns(new CaptureResult(Pattern(100, 80), 100, 80, 0, 0, IGraphicsCaptureWrapper.Method));
        var sut = new ScreenshotService(_capture.Object, _monitors.Object, _windows.Object, Permissive.CapturePolicy,
            Permissive.TargetPolicy, Permissive.SecurityProbe, _calls.Object, TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(100), graphicsCapture: graphics.Object, captureFreshness: _freshness);

        // Act
        var capture = sut.CaptureWindow(Window, includeFrame: true, FullResolution with { CaptureMethod = WindowCaptureMethod.Wgc });

        // Assert
        capture.Metadata.CaptureMethod.Should().Be(IGraphicsCaptureWrapper.Method);
        capture.Metadata.Warnings.Should().BeNull();
    }

    [Fact]
    public void CaptureRegion_ThatDoesNotOverlapTheFullscreenWindow_DoesNotWarn()
    {
        // Arrange - the game fills a second monitor; the region is on the first, which stays live
        var second = TestDataFactory.CreateMonitorInfo(index: 1, isPrimary: false, boundsX: 1920, boundsWidth: 1920);
        FullscreenGameInFront(Other, rect: (1920, 0, 3840, 1080));
        _monitors.Setup(m => m.GetMonitorForWindow(Other)).Returns(second);

        // Act
        var capture = CreateSut().CaptureRegion(10, 10, 100, 80, FullResolution);

        // Assert
        capture.Metadata.Warnings.Should().BeNull();
    }

    #endregion

    /// <summary>Pixels whose color encodes their position (and <paramref name="seed"/>).</summary>
    private static byte[] Pattern(int width, int height, int seed = 0)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(x + seed);
                pixels[i + 1] = (byte)y;
                pixels[i + 2] = (byte)(x ^ y);
                pixels[i + 3] = 0xFF;
            }
        }

        return pixels;
    }
}
