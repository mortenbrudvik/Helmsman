using Helmsman.Services.Interfaces;

namespace Helmsman.Services;

/// <summary>
/// The capture freshness store (<see cref="ICaptureFreshness"/>): the last capture of at most <see cref="TargetCapacity"/>
/// targets and the last input to at most <see cref="WindowCapacity"/> windows, the least recent dropped first. "Since" is a
/// sequence number every input takes, not the clock, so two calls in the same tick still come in order. Thread-safe.
/// </summary>
public sealed class CaptureFreshness : ICaptureFreshness
{
    /// <summary>How many targets' last captures are kept.</summary>
    public const int TargetCapacity = 64;

    /// <summary>How many windows' last input is kept.</summary>
    public const int WindowCapacity = 256;

    private readonly object _lock = new();

    // A target is its fingerprint without the pixel hash
    private readonly Dictionary<CaptureFingerprint, Remembered> _captures = new();
    private readonly Dictionary<long, Received> _input = new();
    private long _sequence;
    private long _captureCount;

    /// <inheritdoc />
    public void InputSent(nint window, bool pointerMoveOnly)
    {
        if (window == nint.Zero)
        {
            return;
        }

        lock (_lock)
        {
            var at = ++_sequence;
            var previous = _input.GetValueOrDefault((long)window);
            _input[(long)window] = new Received(at, pointerMoveOnly ? previous?.NotOnlyMovesAt ?? 0 : at);
            if (_input.Count > WindowCapacity)
            {
                _input.Remove(_input.MinBy(entry => entry.Value.At).Key);
            }
        }
    }

    /// <inheritdoc />
    public long InputSequence
    {
        get
        {
            lock (_lock)
            {
                return _sequence;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<InputSinceCapture>? Remember(CaptureFingerprint capture, long inputSequence)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var target = capture with { PixelHash = 0 };

        lock (_lock)
        {
            var previous = _captures.GetValueOrDefault(target);

            // The sequence read before the pixels, not now: input that landed while they were read counts as after them
            _captures[target] = new Remembered(capture.PixelHash, Math.Min(inputSequence, _sequence), ++_captureCount);
            if (_captures.Count > TargetCapacity)
            {
                _captures.Remove(_captures.MinBy(entry => entry.Value.Order).Key);
            }

            if (previous == null || previous.PixelHash != capture.PixelHash)
            {
                return null;
            }

            return _input
                .Where(entry => entry.Value.At > previous.Sequence)
                .Select(entry => new InputSinceCapture(entry.Key, PointerMovesOnly: entry.Value.NotOnlyMovesAt <= previous.Sequence))
                .ToList();
        }
    }

    /// <summary>A target's last capture: its pixel hash, the input sequence before its pixels were read, and when it was remembered.</summary>
    private sealed record Remembered(ulong PixelHash, long Sequence, long Order);

    /// <summary>A window's last input, and its last input that did more than move the pointer (0: none).</summary>
    private sealed record Received(long At, long NotOnlyMovesAt);
}
