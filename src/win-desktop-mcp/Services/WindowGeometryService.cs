using WinDesktopMcp.Models;
using WinDesktopMcp.Native;
using WinDesktopMcp.Services.Interfaces;
using static WinDesktopMcp.Services.WindowChangeFailures;

namespace WinDesktopMcp.Services;

/// <summary>
/// The window placements and state changes (<see cref="IWindowGeometry"/>, roadmap M2.2 and M2.3): move, resize, bounds and
/// frame, snap, move to a monitor (across a DPI change in two steps), minimize, maximize and restore, and a minimized
/// window's restore rectangle.
/// </summary>
/// <remarks>
/// SetWindowPos and ShowWindow on another thread's window wait until that thread answers, and the window tools run
/// inside the process-wide input gate. So every such call goes through <see cref="IWindowCallRunner"/>: a window
/// that does not answer is given up on at its deadline with <see cref="WindowNotRespondingException"/>, and a hung
/// application cannot hold the gate, and with it every input and window tool, until it recovers.
/// </remarks>
public sealed class WindowGeometryService : IWindowGeometry
{
    /// <summary>
    /// How long a state change (minimize, maximize, restore) waits for the window to be seen in the new state before it
    /// reports StateChangeFailed: applications that animate the change take a moment.
    /// </summary>
    public static readonly TimeSpan StateChangeTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>How often the state is read while a state change waits for it.</summary>
    private static readonly TimeSpan StatePollInterval = TimeSpan.FromMilliseconds(20);

    private readonly IMonitorService _monitorService;
    private readonly INativeWindowWrapper _nativeWrapper;
    private readonly IWindowCallRunner _windowCalls;
    private readonly TimeSpan _stateChangeTimeout;
    private readonly WindowReads _reads;
    private readonly WindowTargetCheck _targetCheck;

    /// <param name="monitorService">Monitor lookups.</param>
    /// <param name="nativeWrapper">The window calls.</param>
    /// <param name="targetPolicy">
    /// The server's target policy (roadmap M1.12). Every window change (move, resize, bounds, state, snap, move to a
    /// monitor, and their batches) refuses a window it denies, so these tools never restore, raise or move it (input and
    /// the app itself still can).
    /// </param>
    /// <param name="windowCalls">
    /// Runs the calls that can block on another thread's window (SetWindowPos, ShowWindow) with a deadline; the one
    /// set_window_zorder uses, which counts the calls still stuck.
    /// </param>
    /// <param name="stateChangeTimeout">
    /// How long a state change waits to see the new state; default <see cref="StateChangeTimeout"/>. Tests pass zero.
    /// </param>
    public WindowGeometryService(
        IMonitorService monitorService,
        INativeWindowWrapper nativeWrapper,
        ITargetPolicy targetPolicy,
        IWindowCallRunner windowCalls,
        TimeSpan? stateChangeTimeout = null)
    {
        _monitorService = monitorService;
        _nativeWrapper = nativeWrapper;
        _reads = new WindowReads(nativeWrapper);
        _targetCheck = new WindowTargetCheck(nativeWrapper, _reads, targetPolicy ?? throw new ArgumentNullException(nameof(targetPolicy)));
        _windowCalls = windowCalls ?? throw new ArgumentNullException(nameof(windowCalls));
        _stateChangeTimeout = stateChangeTimeout ?? StateChangeTimeout;
    }

    public WindowChangeResult MoveWindow(nint handle, int x, int y, bool bringToFront = true) =>
        _targetCheck.ChangeRefusal(handle) ??
        Place(handle, new PlacementTarget(X: x, Y: y), BoundsFrame.Visible, PlacementFlags(bringToFront), WindowErrorCode.MoveFailed);

