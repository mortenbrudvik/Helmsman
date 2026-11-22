using System.Drawing;
using WinDesktopMcp.Models;
using WinDesktopMcp.Services.Interfaces;

namespace WinDesktopMcp.Services;

/// <summary>
/// Registers the screenshot service's captures under their captureId (roadmap M3.11): the window or screen layout a capture
/// was read in, and its visual signature (roadmap M6.4). The screenshot service makes one for itself, with its own registry,
/// policies, wrappers and restorer.
/// </summary>
internal sealed class CaptureRegistrar
{
    private readonly ICaptureRegistry? _captureRegistry;
    private readonly INativeWindowWrapper _windows;
    private readonly ICaptureTargetPolicy _capturePolicy;
    private readonly ITargetPolicy _targetPolicy;
    private readonly IScreenCaptureWrapper _captureWrapper;
    private readonly IMonitorService _monitorService;
    private readonly MinimizedCaptureRestorer _restorer;

    public CaptureRegistrar(ICaptureRegistry? captureRegistry, INativeWindowWrapper windows, ICaptureTargetPolicy capturePolicy,
        ITargetPolicy targetPolicy, IScreenCaptureWrapper captureWrapper, IMonitorService monitorService, MinimizedCaptureRestorer restorer)
    {
        _captureRegistry = captureRegistry;
        _windows = windows;
        _capturePolicy = capturePolicy;
        _targetPolicy = targetPolicy;
        _captureWrapper = captureWrapper;
        _monitorService = monitorService;
        _restorer = restorer;
    }

    /// <summary>
    /// The window a capture is of, read right before its pixels (roadmap M3.11), as the wrapper reads the capture's
    /// origin then: its process and rectangle. Null when the rectangle cannot be read; the capture then gets no
    /// captureId, because a click could not tell whether the window moved since.
    /// </summary>
    public CapturedWindow? ReadCapturedWindow(nint windowHandle, bool includeFrame)
    {
        if (_captureRegistry == null || _windows.GetWindowRect(windowHandle) is not { } rect)
        {
            return null;
        }

        return new CapturedWindow((long)windowHandle, (int)_windows.GetWindowThreadProcessId(windowHandle),
            WindowRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom), includeFrame);
    }

    /// <summary>
    /// Registers a successful capture (roadmap M3.11) and returns its captureId: a screen capture with
    /// <paramref name="screen"/>, the virtual screen and the monitors as they were read right after its pixels (not as they
    /// are now, which may be seconds later for <see cref="ScreenshotService.RegisterRaw"/>), a window capture with <paramref name="window"/>.
    /// Null without a registry, for a window capture whose window could not be read, and for a screen capture without its
    /// screen layout: a click could not tell whether the monitors changed since its pixels. <paramref name="signature"/>, the
    /// capture's visual signature (<see cref="SignatureOf"/>), is registered with it.
    /// </summary>
    internal string? Register(int width, int height, ImageToScreenTransform transform, CapturedRegion region, bool windowCapture,
        CapturedWindow? window, CapturedScreen? screen, VisualSignature? signature = null)
    {
        if (_captureRegistry == null || (windowCapture ? window == null : screen == null))
        {
            return null;
        }

        var geometry = new CaptureGeometry(width, height, transform, region, window,
            windowCapture ? null : screen!.VirtualScreen, windowCapture ? null : screen!.Monitors);
        return (signature == null ? _captureRegistry.Register(geometry) : _captureRegistry.Register(geometry, signature)).CaptureId;
    }

    /// <summary>
    /// The visual signature of a capture's full-resolution pixels, which wait_for_visual's since compares with (roadmap M6.4);
    /// null unless the registry keeps signatures, which it does only while wait_for_visual is served (owner decision D9), so
    /// the captures cost nothing more otherwise. Null too for a window restoreIfMinimized restored for the capture: it is
    /// minimized again afterwards, so a since on it would read a minimized window. A screen capture's signature leaves out
    /// the windows the policy denies over it (owner decision D8), so a since on it leaves them out even after they closed.
    /// </summary>
    internal VisualSignature? SignatureOf(CaptureResult capture, bool windowCapture, CapturedWindow? window)
    {
        if (_captureRegistry is not { KeepsSignatures: true } ||
            (windowCapture && (window == null || _restorer.IsRestoredForCapture((nint)window.Handle))))
        {
            return null;
        }

        var signature = VisualSignature.Of(PixelView.Of(capture));
        if (windowCapture)
        {
            return signature;
        }

        var masked = new DeniedWindowMask(_windows, _capturePolicy, _targetPolicy)
            .Over(new Rectangle(capture.ScreenOriginX, capture.ScreenOriginY, capture.Width, capture.Height));
        return masked.Count == 0 ? signature : signature.WithExcluded(masked.Select(w => new Rectangle(w.X, w.Y, w.Width, w.Height)).ToList());
    }

    /// <summary>
    /// The virtual screen and the monitors now, which a screen capture reads right after its pixels and registers; null
    /// when captures are not registered.
    /// </summary>
    internal CapturedScreen? ReadScreen()
    {
        if (_captureRegistry == null)
        {
            return null;
        }

        var screen = _captureWrapper.GetVirtualScreenBounds();
        return new CapturedScreen(new VirtualScreenBounds(screen.X, screen.Y, screen.Width, screen.Height), ReadMonitors());
    }

    /// <summary>The monitors a screen capture registers, or null when they cannot be read (only the virtual screen is compared then).</summary>
    private IReadOnlyList<CapturedMonitor>? ReadMonitors()
    {
        try
        {
            return CapturedMonitors.Of(_monitorService.GetAllMonitors());
        }
        catch (Exception)
        {
            // The capture itself does not depend on them
            return null;
        }
    }
}
