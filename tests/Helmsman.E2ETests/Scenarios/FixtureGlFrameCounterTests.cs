using System.Runtime.InteropServices;

namespace Helmsman.E2ETests.Scenarios;

/// <summary>
/// Captures of the fixture's GL frame counter window (<c>--open-gl</c>): OpenGL drawing its frame number at every vsync
/// (<see cref="GlFrameCounterLayout"/>), which <see cref="GlFrameDecoder"/> reads back from a capture and compares with the frames
/// the fixture reported at that moment (<see cref="GlFrameTimeline"/>). The windowed scenario (no-input tier) proves the
/// fixture renders and the decoder reads a real capture. The full-monitor scenario (input tier) re-checks, on any machine,
/// what the server's ExclusiveFullscreen and CaptureUnchangedAfterInput warnings and the skill's remedy rest on: with a
/// full-monitor OpenGL window in front and Windows reporting a full-screen 3D app, captures of that monitor can stay at the
/// frame from when the window came to the front (measured on one NVIDIA GPU on Windows 11), and a capture taken once another
/// window was in front is current.
/// </summary>
public class FixtureGlFrameCounterTests
{
    /// <summary>QUNS_RUNNING_D3D_FULL_SCREEN, as the server reads it (IScreenCaptureWrapper.GetUserNotificationState).</summary>
    private const int RunningD3DFullScreen = 3;

    /// <summary>How long after its swap a frame can still be the one a current capture shows: the compositor lags a few frames.</summary>
    private static readonly TimeSpan CaptureLag = TimeSpan.FromMilliseconds(100);

    /// <summary>Frames of slack on both ends of the current range, for the interpolation between the fixture's reports.</summary>
    private const long FrameSlack = 2;

    /// <summary>The first frame can reach the compositor slightly after the window opened.</summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan FocusTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The wait before the one retry of the capture after the GL window is back in front, when it did not decode.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private const string AlreadyFullscreen =
        "Skipped: a full-screen 3D app is already in front (Windows reports shell state 3), and this test never takes the " +
        "foreground from one.";

    private readonly ITestOutputHelper _output;

    public FixtureGlFrameCounterTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopNoInputFact]
    public void CaptureWindow_WindowedGlCounter_IsLiveAndShowsTheFramesTheFixtureReported()
    {
        // Arrange - 640x360, shown without activation
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output,
            new FixtureLaunchOptions { OpenGl = GlWindowMode.Windowed(640, 360) });
        var gl = FixtureGlWindow.WaitForOpen(e2e.Fixture);
        _output.WriteLine($"GL window: {gl}");
        if (ShellState(e2e) is RunningD3DFullScreen)
        {
            _output.WriteLine("Skipped: Windows reports a full-screen 3D app in front (shell state 3), which would distort this " +
                              "check. Run it with no fullscreen game or video in front.");
            return;
        }

        var options = new CaptureOptions { Format = CaptureFormat.Png, MaxDimension = 0 };
        Wait.Until(() => Capture(e2e, gl, options).Reading.Found, SettleTimeout, TimeSpan.FromMilliseconds(100))
            .Should().BeTrue("the GL window's row should reach a capture within {0} s", SettleTimeout.TotalSeconds);

        // Act - two captures about 300 ms apart
        var first = Capture(e2e, gl, options);
        Thread.Sleep(300);
        var second = Capture(e2e, gl, options);

        // Assert
        var timeline = GlFrameTimeline.After(e2e.Fixture, second.After);
        Log("first", first, timeline);
        Log("second", second, timeline);
        first.Reading.Found.Should().BeTrue(first.Reading.Description);
        second.Reading.Found.Should().BeTrue(second.Reading.Description);
        ((second.Frame!.Value - first.Frame!.Value) & GlFrameCounterLayout.FrameMask).Should().BePositive(
            "the captures are live: the fixture swaps a new frame at every vsync");
        ShouldBeCurrent(first, timeline);
        ShouldBeCurrent(second, timeline);
        first.Warnings.Should().NotContain(w => IsWarning(w, CaptureWarnings.ExclusiveFullscreen));
        second.Warnings.Should().NotContain(w => IsWarning(w, CaptureWarnings.ExclusiveFullscreen));
    }

    [DesktopFact]
    public void CaptureWindow_FullMonitorGlCounterInFront_WarnsExclusiveFullscreen_AndIsCurrentAfterAnotherWindowWasInFront()
    {
        // Before anything opens: the full-monitor GL window covers its monitor even shown without activation, so a fullscreen
        // game the user has in front must not get even that
        if (E2ENative.GetUserNotificationState() is RunningD3DFullScreen)
        {
            _output.WriteLine($"{AlreadyFullscreen} Nothing was opened.");
            return;
        }

        // Arrange - over the main window's whole monitor, shown without activation; the test brings it to the front
        using var e2e = E2ESession.Start(DesktopTier.Input, _output,
            new FixtureLaunchOptions { OpenGl = GlWindowMode.FullMonitor, GlLifetime = TimeSpan.FromSeconds(45) });
        var gl = FixtureGlWindow.WaitForOpen(e2e.Fixture);
        _output.WriteLine($"GL window: {gl}");
        try
        {
            // Again right before taking the foreground: a fullscreen app may have come to the front meanwhile
            if (ShellState(e2e) is RunningD3DFullScreen)
            {
                _output.WriteLine($"{AlreadyFullscreen} The state was 3 before the GL window was brought to the front; it " +
                                  "is closed again without having been in front.");
                return;
            }

            BringToFront(e2e, gl.Handle);
            var inFront = DateTimeOffset.Now;
            if (!Wait.Until(() => ShellState(e2e) is RunningD3DFullScreen, TimeSpan.FromSeconds(5)))
            {
                // A fixture that no longer covers its monitor, or no longer draws, must fail here, not skip below on every machine
                ShouldCoverItsMonitor(e2e, gl);
                ShouldBeDrawing(e2e, inFront);
                _output.WriteLine($"Skipped: with the full-monitor GL window in front, Windows reports shell state {ShellState(e2e)}, " +
                                  "not 3 (a full-screen 3D app), so the server raises no ExclusiveFullscreen here and there is " +
                                  "nothing to check. The GPU driver decides this; nothing was captured.");
                return;
            }

            // Act - in front: two captures 500 ms apart, with the parameters every capture here uses
            var options = new CaptureOptions { Format = CaptureFormat.Png };
            var first = Capture(e2e, gl, options);
            Thread.Sleep(500);
            var second = Capture(e2e, gl, options);
            var timeline = GlFrameTimeline.After(e2e.Fixture, second.After);
            var firstVerdict = Log("in front, first", first, timeline);
            var secondVerdict = Log("in front, second", second, timeline);
            var advanced = timeline.FrameAt(second.After) - timeline.FrameAt(first.Before);

            // Whether the captures are frozen is the driver's, and only reported
            var outcome = Classify(firstVerdict, secondVerdict);
            _output.WriteLine(Describe(outcome, first, firstVerdict, second, secondVerdict, advanced));

            // Assert - the warning
            first.Warnings.Should().Contain(w => IsWarning(w, CaptureWarnings.ExclusiveFullscreen));

            if (outcome == InFrontOutcome.Frozen && first.Frame == second.Frame)
            {
                // A click on a neutral point of the GL window, then the same capture: identical pixels after input
                var click = e2e.Server.Input.Click(gl.NeutralPoint.X, gl.NeutralPoint.Y, InputTarget.Window(gl.Handle),
                    releaseHeldModifiers: false, CancellationToken.None);
                click.Success.Should().BeTrue($"{click.ErrorCode} {click.Error}");
                var afterClick = Capture(e2e, gl, options);
                Log("after the click", afterClick, GlFrameTimeline.After(e2e.Fixture, afterClick.After));
                var afterClickVerdict = GlFrameReading.CompareAfterInput(second.Reading, afterClick.Reading);
                _output.WriteLine(DescribeAfterClick(afterClickVerdict, second, afterClick));
                switch (afterClickVerdict)
                {
                    case GlFrameAfterInput.SameFrame:
                        afterClick.Warnings.Should().Contain(w => IsWarning(w, CaptureWarnings.CaptureUnchangedAfterInput));
                        break;
                    case GlFrameAfterInput.OtherFrame:
                        // Another frame decoded from the same parameters means other pixels (the scaling is the same for
                        // identical pixels), and the server warns only when its hash of every pixel is the previous one
                        afterClick.Warnings.Should().NotContain(w => IsWarning(w, CaptureWarnings.CaptureUnchangedAfterInput),
                            "the server warns CaptureUnchangedAfterInput only on pixels identical to the previous capture's");
                        break;
                }
            }

            // The remedy the skill documents: another window in front, then a capture of the GL window is current
            BringToFront(e2e, e2e.Fixture.MainWindowHandle);
            Wait.Until(() => ShellState(e2e) is not RunningD3DFullScreen, TimeSpan.FromSeconds(5));
            _output.WriteLine($"Shell state with the main window in front: {ShellState(e2e)}.");
            var behind = Capture(e2e, gl, options);
            var behindTimeline = GlFrameTimeline.After(e2e.Fixture, behind.After);
            Log("main window in front", behind, behindTimeline);
            behind.Warnings.Should().NotContain(w => IsWarning(w, CaptureWarnings.ExclusiveFullscreen));
            ShouldBeCurrent(behind, behindTimeline);

            // ... and the first capture after the GL window is back in front shows the frame of that moment
            var refocused = DateTimeOffset.Now;
            BringToFront(e2e, gl.Handle);
            var back = Capture(e2e, gl, options);
            if (!back.Reading.Found)
            {
                // The switch back to the full-monitor window can yield one frame that does not decode; once more, not more
                _output.WriteLine($"back in front: the capture did not decode ({back.Reading}); retried once after " +
                                  $"{RetryDelay.TotalMilliseconds:0} ms.");
                Thread.Sleep(RetryDelay);
                back = Capture(e2e, gl, options);
            }

            var backTimeline = GlFrameTimeline.After(e2e.Fixture, back.After);
            Log("back in front", back, backTimeline, from: refocused);
            ShouldBeCurrent(back, backTimeline, from: refocused);
        }
        finally
        {
            Close(e2e, gl);
        }
    }

    /// <summary>What the two captures taken while the full-monitor GL window was in front showed.</summary>
    private enum InFrontOutcome
    {
        /// <summary>Both decoded, and at least one shows a frame older than the current range: an older frame.</summary>
        Frozen,

        /// <summary>Both decoded and both current.</summary>
        Live,

        /// <summary>At least one did not decode or is one color (ImageBlank): nothing can be said about freshness.</summary>
        Unreadable
    }

    /// <summary>A capture of the GL window's client area, timed, with what the decoder read from it.</summary>
    private sealed record GlCapture(DateTimeOffset Before, DateTimeOffset After, ScreenshotResult Metadata, GlFrameReading Reading)
    {
        public long? Frame => Reading.Frame;

        public IReadOnlyList<string> Warnings => Metadata.Warnings ?? [];

        public bool IsBlank => Warnings.Any(w => IsWarning(w, CaptureWarnings.ImageBlank));
    }

    /// <summary>
    /// A capture judged against the frames the fixture swapped around it: the range a current capture shows (with
    /// <see cref="FrameSlack"/>), whether it decoded (and is not one color), and whether its frame is in the range.
    /// </summary>
    private sealed record Verdict(long Lowest, long Highest, bool Readable, bool Current)
    {
        public override string ToString() => !Readable ? "unreadable" : Current ? "current" : "an older frame";
    }

    private static GlCapture Capture(E2ESession e2e, FixtureGlWindow gl, CaptureOptions options)
    {
        var before = DateTimeOffset.Now;
        var capture = e2e.Server.Screenshots.CaptureWindow(gl.Handle, includeFrame: false, options);
        var after = DateTimeOffset.Now;
        if (!capture.Metadata.Success)
        {
            return new GlCapture(before, after, capture.Metadata,
                new GlFrameReading(null, $"not captured: {capture.Metadata.ErrorCode} {capture.Metadata.Error}"));
        }

        using var image = CaptureChecks.DecodeImage(capture);
        return new GlCapture(before, after, capture.Metadata, GlFrameDecoder.Decode(image));
    }

    /// <summary>
    /// Judges <paramref name="capture"/>: it is current when it shows a frame the fixture swapped between
    /// <paramref name="from"/> (by default when the capture started) less <see cref="CaptureLag"/> and the end of the capture,
    /// give or take <see cref="FrameSlack"/>, compared modulo the row (<see cref="GlFrameTimeline.IsInRange"/>).
    /// </summary>
    private static Verdict Judge(GlCapture capture, GlFrameTimeline timeline, DateTimeOffset? from = null)
    {
        var (lowest, highest) = timeline.CurrentRange(from ?? capture.Before, capture.After, CaptureLag);
        lowest -= FrameSlack;
        highest += FrameSlack;
        var readable = capture.Reading.Found && !capture.IsBlank;
        return new Verdict(lowest, highest, readable,
            readable && GlFrameTimeline.IsInRange(capture.Frame!.Value, lowest, highest));
    }

    /// <summary>Asserts that <paramref name="capture"/> decodes and is current (<see cref="Judge"/>).</summary>
    private static void ShouldBeCurrent(GlCapture capture, GlFrameTimeline timeline, DateTimeOffset? from = null)
    {
        var verdict = Judge(capture, timeline, from);
        capture.Reading.Found.Should().BeTrue(capture.Reading.Description);
        verdict.Current.Should().BeTrue(
            $"a current capture shows a frame the fixture swapped while it was taken (or just before): {verdict.Lowest}.." +
            $"{verdict.Highest} (compared modulo 2^{GlFrameCounterLayout.DataBits}), and this one read {capture.Reading}");
    }

    private static InFrontOutcome Classify(Verdict first, Verdict second) =>
        !first.Readable || !second.Readable ? InFrontOutcome.Unreadable
        : first.Current && second.Current ? InFrontOutcome.Live
        : InFrontOutcome.Frozen;

    /// <summary>The line that reports the in-front outcome; the scenario's documentation in TESTING.md quotes these.</summary>
    private static string Describe(InFrontOutcome outcome, GlCapture first, Verdict firstVerdict, GlCapture second,
        Verdict secondVerdict, long advanced) => outcome switch
    {
        InFrontOutcome.Unreadable =>
            $"UNREADABLE: the first capture {Unreadable(first)}; the second {Unreadable(second)}. Whether the captures in front " +
            "were current is unknown; no click check (the server never warns CaptureUnchangedAfterInput on a one-color " +
            "image), the checks with another window in front still run.",
        InFrontOutcome.Live =>
            $"live: the captures in front show frames {first.Frame} and {second.Frame}, both current, while the fixture " +
            $"swapped {advanced} frames.",
        _ when first.Frame == second.Frame =>
            $"FROZEN: both captures in front show frame {first.Frame} while the fixture swapped {advanced} frames (the " +
            $"first is {firstVerdict}, the second {secondVerdict}).",
        _ =>
            $"FROZEN: the captures in front show frames {first.Frame} ({firstVerdict}) and {second.Frame} ({secondVerdict}) " +
            $"while the fixture swapped {advanced} frames. They differ, so no click check: CaptureUnchangedAfterInput needs " +
            "identical pixels."
    };

    /// <summary>The line that reports the capture after the click; the scenario's documentation in TESTING.md quotes these.</summary>
    private static string DescribeAfterClick(GlFrameAfterInput verdict, GlCapture beforeClick, GlCapture afterClick) => verdict switch
    {
        GlFrameAfterInput.SameFrame =>
            $"the click left the capture at frame {afterClick.Frame}: CaptureUnchangedAfterInput expected.",
        GlFrameAfterInput.OtherFrame =>
            $"the click unfroze the capture: frame {beforeClick.Frame} -> {afterClick.Frame}, no CaptureUnchangedAfterInput " +
            "expected.",
        _ =>
            $"the capture after the click {Unreadable(afterClick)}: no CaptureUnchangedAfterInput check."
    };

    private static string Unreadable(GlCapture capture) =>
        capture.Reading.Found && !capture.IsBlank
            ? $"read {capture.Reading}"
            : $"is unreadable ({capture.Reading}{(capture.IsBlank ? ", warned ImageBlank" : "")})";

    /// <summary>Logs <paramref name="capture"/> with its frame, the range a current capture shows and the verdict; returns the verdict.</summary>
    private Verdict Log(string what, GlCapture capture, GlFrameTimeline timeline, DateTimeOffset? from = null)
    {
        var verdict = Judge(capture, timeline, from);
        _output.WriteLine(
            $"{what}: {capture.Reading}; a current capture shows {verdict.Lowest}..{verdict.Highest}; verdict: {verdict} " +
            $"({timeline.FramesPerSecond:0.#} fps); {capture.Metadata.Width}x{capture.Metadata.Height}, method " +
            $"{capture.Metadata.CaptureMethod}, {(capture.After - capture.Before).TotalMilliseconds:0} ms, " +
            $"warnings [{string.Join(" | ", capture.Warnings)}]");
        return verdict;
    }

    private static bool IsWarning(string warning, string code) => warning.StartsWith(code + ":", StringComparison.Ordinal);

    /// <summary>The shell's notification state as the server reads it, through the guarded wrapper (a read).</summary>
    private static int? ShellState(E2ESession e2e) => e2e.Server.Get<IScreenCaptureWrapper>().GetUserNotificationState();

    /// <summary>
    /// Asserts that the full-monitor GL window covers exactly its monitor: as the fixture placed it (its glOpened event) and
    /// as it is now (read through the server's wrapper).
    /// </summary>
    private void ShouldCoverItsMonitor(E2ESession e2e, FixtureGlWindow gl)
    {
        var now = e2e.Server.Get<INativeWindowWrapper>().GetWindowRect(gl.Handle);
        _output.WriteLine($"GL window rectangle now: {now}; its monitor: {gl.MonitorRect}.");
        gl.WindowRect.Should().Be(gl.MonitorRect, "the fixture opens the full-monitor GL window over its monitor's bounds");
        now.Should().NotBeNull("the GL window should still be open");
        var (left, top, right, bottom) = now!.Value;
        new ScreenRect(left, top, right - left, bottom - top).Should().Be(gl.MonitorRect,
            "the full-monitor GL window should still cover its monitor exactly once it is in front");
    }

    /// <summary>
    /// Asserts that the GL window is still drawing: the fixture reports a frame after now (the wait fails the test when it
    /// does not), and its reports since <paramref name="since"/> show a frame rate above 0.
    /// </summary>
    private void ShouldBeDrawing(E2ESession e2e, DateTimeOffset since)
    {
        var timeline = GlFrameTimeline.After(e2e.Fixture, DateTimeOffset.Now);
        var framesPerSecond = timeline.FramesPerSecondSince(since);
        _output.WriteLine($"GL window frame rate since it came to the front: {framesPerSecond:0.#} fps ({timeline.Count} reports in all).");
        timeline.Count.Should().BePositive("the GL window reports the frames it swaps");
        framesPerSecond.Should().BePositive("the full-monitor GL window should keep drawing once it is in front");
    }

    /// <summary>Brings a fixture window to the front through the server (verified activation) and waits until it is.</summary>
    private void BringToFront(E2ESession e2e, nint window)
    {
        var focus = e2e.Server.Foreground.EnsureForeground(window);
        _output.WriteLine($"EnsureForeground(0x{window:X}): isForeground={focus.IsForeground}, method={focus.Method}, {focus.ErrorCode} {focus.Error}");
        Wait.Until(() => E2ENative.GetAncestor(E2ENative.GetForegroundWindow(), E2ENative.GA_ROOTOWNER) == window, FocusTimeout)
            .Should().BeTrue($"focus_window reported isForeground={focus.IsForeground} ({focus.ErrorCode}: {focus.Error})");
    }

    /// <summary>
    /// Closes the GL window: WM_CLOSE to the window the test opened (it then releases its GL context and ends its thread). The
    /// session's end kills the fixture anyway. It runs in a finally block, so it logs what goes wrong instead of throwing: an
    /// exception here would hide the test's own failure.
    /// </summary>
    private void Close(E2ESession e2e, FixtureGlWindow gl)
    {
        const uint WmClose = 0x0010;
        try
        {
            if (!E2ENative.PostMessageW(gl.Handle, WmClose, 0, 0))
            {
                _output.WriteLine($"WM_CLOSE to the GL window could not be posted (error {Marshal.GetLastWin32Error()}); " +
                                  "the fixture is killed with the session.");
                return;
            }

            var closed = e2e.Fixture.WaitForEvent(FixtureContract.Events.GlClosed, timeout: TimeSpan.FromSeconds(3));
            _output.WriteLine($"GL window closed: {closed}");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"The GL window did not report closing; the fixture is killed with the session. {ex.GetType().Name}: {ex.Message}");
        }
    }
}
