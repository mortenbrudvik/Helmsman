using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using Rgb = Helmsman.FixtureApp.GlFrameCounterLayout.Rgb;

namespace Helmsman.E2ETests.HarnessTests;

/// <summary>
/// The GL frame counter's layout (shared with the fixture), the decoder that reads a frame number back from a capture, and the
/// timeline of the frames the fixture reports. Pure tests: the images are drawn here with the fixture's layout, as the fixture
/// draws them with OpenGL; nothing is started.
/// </summary>
public class GlFrameCounterTests
{
    [Theory]
    [InlineData(640, 360, 0L)]
    [InlineData(640, 360, 1L)]
    [InlineData(640, 360, 1234L)]
    [InlineData(3840, 2160, 987_654L)]
    [InlineData(1920, 1080, GlFrameCounterLayout.FrameMask)]
    public void Decode_AnImageDrawnWithTheLayout_ReadsTheFrame(int width, int height, long frame)
    {
        // Arrange
        using var image = Draw(width, height, frame);

        // Act
        var reading = GlFrameDecoder.Decode(image);

        // Assert
        reading.Frame.Should().Be(frame, reading.Description);
    }

    [Fact]
    public void Decode_AFrameAboveTheMask_ReadsItModuloTheRow()
    {
        using var image = Draw(640, 360, GlFrameCounterLayout.FrameMask + 6);

        GlFrameDecoder.Decode(image).Frame.Should().Be(5);
    }

    [Theory]
    [InlineData(3840, 2160, 1568, 882)]
    [InlineData(3840, 2160, 800, 450)]
    [InlineData(3840, 2160, 400, 225)]
    [InlineData(2560, 1440, 1568, 882)]
    [InlineData(640, 360, 320, 180)]
    public void Decode_ACaptureScaledDownByMaxDimension_ReadsTheFrame(int width, int height, int scaledWidth, int scaledHeight)
    {
        // Arrange - a capture scales the whole client area, as maxDimension does
        using var full = Draw(width, height, 31_337);
        using var scaled = Scale(full, scaledWidth, scaledHeight);

        // Act
        var reading = GlFrameDecoder.Decode(scaled);

        // Assert
        reading.Frame.Should().Be(31_337, reading.Description);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(80)]
    public void Decode_AJpegCapture_ReadsTheFrame(int quality)
    {
        // Arrange - JPEG's chroma subsampling and block noise, at the server's own quality and below it
        using var full = Draw(3840, 2160, 424_242);
        using var scaled = Scale(full, 1568, 882);
        using var jpeg = ThroughJpeg(scaled, quality);

        // Act
        var reading = GlFrameDecoder.Decode(jpeg);

        // Assert
        reading.Frame.Should().Be(424_242, reading.Description);
    }

