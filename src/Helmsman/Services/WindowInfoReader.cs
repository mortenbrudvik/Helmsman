using Helmsman.Models;
using Helmsman.Native;
using Helmsman.Services.Interfaces;
using static Helmsman.Services.WindowReads;

namespace Helmsman.Services;

/// <summary>
/// Reads a window as a <see cref="WindowInfo"/>, as the listings, the window reads and the owned and application windows
/// report it: its title, process, bounds, state, monitor and zone, and whether it is the agent's host. Only reads, and holds
/// nothing between calls.
/// </summary>
internal sealed class WindowInfoReader
{
    private readonly INativeWindowWrapper _nativeWrapper;
    private readonly IMonitorService _monitorService;
    private readonly IHostGuard _hostGuard;
    private readonly WindowReads _reads;

    /// <param name="nativeWrapper">The window calls.</param>
    /// <param name="monitorService">The window's monitor, for its index, id and zone.</param>
    /// <param name="hostGuard">Recognizes the agent's host windows (WindowInfo.IsAgentHost).</param>
    /// <param name="reads">The process a window belongs to, and its topmost style.</param>
    public WindowInfoReader(INativeWindowWrapper nativeWrapper, IMonitorService monitorService, IHostGuard hostGuard, WindowReads reads)
    {
        _nativeWrapper = nativeWrapper;
        _monitorService = monitorService;
        _hostGuard = hostGuard;
        _reads = reads;
    }

    /// <summary>
    /// Reads any window, with no listing filter: untitled, cloaked, tool and shell windows and the
    /// server's own windows are described too. Null only when the window cannot be read (it is gone).
    /// </summary>
    public WindowInfo? ReadWindowInfo(nint hWnd)
    {
        var session = new ReadSession(_nativeWrapper, topLevelWindows: null);
        var className = _nativeWrapper.GetClassName(hWnd) ?? string.Empty;
        var title = _nativeWrapper.GetWindowText(hWnd) ?? string.Empty;
        var process = _reads.ResolveProcess(hWnd, className, title, _nativeWrapper.GetWindowThreadProcessId(hWnd), session);

        return BuildWindowInfo(hWnd, title, className, _nativeWrapper.IsCloaked(hWnd), process, session);
    }

    public WindowInfo? BuildWindowInfo(
        nint hWnd, string title, string className, bool isCloaked, WindowProcess process, ReadSession session)
    {
        var rect = _nativeWrapper.GetWindowRect(hWnd);
        if (rect == null)
            return null;

        var state = WindowState.Normal;
        if (_nativeWrapper.IsIconic(hWnd))
            state = WindowState.Minimized;
        else if (_nativeWrapper.IsZoomed(hWnd))
            state = WindowState.Maximized;

        var owner = _nativeWrapper.GetWindow(hWnd, NativeEnums.GW_OWNER);
        var frame = _nativeWrapper.GetExtendedFrameBounds(hWnd) is { } extended
            ? WindowRect.FromLTRB(extended.Left, extended.Top, extended.Right, extended.Bottom)
            : null;
        var bounds = WindowRect.FromLTRB(rect.Value.Left, rect.Value.Top, rect.Value.Right, rect.Value.Bottom);
        var isAgentHost = _hostGuard.IsHostWindow(hWnd);

        // One lookup for the index, the id and the zone (issue #12); a monitor that cannot be read leaves all of them out
        var monitor = _monitorService.GetMonitorForWindow(hWnd);
        var visible = frame ?? bounds;

        return new WindowInfo(
            Handle: hWnd,
            Title: title,
            ProcessName: session.ProcessName(process.ProcessId),
            ProcessId: (int)process.ProcessId,
            Bounds: bounds,
            State: state,
            IsVisible: _nativeWrapper.IsWindowVisible(hWnd),
            MonitorIndex: monitor?.Index)
        {
            MonitorId = monitor?.Id,
            Zone = monitor == null ? null : WindowZones.Match(visible, state, monitor),
            // Only the visible frame: the window rect's invisible borders (about 7 px) overhang the next monitor
            SpansMonitors = monitor != null && frame != null && WindowZones.Spans(frame, state, monitor, _monitorService.GetAllMonitors),
            VisibleBounds = frame,
            ClassName = className,
            IsCloaked = isCloaked,
            OwnerHandle = owner == nint.Zero ? null : owner,
            HostProcessName = process.HostProcessId is { } hostId ? session.ProcessName(hostId) : null,
            HostProcessId = process.HostProcessId is { } id ? (int)id : null,
            IsAgentHost = isAgentHost,
            IsAgentHostOnlyWhileAncestorsUnknown = isAgentHost && _hostGuard.IsHostWindowOnlyWhileAncestorsUnknown(hWnd),
            IsTopmost = _reads.IsTopmost(hWnd)
        };
    }
}
