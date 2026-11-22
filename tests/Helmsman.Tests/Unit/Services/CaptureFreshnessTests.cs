namespace Helmsman.Tests.Unit.Services;

/// <summary>
/// The capture freshness store (warning CaptureUnchangedAfterInput): per target the last capture's pixel hash, and per
/// window the input sent to it, numbered in order, so a capture can tell whether input reached a window since the previous
/// identical capture of its target.
/// </summary>
public class CaptureFreshnessTests
{
    private const long Window = 0x5000;
    private const long Other = 0x6000;

    private static readonly CapturedRegion Region = new(10, 20, 300, 200);

    private static CaptureFingerprint Capture(ulong hash, string method = "PrintWindow", CapturedRegion? region = null,
        long window = Window, int processId = 42, bool includeFrame = true) =>
        new(window, processId, includeFrame, region ?? Region, method, hash);

    /// <summary>Remembers a capture whose pixels were read just now, after every input so far.</summary>
    private static IReadOnlyList<InputSinceCapture>? RememberNow(CaptureFreshness sut, CaptureFingerprint capture) =>
        sut.Remember(capture, sut.InputSequence);

    #region Comparing with the previous capture

    [Fact]
    public void InputSequence_CountsEveryInput()
    {
        // Arrange
        var sut = new CaptureFreshness();

        // Act
        sut.InputSent((nint)Window, pointerMoveOnly: false);
        sut.InputSent((nint)Other, pointerMoveOnly: true);
        sut.InputSent(nint.Zero, pointerMoveOnly: false);

        // Assert
        sut.InputSequence.Should().Be(2);
    }

    [Fact]
    public void Remember_InputBetweenTheSequenceReadAndRemember_CountsAsAfterTheCapture()
    {
        // Arrange - a click that a parallel call sent while the first capture's pixels were read, before it was remembered
        var sut = new CaptureFreshness();
        var beforePixels = sut.InputSequence;
        sut.InputSent((nint)Window, pointerMoveOnly: false);
        sut.Remember(Capture(hash: 1), beforePixels);

        // Act
        var since = RememberNow(sut, Capture(hash: 1));

        // Assert
        since.Should().ContainSingle().Which.Should().Be(new InputSinceCapture(Window, PointerMovesOnly: false));
    }

    [Fact]
    public void Remember_TheFirstCaptureOfATarget_IsNull()
    {
        // Arrange
        var sut = new CaptureFreshness();
        sut.InputSent((nint)Window, pointerMoveOnly: false);

        // Act
        var since = RememberNow(sut, Capture(hash: 1));

        // Assert
        since.Should().BeNull();
    }

    [Fact]
    public void Remember_TheSamePixelsWithInputInBetween_ListsTheWindow()
    {
        // Arrange
        var sut = new CaptureFreshness();
        RememberNow(sut, Capture(hash: 1));
        sut.InputSent((nint)Window, pointerMoveOnly: false);

        // Act
        var since = RememberNow(sut, Capture(hash: 1));

        // Assert
        since.Should().ContainSingle().Which.Should().Be(new InputSinceCapture(Window, PointerMovesOnly: false));
    }

