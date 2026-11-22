namespace Helmsman.Tests.Unit.Services;

/// <summary>
/// The names CaptureFileWriter gives the files of its default captures folder, and the check that recognises them, which is
/// all the capture file sweep deletes by: every name the writer can give is recognised, and nothing else is. Nothing here
/// touches the file system.
/// </summary>
public sealed class CaptureFileNameTests
{
    public static TheoryData<DateTime> Times() => new()
    {
        new DateTime(2026, 11, 22, 15, 30, 0),
        new DateTime(2028, 2, 29, 23, 59, 59), // a leap day, the last second of a day
        new DateTime(2026, 1, 1, 0, 0, 0),
        DateTime.MinValue,
        DateTime.MaxValue,
        new DateTime(2026, 3, 29, 2, 30, 0, DateTimeKind.Local), // a local time a clock change skips in Europe
        new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc)
    };

    [Theory]
    [MemberData(nameof(Times))]
    public void EveryNameTheWriterGives_IsRecognised(DateTime time)
    {
        foreach (var encoding in Enum.GetValues<ImageEncoding>())
        {
            foreach (var id in new[] { Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") })
            {
                var name = CaptureFileWriter.NewFileName(time, id, encoding);

                CaptureFileWriter.IsOwnFileName(name).Should().BeTrue("the writer gives '{0}'", name);
            }
        }
    }

    [Fact]
    public void TheName_IsTheTimeTheIdAndTheExtensionOfTheEncoding()
    {
        var id = Guid.Parse("0123456789abcdef0123456789abcdef");

        CaptureFileWriter.NewFileName(new DateTime(2026, 11, 22, 15, 30, 5), id, ImageEncoding.Png)
            .Should().Be("screenshot_20261122_153005_0123456789abcdef0123456789abcdef.png");
        CaptureFileWriter.NewFileName(new DateTime(2026, 11, 22, 15, 30, 5), id, ImageEncoding.Jpeg)
            .Should().Be("screenshot_20261122_153005_0123456789abcdef0123456789abcdef.jpg");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("screenshot_20261122_153005_0123456789abcdef0123456789abcdef")] // no extension
    [InlineData("screenshot_20261122_153005_0123456789abcdef0123456789abcdef.jpeg")] // an extension outputPath takes, the writer never gives
    [InlineData("screenshot_20261122_153005_0123456789abcdef0123456789abcdef.bmp")]
    [InlineData("screenshot_20261122_153005_0123456789abcdef0123456789abcdef.png.txt")]
    [InlineData("screenshot_20261122_153005_0123456789abcdef0123456789abcdef.PNG")] // another case
    [InlineData("Screenshot_20261122_153005_0123456789abcdef0123456789abcdef.png")]
    [InlineData("screenshot_20261122_153005_0123456789ABCDEF0123456789ABCDEF.png")] // uppercase digits
    [InlineData("screenshot_20261122_153005_0123456789abcdef0123456789abcde.png")] // 31 digits
    [InlineData("screenshot_20261122_153005_0123456789abcdef0123456789abcdef0.png")] // 33 digits
    [InlineData("screenshot_20261122_153005_0123456789abcdef0123456789abcdeg.png")] // not hex
    [InlineData("screenshot_20261122_153005_01234567-89ab-cdef-0123-456789abcdef.png")] // the GUID with dashes
    [InlineData("screenshot_20261122_153005-0123456789abcdef0123456789abcdef.png")] // another separator
    [InlineData("screenshot_20261122153005_0123456789abcdef0123456789abcdef.png")]
    [InlineData("screenshot_20261322_153005_0123456789abcdef0123456789abcdef.png")] // month 13
    [InlineData("screenshot_20261122_256005_0123456789abcdef0123456789abcdef.png")] // hour 25
    [InlineData("screenshot_2026112_1530050_0123456789abcdef0123456789abcdef.png")] // the digits in other places
    [InlineData("screenshot_20261122_153005_0123456789abcdef0123456789abcdef.png ")] // a trailing space
    [InlineData("screenshot_20261122_153005_0123456789abcdef0123456789abcdef.png.")] // a trailing dot
    [InlineData("screenshot_20261122_153005_0123456789abcdef0123456789abcdef.png:zone")] // an alternate data stream
    [InlineData(" screenshot_20261122_153005_0123456789abcdef0123456789abcdef.png")]
    [InlineData("xscreenshot_20261122_153005_0123456789abcdef0123456789abcdef.png")]
    [InlineData("screenshot_20261122_153005_0123456789abcdef0123456789abcdef(1).png")] // a copy Explorer names
    [InlineData(@"sub\screenshot_20261122_153005_0123456789abcdef0123456789abcdef.png")] // a path, not a name
    [InlineData(@"..\screenshot_20261122_153005_0123456789abcdef0123456789abcdef.png")]
    [InlineData("screenshot_２０２６１１２２_153005_0123456789abcdef0123456789abcdef.png")] // other digits
    [InlineData("screenshot_.png")]
    [InlineData("shot.png")]
    [InlineData("layout.json")]
    public void ANearMiss_IsNotRecognised(string? name)
    {
        CaptureFileWriter.IsOwnFileName(name).Should().BeFalse();
    }

    [Fact]
    public void TheDefaultFolder_IsCapturesInTheServersOwnTempFolder()
    {
        CaptureFileWriter.ServerTempRoot.Should().Be(Path.Combine(Path.GetTempPath(), "Helmsman"));
        CaptureFileWriter.DefaultCaptureFolder.Should().Be(Path.Combine(Path.GetTempPath(), "Helmsman", "captures"));
    }
}
