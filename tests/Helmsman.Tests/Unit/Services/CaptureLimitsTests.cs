namespace Helmsman.Tests.Unit.Services;

/// <summary>
/// The size bounds of <see cref="CaptureLimits"/>: the base64 length an image returned in a result may take, at and around
/// it, and the largest maxDimension a refusal suggests, which must pass when it is passed back, for any size and shape.
/// </summary>
public class CaptureLimitsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(7_499_999, 10_000_000)]
    [InlineData(7_500_000, 10_000_000)]
    [InlineData(7_500_001, 10_000_004)]
    public void Base64Length_IsFourCharactersForEveryThreeBytesOrPartOfThem(long bytes, long characters) =>
        CaptureLimits.Base64Length(bytes).Should().Be(characters);

    [Fact]
    public void TheEncodedByteBound_IsTheMostBytesWhoseBase64FitsTheBound()
    {
        CaptureLimits.MaxInlineBase64Length.Should().Be(10_000_000);
        CaptureLimits.MaxInlineEncodedBytes.Should().Be(7_500_000);
        CaptureLimits.FitsInlineBase64(CaptureLimits.MaxInlineEncodedBytes).Should().BeTrue();
        CaptureLimits.FitsInlineBase64(CaptureLimits.MaxInlineEncodedBytes + 1).Should().BeFalse();
        Convert.ToBase64String(new byte[300]).Length.Should().Be((int)CaptureLimits.Base64Length(300));
        Convert.ToBase64String(new byte[301]).Length.Should().Be((int)CaptureLimits.Base64Length(301));
    }

    [Theory]
    [InlineData(7_499_999, false)]
    [InlineData(7_500_000, false)] // exactly 10,000,000 characters of base64
    [InlineData(7_500_001, true)]
    public void ThrowIfEncodedTooLarge_AtAndAroundTheBound(long bytes, bool refused)
    {
        var request = CaptureRequest.Resolve(new CaptureOptions { MaxDimension = 0 });

        var act = () => request.ThrowIfEncodedTooLarge(bytes, ImageEncoding.Png, 2000, 2000);

        if (refused)
        {
            act.Should().Throw<CaptureException>().Which.Code.Should().Be(ScreenshotErrorCode.InvalidImageSize);
        }
        else
        {
            act.Should().NotThrow();
        }
    }

    [Theory]
    [InlineData(CaptureOutputMode.File)]
    [InlineData(CaptureOutputMode.Metadata)]
    public void ThrowIfEncodedTooLarge_AnImageNotReturned_IsNotHeldToTheBase64Bound(CaptureOutputMode output) =>
        CaptureRequest.Resolve(new CaptureOptions { MaxDimension = 0, Output = output })
            .Invoking(r => r.ThrowIfEncodedTooLarge(200_000_000, ImageEncoding.Png, 8192, 8192)).Should().NotThrow();

    [Theory]
    [InlineData(10240, 5760, 8000)] // 8000 x 4500 is 36 megapixels: "at most 8000" would be refused again
    [InlineData(6000, 6000, 8000)] // not scaled at 8000 at all
    [InlineData(11520, 2160, 0)]
    [InlineData(16384, 4096, 0)]
    public void TheRefusal_SuggestsTheLargestMaxDimensionThatPasses(int width, int height, int maxDimension)
    {
        // Arrange
        var request = CaptureRequest.Resolve(new CaptureOptions { MaxDimension = maxDimension });
        var suggested = request.SuggestedMaxDimension(width, height)!.Value;

        // Act
        var refusal = request.Invoking(r => r.ThrowIfOutputTooLarge(width, height)).Should().Throw<CaptureException>().Which;

        // Assert
        refusal.Message.Should().Contain($"maxDimension of at most {suggested}");
        CaptureRequest.Resolve(new CaptureOptions { MaxDimension = suggested })
            .Invoking(r => r.ThrowIfOutputTooLarge(width, height)).Should().NotThrow();
    }

    [Fact]
    public void TheSuggestedMaxDimension_PassedBack_Passes_AndOneMoreDoesNot_ForEverySizeShapeAndZoom()
    {
        int[] edges = [1, 2, 3, 7, 100, 513, 1568, 3000, 4097, 5792, 5793, 6000, 7999, 8000, 8001, 9999, 10240, 12000, 16384];
        double[] zooms = [1, 1.37, 2, 2.5, 4];
        int?[] maxWidths = [null, 1, 300, 5000, 9000];
        var checkedCount = 0;
        foreach (var width in edges)
        foreach (var height in edges)
        foreach (var zoom in zooms)
        foreach (var maxWidth in maxWidths)
        {
            if ((long)width * height > CaptureLimits.MaxCapturePixels)
            {
                continue;
            }

            var options = new CaptureOptions { MaxDimension = 0, Zoom = zoom, MaxWidth = maxWidth };
            var request = CaptureRequest.Resolve(options);
            var (outputWidth, outputHeight) = request.OutputSize(width, height);
            if (CaptureLimits.FitsInline(outputWidth, outputHeight))
            {
                continue;
            }

            var suggested = request.SuggestedMaxDimension(width, height);
            var because = $"{width}x{height} at zoom {zoom}, maxWidth {maxWidth}";
            suggested.Should().NotBeNull(because);
            CaptureRequest.Resolve(options with { MaxDimension = suggested })
                .Invoking(r => r.ThrowIfOutputTooLarge(width, height)).Should().NotThrow(because);
            if (suggested < CaptureLimits.MaxInlineEdge)
            {
                CaptureRequest.Resolve(options with { MaxDimension = suggested + 1 })
                    .Invoking(r => r.ThrowIfOutputTooLarge(width, height)).Should().Throw<CaptureException>(because);
            }

            checkedCount++;
        }

        checkedCount.Should().BeGreaterThan(500, "the sizes over the bound are many");
    }

    [Fact]
    public void BrowserScreenshot_TheSuggestedMaxDimension_FitsTheBound_AndOneMoreDoesNot_ForEverySizeAndShape()
    {
        int[] edges = [1, 64, 1568, 4097, 5792, 5793, 6000, 8000, 8001, 10240, 16384];
        foreach (var width in edges)
        foreach (var height in edges)
        {
            if (CaptureLimits.FitsInline(width, height) || (long)width * height > CaptureLimits.MaxCapturePixels)
            {
                continue;
            }

            var suggested = CaptureLimits.LargestFittingMaxDimension(m => BrowserPageService.ScaledSize(width, height, m))!.Value;
            var (scaledWidth, scaledHeight) = BrowserPageService.ScaledSize(width, height, suggested);
            CaptureLimits.FitsInline(scaledWidth, scaledHeight).Should().BeTrue($"{width}x{height} at {suggested}");
            suggested.Should().BeInRange(64, CaptureLimits.MaxInlineEdge, "browser_screenshot takes 64-8000");
            if (suggested < CaptureLimits.MaxInlineEdge)
            {
                var (largerWidth, largerHeight) = BrowserPageService.ScaledSize(width, height, suggested + 1);
                CaptureLimits.FitsInline(largerWidth, largerHeight).Should().BeFalse($"{width}x{height} at {suggested + 1}");
            }
        }
    }
}