    /// <summary>
    /// The SetWindowPos flags for a placement. With <paramref name="bringToFront"/> the window goes to
    /// the top of the z-order (HWND_TOP), but it is never activated: SWP_NOACTIVATE stays set.
    /// BringWindowToTop is not used, because it activates the window, so keyboard input would follow it
    /// whenever the server happened to have the foreground rights. focus_window activates, verified.
    /// </summary>
    private static uint PlacementFlags(bool bringToFront) =>
        bringToFront ? NativeEnums.SWP_NOACTIVATE : NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE;

    public WindowChangeResult ResizeWindow(nint handle, int width, int height) =>
        ResizeWindow(handle, width, height, bringToFront: false);

    public WindowChangeResult ResizeWindow(nint handle, int width, int height, bool bringToFront) =>
        _targetCheck.ChangeRefusal(handle) ??
        Place(handle, new PlacementTarget(Width: width, Height: height), BoundsFrame.Visible,
            PlacementFlags(bringToFront), WindowErrorCode.ResizeFailed);

    public WindowChangeResult SetWindowBounds(nint handle, int x, int y, int width, int height, bool bringToFront = true) =>
        _targetCheck.ChangeRefusal(handle) ??
        Place(handle, new PlacementTarget(x, y, width, height), BoundsFrame.Outer, PlacementFlags(bringToFront), WindowErrorCode.MoveFailed);

    public WindowChangeResult SetWindowFrame(nint handle, int x, int y, int width, int height, bool bringToFront = true) =>
        _targetCheck.ChangeRefusal(handle) ??
        Place(handle, new PlacementTarget(x, y, width, height), BoundsFrame.Visible, PlacementFlags(bringToFront), WindowErrorCode.MoveFailed);

    /// <summary>The longest settleMs the geometry tools wait for a window to stop moving: they wait inside the input gate.</summary>
    public const int MaxSettleMs = 5000;

    /// <summary>
    /// How long a placement that took the window to a monitor with another scale waits for the application to apply its
    /// own DPI change (WM_DPICHANGED) before it measures the window again.
    /// </summary>
    public static readonly TimeSpan DpiSettleTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>How often the frame is read while a placement waits for the window to stop moving.</summary>
    private static readonly TimeSpan SettlePollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>The wait for <paramref name="settleMs"/>: 0 to <see cref="MaxSettleMs"/>.</summary>
    public static TimeSpan SettleDelay(int settleMs) => TimeSpan.FromMilliseconds(Math.Clamp(settleMs, 0, MaxSettleMs));

    public void WaitForSettledFrame(nint handle, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return;

        var waited = System.Diagnostics.Stopwatch.StartNew();
        var last = ReadFrame(handle);
        while (last != null)
        {
            // Thread.Sleep throws for a negative time, so the remaining time is read once
            var remaining = timeout - waited.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return;

            Thread.Sleep(remaining < SettlePollInterval ? remaining : SettlePollInterval);

            var now = ReadFrame(handle);
            if (now == last)
                return;

            last = now;
        }
    }

    /// <summary>
    /// What a placement sets; null keeps the window's own value. Without X and Y the window is not moved
    /// (SWP_NOMOVE), without Width and Height it is not resized (SWP_NOSIZE).
    /// </summary>
    private readonly record struct PlacementTarget(int? X = null, int? Y = null, int? Width = null, int? Height = null);

    /// <summary>A window's rect, its visible frame and the invisible borders between them, read at one moment.</summary>
    private readonly record struct WindowFrame(WindowRect Outer, WindowRect Visible, FrameInsets Insets);

    /// <summary>
    /// The window's rect and visible frame (DWMWA_EXTENDED_FRAME_BOUNDS; the rect itself when DWM cannot report it or
    /// reports an implausible frame). Null when the window is gone. Both are reads that never wait for the window's thread.
    /// </summary>
    private WindowFrame? ReadFrame(nint handle)
    {
        if (_nativeWrapper.GetWindowRect(handle) is not { } rect)
            return null;

        var insets = FrameInsets.Between(rect, _nativeWrapper.GetExtendedFrameBounds(handle));
        var outer = WindowRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var visible = new WindowRect(outer.X + insets.Left, outer.Y + insets.Top,
            outer.Width - insets.Left - insets.Right, outer.Height - insets.Top - insets.Bottom);
        return new WindowFrame(outer, visible, insets);
    }

