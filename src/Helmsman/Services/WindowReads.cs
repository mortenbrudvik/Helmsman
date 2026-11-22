using Helmsman.Models;
using Helmsman.Native;
using Helmsman.Services.Interfaces;

namespace Helmsman.Services;

/// <summary>
/// The window reads the window services share: whether a handle is a window, its topmost and tool-window styles, its
/// restore rectangle, the top-level windows, and the process a window belongs to (a UWP frame is attributed to the app it
/// hosts). Only reads, and holds nothing between calls.
/// </summary>
internal sealed class WindowReads
{
    private readonly INativeWindowWrapper _nativeWrapper;

    /// <param name="nativeWrapper">The window calls.</param>
    public WindowReads(INativeWindowWrapper nativeWrapper)
    {
        _nativeWrapper = nativeWrapper;
    }

    public bool IsValidWindow(nint handle)
    {
        return handle != nint.Zero && _nativeWrapper.IsWindow(handle);
    }

    public bool IsTopmost(nint window) =>
        ((long)_nativeWrapper.GetWindowLongPtr(window, NativeEnums.GWL_EXSTYLE) & NativeEnums.WS_EX_TOPMOST) != 0;

    /// <summary>Whether the window is a tool window (WS_EX_TOOLWINDOW), whose placement is in screen coordinates.</summary>
    public bool IsToolWindow(nint handle) =>
        (_nativeWrapper.GetWindowLongPtr(handle, NativeEnums.GWL_EXSTYLE) & (nint)NativeEnums.WS_EX_TOOLWINDOW) != 0;

    /// <summary>The placement's restore rectangle in screen coordinates: a tool window's is already, any other's is in workspace coordinates.</summary>
    public WindowRect NormalBounds(nint handle, NativeWindowPlacement placement, IReadOnlyList<MonitorInfo> monitors)
    {
        var (left, top, right, bottom) = placement.NormalPosition;
        var normal = WindowRect.FromLTRB(left, top, right, bottom);
        return IsToolWindow(handle) ? normal : WorkspaceCoordinates.ToScreen(normal, monitors);
    }

    /// <summary>
    /// The process a window belongs to. A UWP frame window (ApplicationFrameWindow) belongs to
    /// ApplicationFrameHost, but the app runs in the process of the CoreWindow that the frame hosts:
    /// report that process, with ApplicationFrameHost as the host. A window of ApplicationFrameHost of another
    /// class, such as the frame a prompt may come up in, is attributed the same way to a CoreWindow child of
    /// another process (roadmap M3.F7). The process name that check reads is read once per process in a call, and is
    /// the one the window is reported with anyway.
    /// </summary>
    public WindowProcess ResolveProcess(nint hWnd, string className, string title, uint windowProcessId, ReadSession session)
    {
        if (!UwpFrames.IsFrame(className, windowProcessId, session.ProcessName))
            return new WindowProcess(windowProcessId, HostProcessId: null);

        return FindHostedAppProcessId(hWnd, className, title, windowProcessId, session) is { } appProcessId
            ? new WindowProcess(appProcessId, HostProcessId: windowProcessId)
            : new WindowProcess(windowProcessId, HostProcessId: null);
    }

    private uint? FindHostedAppProcessId(nint frame, string className, string title, uint hostProcessId, ReadSession session)
    {
        // While the app runs, its CoreWindow is a child of the frame.
        if (UwpFrames.FindHostedAppProcessId(_nativeWrapper, frame, hostProcessId) is { } runningAppProcessId)
            return runningAppProcessId;

        // While the app is minimized or suspended, Windows moves its CoreWindow out of the frame to the
        // top level. Match it by title, and only when exactly one other process has such a window; an
        // ambiguous title leaves the frame attributed to its host. Only an app's frame (ApplicationFrameWindow)
        // is minimized that way; the frame host's other windows are matched by their CoreWindow child alone.
        if (string.IsNullOrEmpty(title) || !WindowClassification.IsApplicationFrameWindow(className))
            return null;

        var candidates = session.TopLevelCoreWindows
            .Where(w => w.ProcessId != 0 && w.ProcessId != hostProcessId && string.Equals(w.Title, title, StringComparison.Ordinal))
            .Select(w => w.ProcessId)
            .Distinct()
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    public static List<nint> EnumerateTopLevelWindows(INativeWindowWrapper nativeWrapper)
    {
        var handles = new List<nint>();
        nativeWrapper.EnumWindows(hWnd =>
        {
            handles.Add(hWnd);
            return true;
        });
        return handles;
    }

    /// <summary>The process of a window, and for a UWP frame window also the frame host process.</summary>
    public readonly record struct WindowProcess(uint ProcessId, uint? HostProcessId)
    {
        public bool Has(int processId) => ProcessId == processId || HostProcessId == processId;
    }

    /// <summary>
    /// Values shared by the reads of one call: each process name is looked up once, and the
    /// top-level UWP CoreWindows are listed only when a frame needs them.
    /// </summary>
    public sealed class ReadSession(INativeWindowWrapper nativeWrapper, IReadOnlyList<nint>? topLevelWindows)
    {
        private readonly Dictionary<uint, string> _processNames = [];
        private uint? _currentProcessId;
        private List<(string Title, uint ProcessId)>? _topLevelCoreWindows;

        public uint CurrentProcessId => _currentProcessId ??= nativeWrapper.GetCurrentProcessId();

        /// <summary>The top-level CoreWindows: (title, process ID).</summary>
        public IReadOnlyList<(string Title, uint ProcessId)> TopLevelCoreWindows =>
            _topLevelCoreWindows ??= (topLevelWindows ?? EnumerateTopLevelWindows(nativeWrapper))
                .Where(hWnd => WindowClassification.IsCoreWindow(nativeWrapper.GetClassName(hWnd)))
                .Select(hWnd => (nativeWrapper.GetWindowText(hWnd) ?? string.Empty, nativeWrapper.GetWindowThreadProcessId(hWnd)))
                .ToList();

        /// <summary>The name of the process, or "Unknown" when it has exited or cannot be read.</summary>
        public string ProcessName(uint processId)
        {
            if (!_processNames.TryGetValue(processId, out var name))
            {
                name = nativeWrapper.GetProcessName(processId) ?? WindowInfo.UnknownProcessName;
                _processNames[processId] = name;
            }

            return name;
        }
    }
}
