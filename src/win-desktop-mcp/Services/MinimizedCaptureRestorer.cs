using System.Collections.Concurrent;
using System.Diagnostics;
using WinDesktopMcp.Services.Interfaces;

namespace WinDesktopMcp.Services;

/// <summary>
/// restoreIfMinimized for a capture (see <see cref="IScreenshotService.RestoreForCapture"/>): restores a minimized window
/// without activation, waits until it stops painting, and keeps count of the windows restored for a capture, whose captures
/// keep no visual signature. The screenshot service makes one for itself, with its own timeouts and window call runner.
/// </summary>
internal sealed class MinimizedCaptureRestorer
{
    /// <summary>How long the frame comparison waits between two frames of a restored window.</summary>
    private static readonly TimeSpan SettleFrameInterval = TimeSpan.FromMilliseconds(50);

    private readonly IScreenCaptureWrapper _captureWrapper;
    private readonly INativeWindowWrapper _windows;
    private readonly IWindowCallRunner _windowCalls;
    private readonly TimeSpan _restoreSettleTimeout;
    private readonly TimeSpan _restoreStateTimeout;

    // The windows restoreIfMinimized has restored for a capture, by how many restores: they are minimized again afterwards,
    // so their captures keep no visual signature (roadmap M6.4)
    private readonly ConcurrentDictionary<nint, int> _restoredForCapture = new();

    public MinimizedCaptureRestorer(IScreenCaptureWrapper captureWrapper, INativeWindowWrapper windows, IWindowCallRunner windowCalls,
        TimeSpan restoreSettleTimeout, TimeSpan restoreStateTimeout)
    {
        _captureWrapper = captureWrapper;
        _windows = windows;
        _windowCalls = windowCalls;
        _restoreSettleTimeout = restoreSettleTimeout;
        _restoreStateTimeout = restoreStateTimeout;
    }

    /// <summary>Whether <paramref name="captureWindow"/> is restored for a capture now (it is minimized again afterwards).</summary>
    public bool IsRestoredForCapture(nint captureWindow) => _restoredForCapture.ContainsKey(captureWindow);

    /// <inheritdoc cref="IScreenshotService.RestoreForCapture"/>
    /// <remarks>
    /// The returned restore is the caller's (the capture tools, through <see cref="IScreenshotService.RestoreForCapture"/>): its
    /// Dispose minimizes the window again and releases the count kept here. A restore that throws is released here.
    /// </remarks>
    public MinimizedWindowRestore RestoreForCapture(nint minimizedWindow, nint captureWindow, bool includeFrame)
    {
        var restore = new MinimizedWindowRestore(minimizedWindow, _windows, _windowCalls, _restoreStateTimeout);
        _restoredForCapture.AddOrUpdate(captureWindow, 1, (_, restores) => restores + 1);
        var released = 0;
        restore.Released = () =>
        {
            if (Interlocked.Exchange(ref released, 1) == 0 &&
                _restoredForCapture.AddOrUpdate(captureWindow, 0, (_, restores) => restores - 1) <= 0)
            {
                _restoredForCapture.TryRemove(KeyValuePair.Create(captureWindow, 0));
            }
        };

        try
        {
            restore.Restore();
        }
        catch
        {
            restore.Released();
            throw;
        }

        if (restore.Failure == null && !WaitForTwoEqualFrames(captureWindow, includeFrame))
        {
            restore.StillPainting(_restoreSettleTimeout);
        }

        return restore;
    }

    /// <summary>
    /// Captures frames of a window that was just restored until two in a row are equal: the restore animation and
    /// the application's repaint are over. False when that did not happen within the settle time. A frame that
    /// cannot be captured ends the wait; the capture itself then reports why.
    /// </summary>
    private bool WaitForTwoEqualFrames(nint window, bool includeFrame)
    {
        var waited = Stopwatch.StartNew();
        byte[]? previous = null;
        while (true)
        {
            byte[] frame;
            try
            {
                frame = _windowCalls.Run(window, () => _captureWrapper.CaptureWindow(window, includeFrame)).Bgra;
            }
            catch (Exception)
            {
                return true;
            }

            if (previous != null && previous.AsSpan().SequenceEqual(frame))
            {
                return true;
            }

            if (waited.Elapsed >= _restoreSettleTimeout)
            {
                return false;
            }

            previous = frame;
            Thread.Sleep(SettleFrameInterval);
        }
    }
}
