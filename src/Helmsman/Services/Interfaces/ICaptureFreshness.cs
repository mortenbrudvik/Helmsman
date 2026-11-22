using Helmsman.Models;

namespace Helmsman.Services.Interfaces;

/// <summary>
/// What tells a fresh capture from an older frame (warning CaptureUnchangedAfterInput): the input the server sent to each
/// top-level window, numbered in the order it was sent, and the last capture of each target with a hash of its pixels. A
/// full-screen game in front can keep returning one old frame to every way of reading pixels, so an image identical
/// to the previous capture of its target, although input went to it in between, is either input that changed nothing
/// visible or such a frame. Thread-safe; the server keeps one for its whole run.
/// </summary>
public interface ICaptureFreshness
{
    /// <summary>
    /// Input reached <paramref name="window"/>, a top-level window: a batch SendInput inserted at least partly, or an element
    /// action that completed. <paramref name="pointerMoveOnly"/> is true for a batch that only moved the pointer.
    /// </summary>
    void InputSent(nint window, bool pointerMoveOnly);

    /// <summary>
    /// The number of the last input sent so far (0: none). A capture reads it before its pixels and passes it to
    /// <see cref="Remember"/>, so input that lands while the pixels are read counts as after them.
    /// </summary>
    long InputSequence { get; }

    /// <summary>
    /// Remembers <paramref name="capture"/> as the last capture of its target (everything but its pixel hash), its pixels read
    /// after input <paramref name="inputSequence"/> (<see cref="InputSequence"/> before they were read), and compares it with
    /// the previous one: null when there was none, or its pixels differ; otherwise the windows that received input since the
    /// previous one's pixels were read (empty when none did).
    /// </summary>
    IReadOnlyList<InputSinceCapture>? Remember(CaptureFingerprint capture, long inputSequence);
}

/// <summary>
/// A capture as <see cref="ICaptureFreshness"/> compares it: its target (a window, or 0 for the screen, with its process
/// and includeFrame), the screen rectangle it shows, how its pixels were read, and a hash of them.
/// </summary>
public sealed record CaptureFingerprint(long Window, int ProcessId, bool IncludeFrame, CapturedRegion Region, string Method, ulong PixelHash);

/// <summary>
/// A window that received input since the previous capture of a target; <paramref name="PointerMovesOnly"/> when that input
/// only moved the pointer.
/// </summary>
public sealed record InputSinceCapture(long Window, bool PointerMovesOnly);
