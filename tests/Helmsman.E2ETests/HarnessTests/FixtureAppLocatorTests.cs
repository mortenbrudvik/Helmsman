namespace Helmsman.E2ETests.HarnessTests;

/// <summary>Finding HelmsmanFixtureApp.exe. These tests only look at files; they never start the fixture.</summary>
public class FixtureAppLocatorTests
{
    [Fact]
    public void BuiltRelativePath_IsRecordedByTheBuild()
    {
        FixtureAppLocator.BuiltRelativePath.Should().NotBeNullOrWhiteSpace()
            .And.EndWith(FixtureContract.ProcessName + ".exe");
    }

    [Fact]
    public void Locate_WithTheBuiltPath_FindsTheFixtureBuiltWithTheTests()
    {
        // Act
        var path = FixtureAppLocator.Locate(null, FixtureAppLocator.BuiltRelativePath, AppContext.BaseDirectory);

        // Assert - the project reference builds the fixture first; it is not copied into the test output
        File.Exists(path).Should().BeTrue();
        Path.GetFileName(path).Should().Be("HelmsmanFixtureApp.exe");
        Path.GetDirectoryName(path).Should().NotBe(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
    }

    [Fact]
    public void Locate_OverridePathWins()
    {
        // Arrange
        var built = FixtureAppLocator.Locate(null, FixtureAppLocator.BuiltRelativePath, AppContext.BaseDirectory);

        // Act
        var path = FixtureAppLocator.Locate(built, "does-not-matter\\HelmsmanFixtureApp.exe", AppContext.BaseDirectory);

        // Assert
        path.Should().Be(built);
    }

    [Theory]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Temp\HelmsmanFixtureApp.exe.bat")]
    public void Locate_RefusesAnythingButTheFixtureExe(string overridePath)
    {
        var act = () => FixtureAppLocator.Locate(overridePath, null, AppContext.BaseDirectory);

        act.Should().Throw<InvalidOperationException>().WithMessage("*must be HelmsmanFixtureApp.exe*");
    }

    [Fact]
    public void Locate_WhenTheFixtureIsMissing_ThrowsFileNotFound()
    {
        var missing = Path.Combine(Path.GetTempPath(), "HelmsmanE2E-missing-" + Guid.NewGuid().ToString("N"), "HelmsmanFixtureApp.exe");

        var act = () => FixtureAppLocator.Locate(missing, null, AppContext.BaseDirectory);

        act.Should().Throw<FileNotFoundException>().WithMessage("*Build the solution*");
    }

    [Fact]
    public void Locate_WithNoPath_Throws()
    {
        var act = () => FixtureAppLocator.Locate(null, null, AppContext.BaseDirectory);

        act.Should().Throw<InvalidOperationException>();
    }
}