    [Fact]
    public void Remember_TheSamePixelsWithoutInput_IsEmpty()
    {
        // Arrange - input before the previous capture does not count
        var sut = new CaptureFreshness();
        sut.InputSent((nint)Window, pointerMoveOnly: false);
        RememberNow(sut, Capture(hash: 1));

        // Act
        var since = RememberNow(sut, Capture(hash: 1));

        // Assert
        since.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Remember_OtherPixels_IsNull()
    {
        // Arrange
        var sut = new CaptureFreshness();
        RememberNow(sut, Capture(hash: 1));
        sut.InputSent((nint)Window, pointerMoveOnly: false);

        // Act
        var since = RememberNow(sut, Capture(hash: 2));

        // Assert
        since.Should().BeNull();
    }

    [Fact]
    public void Remember_AnotherMethod_IsAnotherTarget()
    {
        // Arrange
        var sut = new CaptureFreshness();
        RememberNow(sut, Capture(hash: 1, method: "PrintWindow"));
        sut.InputSent((nint)Window, pointerMoveOnly: false);

        // Act
        var since = RememberNow(sut, Capture(hash: 1, method: "ScreenBitBlt"));

        // Assert
        since.Should().BeNull();
    }

    [Fact]
    public void Remember_AnotherRegion_IsAnotherTarget()
    {
        // Arrange
        var sut = new CaptureFreshness();
        RememberNow(sut, Capture(hash: 1));
        sut.InputSent((nint)Window, pointerMoveOnly: false);

        // Act
        var since = RememberNow(sut, Capture(hash: 1, region: new CapturedRegion(11, 20, 300, 200)));

        // Assert
        since.Should().BeNull();
    }

    [Fact]
    public void Remember_AnotherProcessOrIncludeFrame_IsAnotherTarget()
    {
        // Arrange - a handle Windows gave to a new window of another process, and the client area of the same window
        var sut = new CaptureFreshness();
        RememberNow(sut, Capture(hash: 1));
        sut.InputSent((nint)Window, pointerMoveOnly: false);

        // Act
        var otherProcess = RememberNow(sut, Capture(hash: 1, processId: 43));
        var clientArea = RememberNow(sut, Capture(hash: 1, includeFrame: false));

        // Assert
        otherProcess.Should().BeNull();
        clientArea.Should().BeNull();
    }

    [Fact]
    public void Remember_AfterAnotherTargetWasCaptured_StillComparesWithItsOwnPreviousCapture()
    {
        // Arrange
        var sut = new CaptureFreshness();
        RememberNow(sut, Capture(hash: 1));
        RememberNow(sut, Capture(hash: 7, window: 0, processId: 0, includeFrame: false, method: "ScreenBitBlt"));
        sut.InputSent((nint)Window, pointerMoveOnly: false);

        // Act
        var since = RememberNow(sut, Capture(hash: 1));

        // Assert
        since.Should().ContainSingle();
    }

    #endregion

    #region Input

    [Fact]
    public void Remember_OnlyPointerMovesInBetween_SaysSo()
    {
        // Arrange
        var sut = new CaptureFreshness();
        RememberNow(sut, Capture(hash: 1));
        sut.InputSent((nint)Window, pointerMoveOnly: true);

        // Act
        var since = RememberNow(sut, Capture(hash: 1));

        // Assert
        since.Should().ContainSingle().Which.PointerMovesOnly.Should().BeTrue();
    }

    [Fact]
    public void Remember_AMoveAndAClickInBetween_IsNotMovesOnly()
    {
        // Arrange
        var sut = new CaptureFreshness();
        RememberNow(sut, Capture(hash: 1));
        sut.InputSent((nint)Window, pointerMoveOnly: false);
        sut.InputSent((nint)Window, pointerMoveOnly: true);

        // Act
        var since = RememberNow(sut, Capture(hash: 1));

        // Assert
        since.Should().ContainSingle().Which.PointerMovesOnly.Should().BeFalse();
    }

    [Fact]
    public void Remember_InputToSeveralWindows_ListsEach()
    {
        // Arrange
        var sut = new CaptureFreshness();
        RememberNow(sut, Capture(hash: 1));
        sut.InputSent((nint)Window, pointerMoveOnly: false);
        sut.InputSent((nint)Other, pointerMoveOnly: true);

        // Act
        var since = RememberNow(sut, Capture(hash: 1));

        // Assert
        since.Should().BeEquivalentTo([new InputSinceCapture(Window, false), new InputSinceCapture(Other, true)]);
    }

    [Fact]
    public void InputSent_ToNoWindow_IsIgnored()
    {
        // Arrange
        var sut = new CaptureFreshness();
        RememberNow(sut, Capture(hash: 1));
        sut.InputSent(nint.Zero, pointerMoveOnly: false);

        // Act
        var since = RememberNow(sut, Capture(hash: 1));

        // Assert
        since.Should().BeEmpty();
    }

    #endregion

    #region Bounds

    [Fact]
    public void Remember_MoreTargetsThanItKeeps_ForgetsTheOldest()
    {
        // Arrange
        var sut = new CaptureFreshness();
        RememberNow(sut, Capture(hash: 1));
        for (var i = 1; i <= CaptureFreshness.TargetCapacity; i++)
        {
            RememberNow(sut, Capture(hash: 1, window: Window + i));
        }

        sut.InputSent((nint)Window, pointerMoveOnly: false);

        // Act
        var since = RememberNow(sut, Capture(hash: 1));

        // Assert - the first target was forgotten, so this is a first capture again
        since.Should().BeNull();
    }

    [Fact]
    public void InputSent_ToMoreWindowsThanItKeeps_StaysBounded()
    {
        // Arrange
        var sut = new CaptureFreshness();
        RememberNow(sut, Capture(hash: 1));
        for (var i = 1; i <= CaptureFreshness.WindowCapacity + 10; i++)
        {
            sut.InputSent((nint)(Other + i), pointerMoveOnly: false);
        }

        // Act
        var since = RememberNow(sut, Capture(hash: 1));

        // Assert - the most recent windows are kept
        since.Should().HaveCount(CaptureFreshness.WindowCapacity);
        since!.Select(s => s.Window).Should().Contain(Other + CaptureFreshness.WindowCapacity + 10);
    }

    #endregion
}
