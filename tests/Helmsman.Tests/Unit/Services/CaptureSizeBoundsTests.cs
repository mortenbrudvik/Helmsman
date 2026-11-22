using ModelContextProtocol.Protocol;
using Helmsman.Tools;

namespace Helmsman.Tests.Unit.Services;

/// <summary>
/// How large an image the capture tools make, return and write: what each route returns at its full size, at the pixel
/// budget of one capture, and around it, for regions that are negative, enormous or off the screen. The capture layer is a
/// mock, so nothing is captured from the screen; long, thin strips stand in for wide captures where only an edge matters,
/// so the tests stay small.
/// </summary>
public sealed class CaptureSizeBoundsTests : IDisposable
{
    private static readonly nint Window = new(0x7000);

    private readonly Mock<IScreenCaptureWrapper> _capture = TestDataFactory.CreateMockScreenCaptureWrapper();
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "helmsman-capture-size-tests", Guid.NewGuid().ToString("N"));

    public CaptureSizeBoundsTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private ScreenshotService CreateSut(IGraphicsCaptureWrapper? graphicsCapture = null) =>
        new(_capture.Object, _monitors.Object, Mock.Of<INativeWindowWrapper>(w => w.IsWindowVisible(It.IsAny<nint>()) == true),
            Permissive.CapturePolicy, Permissive.TargetPolicy, Permissive.SecurityProbe, graphicsCapture: graphicsCapture);

    private static CaptureOptions FullResolution(CaptureOutputMode output = CaptureOutputMode.Image) =>
        new() { MaxDimension = 0, Output = output };

    #region What comes back at full size today

    [Fact]
    public void AnEightKMonitorAtFullResolution_ComesBackInline_AtItsOwnSize()
    {
        // Arrange - the largest single display: 7680 x 4320, 33.2 megapixels
        UseMonitors(TestDataFactory.CreateMonitorInfo(boundsWidth: 7680, boundsHeight: 4320));

        // Act
        var capture = CreateSut().CaptureMonitor(0, FullResolution());

        // Assert
        var metadata = capture.Metadata;
        metadata.Success.Should().BeTrue(metadata.Error);
        (metadata.Width, metadata.Height).Should().Be((7680, 4320));
        metadata.OriginalWidth.Should().BeNull();
        metadata.ImageToScreen.Should().Be(new ImageToScreenTransform(0, 0, 1, 1));
        capture.Image.Should().NotBeNull();
        capture.Image!.Data.LongLength.Should().Be(metadata.ByteSize);
    }

    [Fact]
    public void TwoFourKMonitorsAtFullResolution_ComeBackInline_AsOneImageOfTheVirtualScreen()
    {
        // Arrange
        UseMonitors(
            TestDataFactory.CreateMonitorInfo(index: 0, boundsWidth: 3840, boundsHeight: 2160),
            TestDataFactory.CreateMonitorInfo(index: 1, isPrimary: false, boundsX: 3840, boundsWidth: 3840, boundsHeight: 2160));
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 7680, 2160));

        // Act
        var capture = CreateSut().CaptureMonitor(ScreenshotService.VirtualScreenIndex, FullResolution());

        // Assert
        capture.Metadata.Success.Should().BeTrue(capture.Metadata.Error);
        (capture.Metadata.Width, capture.Metadata.Height).Should().Be((7680, 2160));
        capture.Metadata.MonitorIndex.Should().BeNull();
        capture.Image.Should().NotBeNull();
    }

    [Fact]
    public void ARegion8000PixelsLong_ComesBackInline_AtFullSize()
    {
        // Arrange
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 8000, 100));

        // Act
        var capture = CreateSut().CaptureRegion(0, 0, 8000, 2, FullResolution());

        // Assert
        capture.Metadata.Success.Should().BeTrue(capture.Metadata.Error);
        (capture.Metadata.Width, capture.Metadata.Height).Should().Be((8000, 2));
        capture.Image.Should().NotBeNull();
    }

    [Theory]
    [InlineData(CaptureOutputMode.Metadata)]
    [InlineData(CaptureOutputMode.File)]
    public void AFileOrMetadataOutput_KeepsTheWholePixelBudget_16384PixelsOnAnEdge(CaptureOutputMode output)
    {
        // Arrange
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 16384, 10));
        var options = FullResolution(output) with
        {
            OutputPath = output == CaptureOutputMode.File ? Path.Combine(_folder, "strip.png") : null,
            Format = CaptureFormat.Png
        };

        // Act
        var capture = CreateSut().CaptureRegion(0, 0, 16384, 2, options);

        // Assert
        capture.Metadata.Success.Should().BeTrue(capture.Metadata.Error);
        (capture.Metadata.Width, capture.Metadata.Height).Should().Be((16384, 2));
        capture.Image.Should().BeNull();
        if (output == CaptureOutputMode.File)
        {
            new FileInfo(capture.Metadata.FilePath!).Length.Should().Be(capture.Metadata.ByteSize);
        }
    }

    [Fact]
    public void AZoomedStrip8000PixelsLong_ComesBackInline()
    {
        // Arrange
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 2000, 10));

        // Act - 2000 x 1 at zoom 4 is 8000 x 4
        var capture = CreateSut().CaptureRegion(0, 0, 2000, 1, FullResolution() with { Zoom = 4, Format = CaptureFormat.Png });

        // Assert
        capture.Metadata.Success.Should().BeTrue(capture.Metadata.Error);
        (capture.Metadata.Width, capture.Metadata.Height).Should().Be((8000, 4));
        capture.Metadata.ImageToScreen.Should().Be(new ImageToScreenTransform(0, 0, 0.25, 0.25));
        capture.Image.Should().NotBeNull();
    }

    [Fact]
    public void TheLargestImageAtTheDefaultSize_AnExplicitPngOfNoise_ComesBackInline()
    {
        // Arrange - pure noise does not compress: about 8.4 MB as PNG at 1568 x 1568, the most a default-size image can be
        UseNoise(1568, 1568);

        // Act
        var capture = CreateSut().CaptureRegion(0, 0, 1568, 1568, new CaptureOptions { Format = CaptureFormat.Png });

        // Assert
        capture.Metadata.Success.Should().BeTrue(capture.Metadata.Error);
        capture.Metadata.ByteSize.Should().BeGreaterThan(8_000_000);
        capture.Image!.Data.LongLength.Should().Be(capture.Metadata.ByteSize);
    }

    [Fact]
    public void EveryWindowRoute_AtTheDefaultSize_ScalesA4KWindowTo1568_AndMapsItsPointsBack()
    {
        // Arrange - a 3840 x 2160 window whose corner is at (-3840, 100)
        UseWindow(3840, 2160, -3840, 100);
        var sut = CreateSut();
        var options = new CaptureOptions();

        // Act
        var window = sut.CaptureWindow(Window, includeFrame: true, options).Metadata;
        var area = sut.CaptureWindowArea(Window, includeFrame: true, new BoundsDto(-3840, 100, 3840, 2160), options).Metadata;
        var marked = sut.CaptureWindowWithElements(Window, null, [], includeFrame: true, ElementOverlayOptions.Default, options).Metadata;

        // Assert
        foreach (var (success, width, height, transform) in new[]
                 {
                     (window.Success, window.Width, window.Height, window.ImageToScreen),
                     (area.Success, area.Width, area.Height, area.ImageToScreen),
                     (marked.Success, marked.Width, marked.Height, marked.ImageToScreen)
                 })
        {
            success.Should().BeTrue();
            (width, height).Should().Be((1568, 882));
            transform!.ToScreen(784, 441).Should().Be((-3840 + 1920, 100 + 1080));
        }
    }

    #endregion

    #region Regions that are negative, enormous or off the screen

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-5, 10)]
    [InlineData(10, int.MinValue)]
    public void ARegionWithoutAPositiveSize_IsInvalidRegion_BeforeAnythingIsCaptured(int width, int height)
    {
        // Act
        var metadata = CreateSut().CaptureRegion(0, 0, width, height, FullResolution()).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidRegion));
        VerifyNothingCaptured();
    }

    [Fact]
    public void ARegionOnScreenOverThePixelBudget_IsInvalidRegion_BeforeAnythingIsCaptured()
    {
        // Arrange - 16384 x 4097 is just over 64 megapixels
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 16384, 16384));

        // Act
        var metadata = CreateSut().CaptureRegion(0, 0, 16384, 4097, FullResolution(CaptureOutputMode.Metadata)).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidRegion));
        metadata.Error.Should().Contain("64 megapixels");
        VerifyNothingCaptured();
    }

    [Fact]
    public void ARegionOnScreenOverTheLongestEdge_IsInvalidRegion_BeforeAnythingIsCaptured()
    {
        // Arrange
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 20000, 10));

        // Act
        var metadata = CreateSut().CaptureRegion(0, 0, 16385, 1, FullResolution(CaptureOutputMode.Metadata)).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidRegion));
        metadata.Error.Should().Contain("16384");
        VerifyNothingCaptured();
    }

    [Fact]
    public void AnEnormousRegion_IsCutToTheScreen_AndOnlyThatIsCaptured()
    {
        // Act - the region asked is 2 billion pixels wide; the screen is 1920 x 1080
        var metadata = CreateSut().CaptureRegion(-1_000_000_000, -1_000_000_000, 2_000_000_000, 2_000_000_000, FullResolution()).Metadata;

        // Assert
        metadata.Success.Should().BeTrue(metadata.Error);
        metadata.CapturedRegion.Should().Be(new CapturedRegion(0, 0, 1920, 1080));
        metadata.Warnings.Should().Contain(w => w.StartsWith("RegionClipped:"));
        _capture.Verify(c => c.CaptureScreenRegion(0, 0, 1920, 1080), Times.Once);
    }

    [Fact]
    public void ARegionFarOffTheScreen_IsRegionOffScreen_BeforeAnythingIsCaptured()
    {
        // Act
        var metadata = CreateSut().CaptureRegion(int.MinValue, int.MinValue, 100, 100, FullResolution()).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.RegionOffScreen));
        VerifyNothingCaptured();
    }

    [Fact]
    public void AVirtualScreenOverThePixelBudget_IsCaptureFailed_BeforeAnythingIsCaptured()
    {
        // Arrange - three 8K monitors side by side: 23040 x 4320, 99.5 megapixels
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 23040, 4320));

        // Act
        var metadata = CreateSut().CaptureMonitor(ScreenshotService.VirtualScreenIndex, FullResolution(CaptureOutputMode.Metadata)).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.CaptureFailed));
        metadata.Error.Should().Contain("23040x4320");
        VerifyNothingCaptured();
    }

    #endregion

    #region Inline images over the bound

    [Theory]
    [InlineData(8001, 2)] // one pixel over the longest inline edge
    [InlineData(8000, 4195)] // within the edge, but 33,560,000 pixels: over 32 megapixels (33,554,432)
    public void CaptureRegion_WhoseInlineImageWouldBeOverTheBound_IsInvalidImageSize_BeforeAnythingIsCaptured(int width, int height)
    {
        // Arrange
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, width, height));

        // Act
        var capture = CreateSut().CaptureRegion(0, 0, width, height, FullResolution());

        // Assert
        capture.Metadata.Success.Should().BeFalse();
        capture.Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidImageSize));
        capture.Metadata.Error.Should().Contain($"{width}x{height}").And.Contain("8000 pixels on an edge").And.Contain("32 megapixels")
            .And.Contain("output 'file'").And.Contain("maxDimension");
        capture.Image.Should().BeNull();
        VerifyNothingCaptured();
    }

    [Fact]
    public void TakeScreenshot_OfThreeFourKMonitorsAtFullResolution_IsInvalidImageSize_BeforeAnythingIsCaptured()
    {
        // Arrange - 11520 x 2160: within the 64-megapixel budget, but no client takes an image 11520 pixels wide
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 11520, 2160));

        // Act
        var capture = CreateSut().CaptureMonitor(ScreenshotService.VirtualScreenIndex, FullResolution());

        // Assert
        capture.Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidImageSize));
        capture.Metadata.Error.Should().Contain("11520x2160");
        capture.Image.Should().BeNull();
        VerifyNothingCaptured();
    }

    [Fact]
    public void TakeScreenshot_OfAMonitorOverTheInlineEdge_IsInvalidImageSize_BeforeAnythingIsCaptured()
    {
        // Arrange
        UseMonitors(TestDataFactory.CreateMonitorInfo(boundsWidth: 8001, boundsHeight: 2));

        // Act
        var capture = CreateSut().CaptureMonitor(0, FullResolution());

        // Assert
        capture.Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidImageSize));
        VerifyNothingCaptured();
    }

    [Fact]
    public void EveryWindowRoute_WhoseInlineImageWouldBeOverTheBound_IsInvalidImageSize_WithoutAnImage()
    {
        // Arrange - a window's size is known only with its pixels, so it is refused before its image is made
        UseWindow(8001, 2, 0, 0);
        var sut = CreateSut();

        // Act
        var window = sut.CaptureWindow(Window, includeFrame: true, FullResolution());
        var area = sut.CaptureWindowArea(Window, includeFrame: true, new BoundsDto(0, 0, 8001, 2), FullResolution());
        var marked = sut.CaptureWindowWithElements(Window, null, [], includeFrame: true, ElementOverlayOptions.Default, FullResolution());

        // Assert
        window.Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidImageSize));
        window.Image.Should().BeNull();
        area.Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidImageSize));
        area.Image.Should().BeNull();
        marked.Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidImageSize));
        marked.Image.Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_OverTheInlineBound_WithAMetadataOutput_KeepsItsFullSize()
    {
        // Arrange - only an image in the result is held to the inline bound
        UseWindow(8001, 2, 0, 0);

        // Act
        var capture = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution(CaptureOutputMode.Metadata));

        // Assert
        capture.Metadata.Success.Should().BeTrue(capture.Metadata.Error);
        (capture.Metadata.Width, capture.Metadata.Height).Should().Be((8001, 2));
    }

    [Fact]
    public void CaptureRegion_AZoomThatWouldTakeTheInlineImageOverTheBound_IsInvalidImageSize_BeforeAnythingIsCaptured()
    {
        // Arrange - 2001 x 1 at zoom 4 is 8004 x 4
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 2001, 10));

        // Act
        var capture = CreateSut().CaptureRegion(0, 0, 2001, 1, FullResolution() with { Zoom = 4 });

        // Assert
        capture.Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidImageSize));
        capture.Metadata.Error.Should().Contain("8004x4");
        VerifyNothingCaptured();
    }

    [Fact]
    public void CaptureRegion_WithOnlyTheLegacyMaxWidth_IsHeldToTheInlineBoundOnItsHeight()
    {
        // Arrange - maxWidth alone lifts the 1568 long-edge default, so a tall strip keeps its height
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 1920, 8001));

        // Act
        var capture = CreateSut().CaptureRegion(0, 0, 100, 8001, new CaptureOptions { MaxWidth = 200 });

        // Assert
        capture.Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidImageSize));
        VerifyNothingCaptured();
    }

    [Fact]
    public void AnInlineImageOverTheByteBound_IsInvalidImageSize_WithoutAnImage()
    {
        // Arrange - a PNG of noise at 1900 x 1900 is about 12 MB, over the 10 MB an inline image may be
        UseNoise(1900, 1900);

        // Act
        var capture = CreateSut().CaptureRegion(0, 0, 1900, 1900, FullResolution() with { Format = CaptureFormat.Png });

        // Assert
        capture.Metadata.Success.Should().BeFalse();
        capture.Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidImageSize));
        capture.Metadata.Error.Should().Contain("10 MB").And.Contain("1900x1900").And.Contain("output 'file'");
        capture.Image.Should().BeNull();
    }

    [Fact]
    public void AnImageOverTheByteBound_WithOutputBoth_IsRefused_AndNoFileIsWritten()
    {
        // Arrange
        UseNoise(1900, 1900);
        var path = Path.Combine(_folder, "noise.png");

        // Act
        var capture = CreateSut().CaptureRegion(0, 0, 1900, 1900,
            FullResolution(CaptureOutputMode.Both) with { Format = CaptureFormat.Png, OutputPath = path });

        // Assert
        capture.Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidImageSize));
        File.Exists(path).Should().BeFalse();
    }

    [Theory]
    [InlineData(CaptureOutputMode.Metadata)]
    [InlineData(CaptureOutputMode.File)]
    public void AnImageOverTheByteBound_WithoutAnImageInTheResult_IsMadeAsBefore(CaptureOutputMode output)
    {
        // Arrange
        UseNoise(1900, 1900);
        var options = FullResolution(output) with
        {
            Format = CaptureFormat.Png,
            OutputPath = output == CaptureOutputMode.File ? Path.Combine(_folder, "noise.png") : null
        };

        // Act
        var capture = CreateSut().CaptureRegion(0, 0, 1900, 1900, options);

        // Assert
        capture.Metadata.Success.Should().BeTrue(capture.Metadata.Error);
        capture.Metadata.ByteSize.Should().BeGreaterThan(10L * 1024 * 1024);
    }

    #endregion

    #region The target policy comes first

    [Fact]
    public async Task CaptureWindow_OfADeniedWindow_IsTargetDeniedByPolicy_WhateverItsSize()
    {
        // Arrange - a window far larger than any image may be, which the policy denies
        UseWindow(16000, 4000, 0, 0);
        var tools = Tools(Denying());

        // Act
        var result = await tools.CaptureWindow(handle: (long)Window, maxDimension: 0);

        // Assert
        result.IsError.Should().BeTrue();
        ErrorCode(result).Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        _capture.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task CaptureWithElements_OfADeniedWindow_IsTargetDeniedByPolicy_WhateverItsSize()
    {
        // Arrange
        UseWindow(16000, 4000, 0, 0);
        var tools = Tools(Denying());

        // Act
        var result = await tools.CaptureWithElements(handle: (long)Window, maxDimension: 0);

        // Assert
        result.IsError.Should().BeTrue();
        ErrorCode(result).Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        _capture.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    #endregion

    private ScreenshotTools Tools(ICaptureTargetPolicy policy) =>
        new(CreateSut(), TestDataFactory.CreateMockUIElementService().Object, TestDataFactory.CreateMockWindowService().Object, policy,
            new Mock<IInputSession>().Object);

    private static ICaptureTargetPolicy Denying()
    {
        var policy = new Mock<ICaptureTargetPolicy>();
        var refusal = new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy, "The user's policy denies this window.");
        policy.Setup(p => p.Check(It.IsAny<nint>())).Returns(refusal);
        policy.Setup(p => p.Verdict(It.IsAny<WindowInfo?>())).Returns(TargetPolicyVerdict.Deny("vault", "denied by HELMSMAN_DENY_PROCESSES"));
        return policy.Object;
    }

    private static string? ErrorCode(CallToolResult result) =>
        System.Text.Json.JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.GetProperty("errorCode").GetString();

    private void UseMonitors(params MonitorInfo[] monitors)
    {
        _monitors.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var right = monitors.Max(m => m.Bounds.X + m.Bounds.Width);
        var bottom = monitors.Max(m => m.Bounds.Y + m.Bounds.Height);
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, right, bottom));
    }

    private void UseWindow(int width, int height, int x, int y) =>
        _capture.Setup(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()))
            .Returns(() => new CaptureResult(new byte[width * height * 4], width, height, x, y, "PrintWindow"));

    private void UseNoise(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        new Random(7).NextBytes(pixels);
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, Math.Max(width, 1920), Math.Max(height, 1080)));
        _capture.Setup(c => c.CaptureScreenRegion(0, 0, width, height))
            .Returns(() => new CaptureResult((byte[])pixels.Clone(), width, height, 0, 0, "ScreenBitBlt"));
    }

    private void VerifyNothingCaptured()
    {
        _capture.Verify(c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _capture.Verify(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }
}