    [Fact]
    public void Decode_WithColourNoiseOnEveryPixel_ReadsTheFrame()
    {
        // Arrange - up to 40 per channel either way, independently per pixel
        using var image = Draw(800, 450, 777_777);
        var random = new Random(4711);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image.GetPixel(x, y);
                image.SetPixel(x, y, Color.FromArgb(Noisy(pixel.R, random), Noisy(pixel.G, random), Noisy(pixel.B, random)));
            }
        }

        // Act
        var reading = GlFrameDecoder.Decode(image);

        // Assert
        reading.Frame.Should().Be(777_777, reading.Description);
    }

    [Fact]
    public void Decode_WithTheMarkersSwapped_FindsNothing()
    {
        using var image = Draw(640, 360, 99, start: GlFrameCounterLayout.EndMarker, end: GlFrameCounterLayout.StartMarker);

        var reading = GlFrameDecoder.Decode(image);

        reading.Found.Should().BeFalse();
        reading.Description.Should().Contain("start cell");
    }

    [Fact]
    public void Decode_WithBlueMarkers_FindsNothing()
    {
        using var image = Draw(640, 360, 99, start: new Rgb(0, 0, 0xFF), end: new Rgb(0, 0, 0xFF));

        GlFrameDecoder.Decode(image).Found.Should().BeFalse();
    }

    [Fact]
    public void Decode_WithAWrongEndMarker_FindsNothing()
    {
        using var image = Draw(640, 360, 99, end: GlFrameCounterLayout.One);

        GlFrameDecoder.Decode(image).Description.Should().Contain("end cell");
    }

    [Fact]
    public void Decode_WithOneBitFlipped_FailsTheParityCheck()
    {
        // Arrange - frame 6's cells with its lowest data bit set, as frame 7 has it, but frame 6's parity bit
        var colors = GlFrameCounterLayout.CellColors(6).ToArray();
        colors[GlFrameCounterLayout.DataBits] = GlFrameCounterLayout.One;
        using var image = Draw(640, 360, colors);

        // Act
        var reading = GlFrameDecoder.Decode(image);

        // Assert
        reading.Found.Should().BeFalse();
        reading.Description.Should().Contain("parity");
    }

    [Fact]
    public void Decode_ACellThatIsNeitherWhiteNorBlack_FindsNothing()
    {
        // Arrange - a grey cell, as a capture blending two frames could show
        var colors = GlFrameCounterLayout.CellColors(6).ToArray();
        colors[3] = new Rgb(0x80, 0x80, 0x80);
        using var image = Draw(640, 360, colors);

        GlFrameDecoder.Decode(image).Description.Should().Contain("cell 3");
    }

    [Fact]
    public void Decode_AnImageWithoutTheRow_FindsNothing()
    {
        // Arrange - the background only, as a capture of another window would not show the row either
        using var image = new Bitmap(640, 360);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(ToColor(GlFrameCounterLayout.Background));
        }

        GlFrameDecoder.Decode(image).Found.Should().BeFalse();
    }

    [Fact]
    public void Decode_TheClientAreaInsideALargerImage_ReadsTheFrame()
    {
        // Arrange - a screen capture with the window at (300, 200)
        using var window = Draw(640, 360, 4321);
        using var screen = new Bitmap(1600, 900);
        using (var graphics = Graphics.FromImage(screen))
        {
            graphics.Clear(Color.White);
            graphics.DrawImageUnscaled(window, 300, 200);
        }

        // Act
        var reading = GlFrameDecoder.Decode(screen, new Rectangle(300, 200, 640, 360));

        // Assert
        reading.Frame.Should().Be(4321, reading.Description);
    }

    [Theory]
    [InlineData(160, 160)]
    [InlineData(640, 360)]
    [InlineData(3840, 2160)]
    [InlineData(2160, 3840)]
    public void Layout_KeepsTheRowInTheTopQuarter_WithCellsApart_AndTheCloseBlockAndNeutralPointAwayFromIt(int width, int height)
    {
        // Arrange
        var cells = GlFrameCounterLayout.Cells(width, height);
        var close = GlFrameCounterLayout.CloseBlock(width, height);
        var (neutralX, neutralY) = GlFrameCounterLayout.NeutralPoint(width, height);

        // Assert - inside the client area, in its top quarter (see the remarks on GlFrameCounterLayout for when the main
        // window can cover it), one cell per bit and marker, each apart from the next
        cells.Should().HaveCount(GlFrameCounterLayout.CellCount);
        cells.Should().OnlyContain(c => c.X >= 0 && c.Y >= 0 && c.X + c.Width <= width && c.Y + c.Height <= height / 4 + 1);
        cells.Zip(cells.Skip(1)).Should().OnlyContain(pair => pair.First.X + pair.First.Width < pair.Second.X);
        close.X.Should().BeGreaterThanOrEqualTo(0);
        (close.X + close.Width).Should().BeLessThanOrEqualTo(width);
        (close.Y + close.Height).Should().BeLessThanOrEqualTo(height);
        cells.Should().NotContain(c => c.IntersectsWith(close));
        cells.Should().NotContain(c => c.Contains(neutralX, neutralY));
        close.Contains(neutralX, neutralY).Should().BeFalse("a click on the neutral point must not close the window");
    }

    [Fact]
    public void CellColors_StartRed_EndGreen_AndHoldAnEvenNumberOfOnes()
    {
        foreach (var frame in new[] { 0L, 1L, 2L, 3L, 1000L, GlFrameCounterLayout.FrameMask })
        {
            var colors = GlFrameCounterLayout.CellColors(frame);
            colors.Should().HaveCount(GlFrameCounterLayout.CellCount);
            colors[0].Should().Be(GlFrameCounterLayout.StartMarker);
            colors[^1].Should().Be(GlFrameCounterLayout.EndMarker);
            colors.Skip(1).Take(GlFrameCounterLayout.DataBits + 1).Should()
                .OnlyContain(c => c == GlFrameCounterLayout.One || c == GlFrameCounterLayout.Zero);
            (colors.Count(c => c == GlFrameCounterLayout.One) % 2).Should().Be(0, "frame {0} has even parity", frame);
        }
    }

    [Fact]
    public void FrameAt_InterpolatesBetweenReports_AndClampsOutsideThem()
    {
        // Arrange - 60 frames per second, reported every 100 ms
        var start = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var timeline = new GlFrameTimeline([(start, 100), (start.AddMilliseconds(100), 106), (start.AddMilliseconds(200), 112)]);

        // Assert
        timeline.FrameAt(start.AddMilliseconds(-50)).Should().Be(100);
        timeline.FrameAt(start).Should().Be(100);
        timeline.FrameAt(start.AddMilliseconds(50)).Should().Be(103);
        timeline.FrameAt(start.AddMilliseconds(150)).Should().Be(109);
        timeline.FrameAt(start.AddMilliseconds(500)).Should().Be(112);
        timeline.FramesPerSecond.Should().BeApproximately(60, 0.001);
        timeline.CurrentRange(start.AddMilliseconds(150), start.AddMilliseconds(200), TimeSpan.FromMilliseconds(100))
            .Should().Be((103L, 112L));
        new GlFrameTimeline([]).FrameAt(start).Should().Be(0);
    }

    [Fact]
    public void FramesPerSecondSince_CountsOnlyTheReportsFromThatMoment()
    {
        // Arrange - 60 frames per second for 200 ms, then the same frame reported for 200 ms: a window that stopped drawing
        var start = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var timeline = new GlFrameTimeline([
            (start, 100), (start.AddMilliseconds(100), 106), (start.AddMilliseconds(200), 112),
            (start.AddMilliseconds(300), 112), (start.AddMilliseconds(400), 112)
        ]);

        // Assert
        timeline.FramesPerSecond.Should().BePositive("over all the reports the window drew");
        timeline.FramesPerSecondSince(start).Should().BeApproximately(30, 0.001);
        timeline.FramesPerSecondSince(start.AddMilliseconds(100)).Should().BeApproximately(20, 0.001);
        timeline.FramesPerSecondSince(start.AddMilliseconds(200)).Should().Be(0, "it stopped drawing then");
        timeline.FramesPerSecondSince(start.AddMilliseconds(350)).Should().Be(0, "one report gives no rate");
        timeline.FramesPerSecondSince(start.AddMilliseconds(500)).Should().Be(0);
    }

    [Theory]
    [InlineData(42L, 42L, GlFrameAfterInput.SameFrame)]
    [InlineData(42L, 43L, GlFrameAfterInput.OtherFrame)]
    [InlineData(42L, null, GlFrameAfterInput.Unreadable)]
    [InlineData(null, 42L, GlFrameAfterInput.Unreadable)]
    [InlineData(null, null, GlFrameAfterInput.Unreadable)]
    public void CompareAfterInput_TellsTheSameFrameFromAnotherAndFromNoReading(long? before, long? after, GlFrameAfterInput expected)
    {
        var comparison = GlFrameReading.CompareAfterInput(new GlFrameReading(before, "before"), new GlFrameReading(after, "after"));

        comparison.Should().Be(expected);
    }

    [Fact]
    public void CompareAfterInput_TwoImagesOfOneFrame_AreTheSameFrame_AndOfTwoFrames_AnotherFrame()
    {
        // Arrange - decoded from drawn images, as the scenario decodes its captures
        using var frozen = Draw(1568, 882, 5000);
        using var again = Draw(1568, 882, 5000);
        using var moved = Draw(1568, 882, 5001);
        using var blank = new Bitmap(1568, 882);

        // Act
        var before = GlFrameDecoder.Decode(frozen);

        // Assert
        GlFrameReading.CompareAfterInput(before, GlFrameDecoder.Decode(again)).Should().Be(GlFrameAfterInput.SameFrame);
        GlFrameReading.CompareAfterInput(before, GlFrameDecoder.Decode(moved)).Should().Be(GlFrameAfterInput.OtherFrame);
        GlFrameReading.CompareAfterInput(before, GlFrameDecoder.Decode(blank)).Should().Be(GlFrameAfterInput.Unreadable);
    }

    [Theory]
    [InlineData(105L, 100L, 110L, true)]
    [InlineData(100L, 100L, 110L, true)]
    [InlineData(110L, 100L, 110L, true)]
    [InlineData(99L, 100L, 110L, false)]
    [InlineData(111L, 100L, 110L, false)]
    [InlineData(105L, 110L, 100L, false)]
    public void IsInRange_WithoutAWrap_IsAPlainRangeCheck(long decoded, long lowest, long highest, bool expected)
    {
        GlFrameTimeline.IsInRange(decoded, lowest, highest).Should().Be(expected);
    }

    [Fact]
    public void IsInRange_ComparesModuloTheRow_AcrossItsWrap()
    {
        // Arrange - the fixture's frames run past 2^20 while the row shows them modulo 2^20
        const long rowSize = GlFrameCounterLayout.FrameMask + 1;
        const long lowest = rowSize - 3;
        const long highest = rowSize + 4;

        // Assert - the frames on both sides of the wrap, as the decoder reads them
        GlFrameTimeline.IsInRange(GlFrameCounterLayout.FrameMask - 2, lowest, highest).Should().BeTrue();
        GlFrameTimeline.IsInRange(GlFrameCounterLayout.FrameMask, lowest, highest).Should().BeTrue();
        GlFrameTimeline.IsInRange(0, lowest, highest).Should().BeTrue();
        GlFrameTimeline.IsInRange(4, lowest, highest).Should().BeTrue();
        GlFrameTimeline.IsInRange(5, lowest, highest).Should().BeFalse();
        GlFrameTimeline.IsInRange(GlFrameCounterLayout.FrameMask - 3, lowest, highest).Should().BeFalse();

        // ... and a range far above the row, without a wrap inside it
        GlFrameTimeline.IsInRange(15, 3 * rowSize + 10, 3 * rowSize + 20).Should().BeTrue();
        GlFrameTimeline.IsInRange(25, 3 * rowSize + 10, 3 * rowSize + 20).Should().BeFalse();
    }

    [Fact]
    public void IsInRange_TheFrameOfAnImageDrawnPastTheWrap_IsInTheFixturesRange()
    {
        // Arrange - the fixture swapped frame 2^20 + 7 and reported the frames around it unmasked
        const long swapped = GlFrameCounterLayout.FrameMask + 8;
        using var image = Draw(640, 360, swapped);

        // Act
        var reading = GlFrameDecoder.Decode(image);

        // Assert
        reading.Frame.Should().Be(7, reading.Description);
        GlFrameTimeline.IsInRange(reading.Frame!.Value, swapped - 2, swapped + 2).Should().BeTrue();
        GlFrameTimeline.IsInRange(reading.Frame.Value, swapped + 1, swapped + 5).Should().BeFalse();
    }

    [Fact]
    public void Timeline_From_ReadsTheFrameEventsOnly_WithTheirTimes()
    {
        // Arrange - event lines as the fixture writes them
        var events = new[]
        {
            FixtureEvent.Parse("""{"seq":1,"event":"glFrame","time":"2026-11-23T13:00:00.0000000+01:00","frame":10}"""),
            FixtureEvent.Parse("""{"seq":2,"event":"ready","time":"2026-11-23T13:00:00.0500000+01:00"}"""),
            FixtureEvent.Parse("""{"seq":3,"event":"glFrame","time":"2026-11-23T13:00:00.1000000+01:00","frame":16}""")
        };

        // Act
        var timeline = GlFrameTimeline.From(events);

        // Assert
        timeline.Count.Should().Be(2);
        timeline.FrameAt(new DateTimeOffset(2026, 11, 23, 12, 0, 0, 50, TimeSpan.Zero)).Should().Be(13);
    }

    [Fact]
    public void FixtureGlWindow_From_ReadsTheOpenedEvent()
    {
        // Arrange
        var opened = FixtureEvent.Parse(JsonSerializer.Serialize(new
        {
            seq = 7,
            @event = FixtureContract.Events.GlOpened,
            time = "2026-11-23T13:00:00.0000000+01:00",
            hwnd = 0x1234,
            title = FixtureContract.GlTitle("abc"),
            mode = "640x360",
            windowRect = new { x = 10, y = 20, width = 640, height = 360 },
            clientRect = new { x = 10, y = 20, width = 640, height = 360 },
            monitorRect = new { x = 0, y = 0, width = 3840, height = 2160 },
            vsync = true,
            vendor = "Vendor",
            renderer = "Renderer",
            lifetimeMs = 20000,
            closeRect = new { x = 600, y = 340, width = 50, height = 50 },
            neutralPoint = new { x = 170, y = 290 }
        }));

        // Act
        var window = FixtureGlWindow.From(opened);

        // Assert
        window.Handle.Should().Be(0x1234);
        window.Mode.Should().Be("640x360");
        window.ClientRect.Should().Be(new ScreenRect(10, 20, 640, 360));
        window.MonitorRect.Should().Be(new ScreenRect(0, 0, 3840, 2160));
        window.VSync.Should().BeTrue();
        window.NeutralPoint.Should().Be((170, 290));
    }

    /// <summary>A client area as the fixture draws it for <paramref name="frame"/>, optionally with other marker colors.</summary>
    private static Bitmap Draw(int width, int height, long frame, Rgb? start = null, Rgb? end = null)
    {
        var colors = GlFrameCounterLayout.CellColors(frame).ToArray();
        colors[0] = start ?? colors[0];
        colors[^1] = end ?? colors[^1];
        return Draw(width, height, colors);
    }

    /// <summary>A client area with the background, the row's cells in <paramref name="colors"/> and the close block.</summary>
    private static Bitmap Draw(int width, int height, IReadOnlyList<Rgb> colors)
    {
        var image = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(ToColor(GlFrameCounterLayout.Background));
        var cells = GlFrameCounterLayout.Cells(width, height);
        for (var i = 0; i < cells.Count; i++)
        {
            using var brush = new SolidBrush(ToColor(colors[i]));
            graphics.FillRectangle(brush, cells[i].X, cells[i].Y, cells[i].Width, cells[i].Height);
        }

        var close = GlFrameCounterLayout.CloseBlock(width, height);
        using var closeBrush = new SolidBrush(ToColor(GlFrameCounterLayout.CloseBlockColor));
        graphics.FillRectangle(closeBrush, close.X, close.Y, close.Width, close.Height);
        return image;
    }

    private static Bitmap Scale(Bitmap source, int width, int height)
    {
        var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, width, height);
        return scaled;
    }

    private static Bitmap ThroughJpeg(Bitmap source, int quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        var stream = new MemoryStream();
        source.Save(stream, codec, parameters);
        stream.Position = 0;
        return new Bitmap(stream);
    }

    private static int Noisy(int channel, Random random) => Math.Clamp(channel + random.Next(-40, 41), 0, 255);

    private static Color ToColor(Rgb color) => Color.FromArgb(color.R, color.G, color.B);
}
