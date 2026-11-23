namespace Helmsman.E2ETests.HarnessTests;

/// <summary>
/// The GL frame counter window's watchdog (shared with the fixture as a linked source file): when it checks, what it decides,
/// that it fires once when the window's thread is still alive, and that it never fires once the thread ended or disarmed it.
/// Pure tests: a short deadline and a stand-in for the thread and for the fixture's exit; nothing is started.
/// </summary>
public class GlWindowWatchdogTests
{
    /// <summary>Long enough for a thread-pool timer to have fired on a busy machine.</summary>
    private static readonly TimeSpan FiringTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void DeadlineFor_IsTheLifetimePlusTheGrace_SoTheWindowClosesItselfFirst()
    {
        GlWindowWatchdog.DeadlineFor(TimeSpan.FromSeconds(45)).Should().Be(TimeSpan.FromSeconds(45) + GlWindowWatchdog.Grace);
        GlWindowWatchdog.Grace.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5),
            "it covers setting up the window and the context before the lifetime starts, and releasing them after it");
        GlWindowWatchdog.DeadlineFor(FixtureOptions.LongestGlLifetime).Should().BeLessThan(FixtureOptions.DefaultMaxLifetime,
            "the GL window's watchdog should act before the fixture's own maximum lifetime");
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void ShouldExpire_OnlyWhileArmed_AndTheThreadIsAlive(bool armed, bool threadAlive, bool expected)
    {
        GlWindowWatchdog.ShouldExpire(armed, threadAlive).Should().Be(expected);
    }

    [Fact]
    public void AtTheDeadline_WithTheThreadStillAlive_ItExpiresOnce()
    {
        // Arrange
        var expired = 0;
        using var fired = new ManualResetEventSlim();
        using var watchdog = new GlWindowWatchdog(TimeSpan.FromMilliseconds(50), () => true, () =>
        {
            Interlocked.Increment(ref expired);
            fired.Set();
        });

        // Act
        var didFire = fired.Wait(FiringTimeout);
        Thread.Sleep(200);

        // Assert
        didFire.Should().BeTrue();
        watchdog.HasExpired.Should().BeTrue();
        expired.Should().Be(1);
    }

    [Fact]
    public void AtTheDeadline_WithTheThreadEnded_ItDoesNothing()
    {
        // Arrange - the window closed on its own: the thread is gone, though nothing disarmed the watchdog
        using var checkedThread = new ManualResetEventSlim();
        var expired = false;
        using var watchdog = new GlWindowWatchdog(TimeSpan.FromMilliseconds(50), () =>
        {
            checkedThread.Set();
            return false;
        }, () => expired = true);

        // Act
        var didCheck = checkedThread.Wait(FiringTimeout);
        Thread.Sleep(100);

        // Assert
        didCheck.Should().BeTrue();
        expired.Should().BeFalse();
        watchdog.HasExpired.Should().BeFalse();
    }

    [Fact]
    public void Disarmed_BeforeTheDeadline_ItNeverExpires_EvenWithTheThreadAlive()
    {
        // Arrange - the normal path: the window's thread ends and disarms it well before the deadline
        var expired = false;
        var watchdog = new GlWindowWatchdog(TimeSpan.FromMilliseconds(300), () => true, () => expired = true);

        // Act
        watchdog.Disarm();
        Thread.Sleep(600);

        // Assert
        expired.Should().BeFalse();
        watchdog.HasExpired.Should().BeFalse();
        watchdog.Dispose();
    }

    [Fact]
    public void Constructor_RefusesANegativeDeadline()
    {
        var act = () => new GlWindowWatchdog(TimeSpan.FromSeconds(-1), () => true, () => { });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