    /// <summary>
    /// Places a window that was checked already. A minimized or maximized window is restored first, without activation
    /// (<see cref="RestoreForPlacement"/>), and measured only afterwards. The target is in <paramref name="frame"/>'s
    /// coordinates; for the visible frame the invisible borders are added. When the placement took the window to a
    /// monitor with another scale, the application has rescaled it and its borders changed (WM_DPICHANGED), so once the
    /// window has settled the same target is placed again, measured anew, as PowerToys FancyZones does; a move keeps the
    /// size the application chose (SWP_NOSIZE).
    /// </summary>
    private WindowChangeResult Place(nint handle, PlacementTarget target, BoundsFrame frame, uint flags, WindowErrorCode failure)
    {
        if (RestoreForPlacement(handle, failure) is { } notRestored)
            return notRestored;

        // An unknown scale, before or after, is a change that cannot be ruled out (MonitorScale): placed again
        var scaleBefore = MonitorScale(handle);
        var placed = PlaceOnce(handle, target, frame, flags, failure);
        if (!placed.Success || scaleBefore is { } before && MonitorScale(handle) == before)
            return placed;

        WaitForSettledFrame(handle, DpiSettleTimeout);
        return PlaceOnce(handle, target, frame, flags, failure);
    }

    private WindowChangeResult PlaceOnce(nint handle, PlacementTarget target, BoundsFrame frame, uint flags, WindowErrorCode failure)
    {
        if (ReadFrame(handle) is not { } current)
            return WindowGone(handle);

        var (now, insets) = frame == BoundsFrame.Visible ? (current.Visible, current.Insets) : (current.Outer, FrameInsets.None);
        var rect = insets.Outer(new WindowRect(target.X ?? now.X, target.Y ?? now.Y, target.Width ?? now.Width, target.Height ?? now.Height));
        var keep = (target.X is null ? NativeEnums.SWP_NOMOVE : 0) | (target.Width is null ? NativeEnums.SWP_NOSIZE : 0);
        return SetPosition(handle, rect, flags | keep, failure);
    }

    /// <summary>
    /// The scale factor of the monitor the window is on; null when its monitor cannot be read (issue #12), which the
    /// placements take as a scale change they cannot rule out: they place again, and move across monitors in two steps.
    /// </summary>
    private double? MonitorScale(nint handle)
    {
        var monitors = _monitorService.GetAllMonitors();
        return _monitorService.GetMonitorIndex(handle) is { } index && index >= 0 && index < monitors.Count
            ? monitors[index].ScaleFactor
            : null;
    }

