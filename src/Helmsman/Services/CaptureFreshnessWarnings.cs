using System.Drawing;
using Helmsman.Models;
using Helmsman.Services.Interfaces;

namespace Helmsman.Services;

/// <summary>
/// The capture warnings about an image that may be an older frame. While a full-monitor OpenGL game was in front and Windows
/// reported a full-screen 3D app, every way of reading pixels kept returning the frame from when it came to the front for
/// that monitor, and nothing in the results said so: CaptureUnchangedAfterInput (the pixels are identical to the previous
/// capture of the target although input reached it in between) and ExclusiveFullscreen (such a window is in front and in
/// the image).
/// </summary>
internal static class CaptureFreshnessWarnings
{
    /// <summary>QUNS_RUNNING_D3D_FULL_SCREEN: the shell's notification state while a full-screen 3D app is in front.</summary>
    internal const int RunningD3DFullScreen = 3;

    /// <summary>
    /// How both warnings tell the agent to see the current frame of <paramref name="target"/>: a capture_window taken while
    /// another window is in front is current (a screen copy would show the window in front over it). The next key sent with
    /// the target's handle brings it to the front again; a click does only where no window covers the point, since it is
    /// refused with TargetOccluded otherwise. An agent told only to bring another window to the front stopped and asked the
    /// user whether it could, so the text says that it need not ask.
    /// </summary>
    private static string FreshFrameRemedy(string target) =>
        "call focus_window on your own terminal or chat window (get_server_info host.windows), then capture_window of " +
        $"{target}. Your next key, or a click no window covers, with its windowHandle brings it back. No need to ask the user.";

    /// <summary>
    /// Remembers the capture with <paramref name="freshness"/> and warns when its pixels are identical to the previous capture
    /// of the same target although input was sent to it in between: for a window capture to its top-level window
    /// <paramref name="root"/>, for a screen capture (<paramref name="window"/> zero) to a window over the captured region now.
    /// <paramref name="inputSequence"/> is the store's input sequence read before the pixels. Input that only moved the
    /// pointer counts only while the pointer is hidden (<paramref name="pointer"/>): the application then draws its own, so a
    /// move must change pixels, while in an ordinary application a move often changes nothing. An image of one color
    /// (ImageBlank already in <paramref name="warnings"/>) is identical by nature, so it is remembered but not warned about.
    /// </summary>
    internal static void AddUnchangedAfterInputWarning(ICaptureFreshness? freshness, INativeWindowWrapper windows, CaptureResult capture,
        nint window, nint root, bool includeFrame, PointerState? pointer, long inputSequence, List<string> warnings)
    {
        if (freshness == null)
        {
            return;
        }

        var region = new CapturedRegion(capture.ScreenOriginX, capture.ScreenOriginY, capture.Width, capture.Height);
        var processId = window == nint.Zero ? 0 : (int)windows.GetWindowThreadProcessId(window);
        var since = freshness.Remember(new CaptureFingerprint((long)window, processId, includeFrame && window != nint.Zero, region,
            capture.Method, VisualSignature.HashOf(PixelView.Of(capture))), inputSequence);
        if (since is not { Count: > 0 } || warnings.Any(IsBlank))
        {
            return;
        }

        var countMoves = pointer is { Visible: false };
        var shown = new Rectangle(region.X, region.Y, region.Width, region.Height);
        if (!since.Any(input => (countMoves || !input.PointerMovesOnly) &&
                                (window != nint.Zero ? input.Window == (long)root : Over(windows, (nint)input.Window, shown))))
        {
            return;
        }

        warnings.Add(CaptureWarnings.Format(CaptureWarnings.CaptureUnchangedAfterInput,
            $"the image is identical to the previous capture of this {(window != nint.Zero ? "window" : "region")} although input " +
            "was sent to it in between. Either the input changed nothing visible, or this is an older frame (a fullscreen game " +
            "in front can keep returning one). If such a game is in front, " + FreshFrameRemedy("the game")));
    }

    /// <summary>
    /// Warns when Windows reports a full-screen 3D app (<see cref="IScreenCaptureWrapper.GetUserNotificationState"/> is
    /// <see cref="RunningD3DFullScreen"/>; unknown: no warning), the foreground window is not minimized and fills its monitor
    /// (its bounds, taskbar included), and the image shows that window: it is <paramref name="root"/>, the captured window's
    /// top-level window, or the pixels were read from the screen (a screen capture, <paramref name="root"/> zero, or a window
    /// capture whose method can show covering windows) and the captured rectangle overlaps it. The shell's state is one for
    /// the session; the foreground window says which monitor's captures it freezes.
    /// </summary>
    internal static void AddExclusiveFullscreenWarning(IScreenCaptureWrapper screen, INativeWindowWrapper windows, IMonitorService monitors,
        CaptureResult capture, nint root, List<string> warnings)
    {
        if (screen.GetUserNotificationState() != RunningD3DFullScreen)
        {
            return;
        }

        var front = windows.GetForegroundWindow();
        if (front == nint.Zero || windows.IsIconic(front) || windows.GetWindowRect(front) is not { } rect)
        {
            return;
        }

        var frame = WindowRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        if (monitors.GetMonitorForWindow(front) is not { } monitor || !WindowZones.FillsMonitor(frame, monitor))
        {
            return;
        }

        if (root != front)
        {
            var readFromScreen = root == nint.Zero || !CaptureMethods.ShowsOnlyTheWindow(capture.Method);
            var shown = new Rectangle(capture.ScreenOriginX, capture.ScreenOriginY, capture.Width, capture.Height);
            if (!readFromScreen || !shown.IntersectsWith(new Rectangle(frame.X, frame.Y, frame.Width, frame.Height)))
            {
                return;
            }
        }

        warnings.Add(CaptureWarnings.Format(CaptureWarnings.ExclusiveFullscreen,
            "the window in front has its monitor to itself (Windows reports a full-screen 3D app). While it stays in front, " +
            "captures of that monitor can keep returning the frame from when it came to the front: to see the current frame, " +
            FreshFrameRemedy("the fullscreen window")));
    }

    /// <summary>Whether <paramref name="warning"/> is an ImageBlank warning.</summary>
    private static bool IsBlank(string warning) =>
        warning.StartsWith(CaptureWarnings.ImageBlank + ":", StringComparison.Ordinal);

    /// <summary>Whether <paramref name="window"/> is over <paramref name="shown"/> now; false for a window that is gone.</summary>
    private static bool Over(INativeWindowWrapper windows, nint window, Rectangle shown) =>
        windows.GetWindowRect(window) is { } rect &&
        shown.IntersectsWith(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
}