    /// <summary>
    /// Restores a minimized or maximized window to normal before it is placed or measured (roadmap M2.3): a maximized
    /// window that is placed keeps WS_MAXIMIZE (it is reported maximized at the new size and jumps back on the next
    /// restore), and a minimized one is measured as its 160x28 icon. SW_SHOWNOACTIVATE restores without activating the
    /// window, unlike SW_RESTORE, so keyboard input stays where it was. A window minimized from maximized comes back
    /// maximized first, so the command is sent at most twice. Null when the window is normal; MoveFailed or ResizeFailed
    /// (<paramref name="failure"/>) when it stays minimized or maximized. A hidden window that is minimized or maximized (a
    /// tray app hides its window after minimizing it) is not restored: SW_SHOWNOACTIVATE would show it behind its app's
    /// back, as the capture restore refuses to (<see cref="MinimizedWindowRestore"/>); it fails with
    /// <paramref name="failure"/> and nothing is sent.
    /// </summary>
    /// <exception cref="WindowNotRespondingException">The window did not answer in time.</exception>
    private WindowChangeResult? RestoreForPlacement(nint handle, WindowErrorCode failure)
    {
        if (IsMinimizedOrMaximized(handle) && !_nativeWrapper.IsWindowVisible(handle))
        {
            var state = _nativeWrapper.IsIconic(handle) ? "minimized" : "maximized";
            return WindowChangeResult.Failed(handle, failure,
                $"The window is hidden and {state}: its application hid it (for example in the notification area), and restoring " +
                $"it to place it would show it, so it was not shown and not {(failure == WindowErrorCode.ResizeFailed ? "resized" : "moved")}. " +
                "Show it through its application, or ask the user.");
        }

        for (var i = 0; i < 2 && IsMinimizedOrMaximized(handle); i++)
        {
            var before = (_nativeWrapper.IsIconic(handle), _nativeWrapper.IsZoomed(handle));
            ShowWindow(handle, NativeEnums.SW_SHOWNOACTIVATE);

            // An application that animates the restore takes a moment to leave the state
            WaitUntil(() => (_nativeWrapper.IsIconic(handle), _nativeWrapper.IsZoomed(handle)) != before || !_nativeWrapper.IsWindow(handle),
                _stateChangeTimeout);
        }

        if (!IsMinimizedOrMaximized(handle))
            return null;

        if (!_nativeWrapper.IsWindow(handle))
            return WindowGone(handle);

        var change = failure == WindowErrorCode.ResizeFailed ? "resized" : "moved";
        return WindowChangeResult.Failed(handle, failure,
            $"The window is {ObservedState(handle)} and did not return to normal when it was restored (the application may have " +
            $"refused it), so it was not {change}: a {ObservedState(handle)} window cannot be placed. Check the window " +
            "(find_windows with its handle), or ask the user to restore it.");
    }

    private bool IsMinimizedOrMaximized(nint handle) => _nativeWrapper.IsIconic(handle) || _nativeWrapper.IsZoomed(handle);

    /// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/> has passed.</summary>
    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && waited.Elapsed < timeout)
        {
            Thread.Sleep(StatePollInterval);
        }
    }

    public WindowChangeResult SetWindowState(nint handle, WindowStateRequest state) =>
        _targetCheck.ChangeRefusal(handle) ?? ApplyState(handle, state);

    public WindowRect? GetNormalBounds(nint handle) =>
        handle != nint.Zero && _nativeWrapper.GetWindowPlacement(handle) is { } placement
            ? _reads.NormalBounds(handle, placement, _monitorService.GetAllMonitors())
            : null;

    public WindowChangeResult SetMinimizedBounds(nint handle, int x, int y, int width, int height)
    {
        if (_targetCheck.ChangeRefusal(handle) is { } refused)
            return refused;

        if (!_nativeWrapper.IsIconic(handle))
        {
            return WindowChangeResult.Failed(handle, WindowErrorCode.MoveFailed,
                $"The window is {ObservedState(handle)}, not minimized, so its restore rectangle was not set. Nothing was changed.");
        }

        // SW_SHOWMINNOACTIVE would show a window its app hid after minimizing it (a tray app), as the placements refuse to
        if (!_nativeWrapper.IsWindowVisible(handle))
        {
            return WindowChangeResult.Failed(handle, WindowErrorCode.MoveFailed,
                "The window is hidden and minimized: its application hid it (for example in the notification area), and setting " +
                "its restore rectangle would show it, so nothing was changed. Show it through its application, or ask the user.");
        }

        if (_nativeWrapper.GetWindowPlacement(handle) is not { } current)
            return _nativeWrapper.IsWindow(handle) ? NotPlaced(handle, 0, WindowErrorCode.MoveFailed) : WindowGone(handle);

        var target = new WindowRect(x, y, width, height);
        var normal = _reads.IsToolWindow(handle) ? target : WorkspaceCoordinates.ToWorkspace(target, _monitorService.GetAllMonitors());
        var placement = current with
        {
            ShowCmd = NativeEnums.SW_SHOWMINNOACTIVE,
            // Whether the window comes back maximized stays its own; no icon position is set
            Flags = current.Flags & NativeEnums.WPF_RESTORETOMAXIMIZED,
            NormalPosition = (normal.X, normal.Y, normal.X + normal.Width, normal.Y + normal.Height)
        };

        var set = _windowCalls.Run(handle, () => _nativeWrapper.SetWindowPlacement(handle, placement));
        if (!set.Succeeded)
            return NotPlaced(handle, set.Win32Error, WindowErrorCode.MoveFailed);

        if (_nativeWrapper.GetWindowPlacement(handle) is { } now && Near(now.NormalPosition, placement.NormalPosition) &&
            _nativeWrapper.IsIconic(handle))
        {
            return WindowChangeResult.Done(handle);
        }

        return _nativeWrapper.IsWindow(handle)
            ? WindowChangeResult.Failed(handle, WindowErrorCode.MoveFailed,
                $"Windows accepted the window's new restore rectangle, but the window is {ObservedState(handle)} with another one " +
                "now (the application may have changed it). Check the window (find_windows with its handle).")
            : WindowGone(handle);

        static bool Near((int Left, int Top, int Right, int Bottom) a, (int Left, int Top, int Right, int Bottom) b) =>
            Math.Abs(a.Left - b.Left) <= 1 && Math.Abs(a.Top - b.Top) <= 1 && Math.Abs(a.Right - b.Right) <= 1 &&
            Math.Abs(a.Bottom - b.Bottom) <= 1;
    }

    /// <summary>
    /// Changes the show state of a window that was checked already, and reports success only when the window is seen in
    /// that state within <see cref="StateChangeTimeout"/>: ShowWindow's return value says whether the window was visible
    /// before the call, not whether the command worked.
    /// </summary>
    private WindowChangeResult ApplyState(nint handle, WindowStateRequest state)
    {
        var command = state switch
        {
            WindowStateRequest.Minimize => NativeEnums.SW_MINIMIZE,
            WindowStateRequest.Maximize => NativeEnums.SW_MAXIMIZE,
            WindowStateRequest.Restore => NativeEnums.SW_RESTORE,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a window state.")
        };

        var wasMinimized = _nativeWrapper.IsIconic(handle);
        ShowWindow(handle, command);
        return WaitForState(handle, state, wasMinimized);
    }

    /// <summary>
    /// Waits up to <see cref="StateChangeTimeout"/> for the window to be in <paramref name="state"/> (applications that
    /// animate take a moment), polling the state, which reads it without waiting for the window's thread.
    /// </summary>
    private WindowChangeResult WaitForState(nint handle, WindowStateRequest state, bool wasMinimized)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!IsInState(handle, state, wasMinimized))
        {
            if (!_nativeWrapper.IsWindow(handle))
                return WindowGone(handle);

            if (waited.Elapsed >= _stateChangeTimeout)
            {
                return WindowChangeResult.Failed(handle, WindowErrorCode.StateChangeFailed,
                    $"The window did not become {Describe(state)} within {_stateChangeTimeout.TotalMilliseconds:0} ms: it is " +
                    $"{ObservedState(handle)}. The application may have refused or delayed the change, or it may run with higher " +
                    "privileges (for example as administrator), which Windows does not let this server change. Check the window " +
                    "(find_windows with its handle) before acting on it.");
            }

            Thread.Sleep(StatePollInterval);
        }

        return WindowChangeResult.Done(handle);
    }

    /// <summary>
    /// Whether the window is in <paramref name="state"/>. Restore is the Windows Restore command: a minimized window is no
    /// longer minimized (it returns to normal, or to maximized when it was maximized before it was minimized), and a
    /// window that was not minimized is no longer maximized.
    /// </summary>
    private bool IsInState(nint handle, WindowStateRequest state, bool wasMinimized) => state switch
    {
        WindowStateRequest.Minimize => _nativeWrapper.IsIconic(handle),
        WindowStateRequest.Maximize =>
            !_nativeWrapper.IsIconic(handle) && _nativeWrapper.IsZoomed(handle) && _nativeWrapper.IsWindowVisible(handle),
        _ => !_nativeWrapper.IsIconic(handle) && _nativeWrapper.IsWindowVisible(handle) && (wasMinimized || !_nativeWrapper.IsZoomed(handle))
    };

    private static string Describe(WindowStateRequest state) => state switch
    {
        WindowStateRequest.Minimize => "minimized",
        WindowStateRequest.Maximize => "maximized",
        _ => "restored"
    };

    private string ObservedState(nint handle) =>
        _nativeWrapper.IsIconic(handle) ? "minimized"
        : !_nativeWrapper.IsWindowVisible(handle) ? "hidden"
        : _nativeWrapper.IsZoomed(handle) ? "maximized"
        : "normal";

    public WindowChangeResult SnapWindow(nint handle, SnapPosition position, int? monitorIndex = null, bool bringToFront = true)
    {
        if (_targetCheck.ChangeRefusal(handle) is { } refused)
            return refused;

        // Its own monitor when none is named; one that cannot be read is no reason to snap on monitor 0 (issue #12)
        if ((monitorIndex ?? _monitorService.GetMonitorIndex(handle)) is not { } targetMonitor)
            return OwnMonitorUnreadable(handle);

        if (MonitorIndexRefusal(handle, targetMonitor) is { } invalid)
            return invalid;

        MonitorRect bounds;
        try
        {
            bounds = _monitorService.CalculateSnapBounds(targetMonitor, position);
        }
        catch (ArgumentOutOfRangeException)
        {
            // The displays changed between the check and the calculation
            return MonitorIndexRefusal(handle, targetMonitor) ?? throw new InvalidOperationException("The monitor list changed.");
        }

        return Place(handle, new PlacementTarget(bounds.X, bounds.Y, bounds.Width, bounds.Height), BoundsFrame.Visible,
            PlacementFlags(bringToFront), WindowErrorCode.MoveFailed);
    }

    public WindowChangeResult MoveWindowToMonitor(
        nint handle, int monitorIndex, MonitorPositioning positioning = MonitorPositioning.Center, bool bringToFront = true)
    {
        if (_targetCheck.ChangeRefusal(handle) is { } refused)
            return refused;

        var monitors = _monitorService.GetAllMonitors();
        if (MonitorIndexRefusal(handle, monitorIndex, monitors.Count) is { } invalid)
            return invalid;

        // A monitor that cannot be read is never the target (issue #12): the window is moved, and checked afterwards
        var maximize = positioning == MonitorPositioning.Maximize;
        if (maximize && _monitorService.GetMonitorIndex(handle) == monitorIndex)
            return OnMonitor(handle, ApplyState(handle, WindowStateRequest.Maximize), monitorIndex, maximize);

        var moved = MoveOntoMonitor(handle, monitors[monitorIndex], positioning,
            // SW_MAXIMIZE raises (and activates) the window anyway, so the move before it keeps the z-order
            maximize ? NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE : PlacementFlags(bringToFront));
        if (!moved.Success)
            return moved;

        // An app that keeps or restores its own position is still on the old monitor: SW_MAXIMIZE would maximize it there,
        // and activate it, so it is sent only once the window is on the target monitor
        if (maximize && _monitorService.GetMonitorIndex(handle) != monitorIndex)
            return OnMonitor(handle, moved, monitorIndex, maximized: false);

        return OnMonitor(handle, maximize ? ApplyState(handle, WindowStateRequest.Maximize) : moved, monitorIndex, maximize);
    }

    /// <summary>
    /// Places the window in <paramref name="target"/>'s work area at its normal size, clamped to the work area so that the
    /// whole visible frame, title bar included, is on that monitor. The window is restored first and measured only then
    /// (a minimized window measures as its 160x28 icon). Across a scale change the window is moved in two steps: onto the
    /// target monitor without changing its size (SWP_NOSIZE), where a per-monitor-aware application rescales itself for
    /// the new DPI, then, once it has settled, sized from the size the application chose and positioned. The size is never
    /// scaled by the ratio of the scales in advance: the application would scale it a second time, and DWM already scales
    /// a window that is not per-monitor aware.
    /// </summary>
    private WindowChangeResult MoveOntoMonitor(nint handle, MonitorInfo target, MonitorPositioning positioning, uint flags)
    {
        if (RestoreForPlacement(handle, WindowErrorCode.MoveFailed) is { } notRestored)
            return notRestored;

        var workArea = target.WorkArea;
        if (MonitorScale(handle) != target.ScaleFactor)
        {
            if (ReadFrame(handle) is not { } before)
                return WindowGone(handle);

            // Centred, so that the target monitor holds most of the window even before it is clamped
            var (x, y) = Position(MonitorPositioning.Center, workArea, before.Visible.Width, before.Visible.Height);
            var onto = before.Insets.Outer(before.Visible with { X = x, Y = y });
            var arrived = SetPosition(handle, onto, NativeEnums.SWP_NOSIZE | NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE,
                WindowErrorCode.MoveFailed);
            if (!arrived.Success)
                return arrived;

            WaitForSettledFrame(handle, DpiSettleTimeout);
        }

        if (ReadFrame(handle) is not { } frame)
            return WindowGone(handle);

        var width = Math.Clamp(frame.Visible.Width, 1, Math.Max(1, workArea.Width));
        var height = Math.Clamp(frame.Visible.Height, 1, Math.Max(1, workArea.Height));
        var (left, top) = Position(positioning, workArea, width, height);
        return SetPosition(handle, frame.Insets.Outer(new WindowRect(left, top, width, height)), flags, WindowErrorCode.MoveFailed);
    }

    /// <summary>The top-left corner of a window of this size at <paramref name="positioning"/> in the work area.</summary>
    private static (int X, int Y) Position(MonitorPositioning positioning, MonitorRect workArea, int width, int height) => positioning switch
    {
        MonitorPositioning.Center or MonitorPositioning.Maximize =>
            (workArea.X + (workArea.Width - width) / 2, workArea.Y + (workArea.Height - height) / 2),
        MonitorPositioning.TopLeft or MonitorPositioning.Restore => (workArea.X, workArea.Y),
        MonitorPositioning.TopRight => (workArea.X + workArea.Width - width, workArea.Y),
        MonitorPositioning.BottomLeft => (workArea.X, workArea.Y + workArea.Height - height),
        MonitorPositioning.BottomRight => (workArea.X + workArea.Width - width, workArea.Y + workArea.Height - height),
        _ => throw new ArgumentOutOfRangeException(nameof(positioning), positioning, "Not a positioning.")
    };

    /// <summary>
    /// <paramref name="result"/> when it failed; otherwise Done only when the window is on monitor
    /// <paramref name="monitorIndex"/> (MonitorFromWindow) afterwards, and MoveFailed when it is not: an application can
    /// keep or restore its own position, and SW_MAXIMIZE maximizes a window on the monitor it is on. A monitor that cannot be
    /// read afterwards is not the target either (issue #12): the move is not confirmed, so it is MoveFailed (InvalidHandle
    /// when the window closed).
    /// </summary>
    private WindowChangeResult OnMonitor(nint handle, WindowChangeResult result, int monitorIndex, bool maximized)
    {
        if (!result.Success)
            return result;

        if (_monitorService.GetMonitorIndex(handle) is not { } actual)
        {
            return _nativeWrapper.IsWindow(handle)
                ? WindowChangeResult.Failed(handle, WindowErrorCode.MoveFailed,
                    "The window was moved, but its monitor could not be read afterwards (the displays may be changing), so it is " +
                    $"not known to be on monitor {monitorIndex}. Check it with find_windows.")
                : WindowGone(handle);
        }

        if (actual == monitorIndex)
            return WindowChangeResult.Done(handle);

        return WindowChangeResult.Failed(handle, WindowErrorCode.MoveFailed, maximized
            ? $"The window was maximized on monitor {actual}, not on monitor {monitorIndex}: it did not move there. Check it with find_windows."
            : $"The window is on monitor {actual}, not on monitor {monitorIndex}: it did not move there (the application may keep its " +
              "own position). Check it with find_windows.");
    }

    /// <summary>
    /// A snap without a monitor, of a window whose own monitor cannot be read (issue #12): it closed (InvalidHandle), or the
    /// displays changed during the lookup (MoveFailed). It used to snap on monitor 0; now nothing is changed.
    /// </summary>
    private WindowChangeResult OwnMonitorUnreadable(nint handle) =>
        _nativeWrapper.IsWindow(handle)
            ? WindowChangeResult.Failed(handle, WindowErrorCode.MoveFailed,
                "The window's monitor could not be read (the displays may be changing), so it was not snapped: nothing was changed. " +
                "Try again, or pass monitor to name one.")
            : WindowGone(handle);

    private WindowChangeResult? MonitorIndexRefusal(nint handle, int monitorIndex) =>
        MonitorIndexRefusal(handle, monitorIndex, _monitorService.GetAllMonitors().Count);

    /// <summary>InvalidMonitorIndex with the valid range when <paramref name="monitorIndex"/> is not a monitor, otherwise null.</summary>
    private static WindowChangeResult? MonitorIndexRefusal(nint handle, int monitorIndex, int monitorCount) =>
        monitorIndex >= 0 && monitorIndex < monitorCount
            ? null
            : WindowChangeResult.Failed(handle, WindowErrorCode.InvalidMonitorIndex, MonitorIndexMessage(monitorIndex, monitorCount));

    /// <summary>The error of a monitor index that is out of range, with the valid range. Nothing was changed.</summary>
    public static string MonitorIndexMessage(int monitorIndex, int monitorCount) =>
        monitorCount == 0
            ? $"Monitor index {monitorIndex} does not exist: no monitor was found. Nothing was changed."
            : $"Monitor index {monitorIndex} does not exist: valid indexes are 0 to {monitorCount - 1} " +
              $"({monitorCount} monitor{(monitorCount == 1 ? string.Empty : "s")}). Nothing was changed. Call get_monitors to see them.";

    /// <summary>
    /// SetWindowPos, with a deadline (<see cref="IWindowCallRunner"/>). A refused call reports its Win32 error:
    /// ERROR_ACCESS_DENIED (5) is <see cref="WindowErrorCode.AccessDenied"/> (User Interface Privilege Isolation blocks
    /// changes to windows of more privileged processes), any other error <paramref name="failure"/>.
    /// </summary>
    /// <exception cref="WindowNotRespondingException">The window did not answer in time.</exception>
    private WindowChangeResult SetPosition(nint handle, WindowRect rect, uint flags, WindowErrorCode failure)
    {
        var placed = _windowCalls.Run(handle, () => _nativeWrapper.SetWindowPos(handle, rect.X, rect.Y, rect.Width, rect.Height, flags));
        return placed.Succeeded ? WindowChangeResult.Done(handle) : NotPlaced(handle, placed.Win32Error, failure);
    }

    /// <summary>ShowWindow, with a deadline (<see cref="IWindowCallRunner"/>). Its return value is the previous visibility, so it is not returned.</summary>
    /// <exception cref="WindowNotRespondingException">The window did not answer in time.</exception>
    private void ShowWindow(nint handle, int command) =>
        _windowCalls.Run(handle, () => _nativeWrapper.ShowWindow(handle, command));
}
