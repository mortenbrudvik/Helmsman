using System.Text.RegularExpressions;
using WinDesktopMcp.Models;
using WinDesktopMcp.Native;
using WinDesktopMcp.Services.Interfaces;
using static WinDesktopMcp.Services.WindowChangeFailures;
using static WinDesktopMcp.Services.WindowReads;

namespace WinDesktopMcp.Services;

/// <summary>
/// Lists, reads, moves, resizes, restores, minimizes and closes windows.
/// </summary>
/// <remarks>
/// SetWindowPos and ShowWindow on another thread's window wait until that thread answers, and the window tools run
/// inside the process-wide input gate. So every such call goes through <see cref="IWindowCallRunner"/>: a window
/// that does not answer is given up on at its deadline with <see cref="WindowNotRespondingException"/>, and a hung
/// application cannot hold the gate, and with it every input and window tool, until it recovers.
/// </remarks>
public class WindowService : IWindowService
{
    /// <summary>
    /// The longest delay the batches sleep between two windows, whatever delayBetweenMs asks for: they sleep inside
    /// the input gate, so an agent's value must not hold it for long.
    /// </summary>
    public const int MaxDelayBetweenMs = 5000;

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
    private readonly IWindowZOrder _zOrder;
    private readonly IWindowClosing _closing;
    private readonly WindowReads _reads;
    private readonly WindowInfoReader _windowInfos;
    private readonly WindowTargetCheck _targetCheck;

    /// <param name="monitorService">Monitor lookups.</param>
    /// <param name="nativeWrapper">The window calls.</param>
    /// <param name="hostGuard">Recognizes the agent's host windows (WindowInfo.IsAgentHost).</param>
    /// <param name="targetPolicy">
    /// The server's target policy (roadmap M1.12). Every window change (move, resize, bounds, state, snap, move to a
    /// monitor, and their batches) refuses a window it denies, so these tools never restore, raise or move it (input and
    /// the app itself still can).
    /// </param>
    /// <param name="zOrder">set_window_zorder.</param>
    /// <param name="closing">close_window's close and the reads of what a close leaves behind.</param>
    /// <param name="windowCalls">
    /// Runs the calls that can block on another thread's window (SetWindowPos, ShowWindow) with a deadline; the one
    /// <paramref name="zOrder"/> uses.
    /// </param>
    /// <param name="stateChangeTimeout">
    /// How long a state change waits to see the new state; default <see cref="StateChangeTimeout"/>. Tests pass zero.
    /// </param>
    public WindowService(
        IMonitorService monitorService,
        INativeWindowWrapper nativeWrapper,
        IHostGuard hostGuard,
        ITargetPolicy targetPolicy,
        IWindowZOrder zOrder,
        IWindowClosing closing,
        IWindowCallRunner windowCalls,
        TimeSpan? stateChangeTimeout = null)
    {
        _monitorService = monitorService;
        _nativeWrapper = nativeWrapper;
        _reads = new WindowReads(nativeWrapper);
        _windowInfos = new WindowInfoReader(nativeWrapper, monitorService, hostGuard ?? throw new ArgumentNullException(nameof(hostGuard)), _reads);
        _targetCheck = new WindowTargetCheck(nativeWrapper, _reads, targetPolicy ?? throw new ArgumentNullException(nameof(targetPolicy)));
        _zOrder = zOrder ?? throw new ArgumentNullException(nameof(zOrder));
        _closing = closing ?? throw new ArgumentNullException(nameof(closing));
        _windowCalls = windowCalls ?? throw new ArgumentNullException(nameof(windowCalls));
        _stateChangeTimeout = stateChangeTimeout ?? StateChangeTimeout;
    }

    /// <summary>The delay a batch sleeps between two windows for <paramref name="delayBetweenMs"/>: 0 to <see cref="MaxDelayBetweenMs"/>.</summary>
    public static TimeSpan BatchDelay(int delayBetweenMs) =>
        TimeSpan.FromMilliseconds(Math.Clamp(delayBetweenMs, 0, MaxDelayBetweenMs));

    public IReadOnlyList<WindowInfo> GetAllWindows(bool includeMinimized = true) =>
        FindWindows(new WindowQuery { IncludeMinimized = includeMinimized });

    public IReadOnlyList<WindowInfo> FindWindows(
        string? titleContains = null,
        string? processName = null,
        int? processId = null,
        nint? handle = null,
        bool visibleOnly = true) =>
        FindWindows(new WindowQuery
        {
            TitleContains = titleContains,
            ProcessName = processName,
            ProcessId = processId,
            Handle = handle,
            VisibleOnly = visibleOnly
        });

    public IReadOnlyList<WindowInfo> FindWindows(WindowQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1)
            throw new ArgumentOutOfRangeException(nameof(query), query.Limit, "limit must be at least 1.");

        var titleRegex = query.TitleRegex is { Length: > 0 } pattern
            ? WindowTitles.CompileTitleRegex(pattern, out var invalid) ?? throw new ArgumentException(invalid, nameof(query))
            : null;

        // A handle is read directly. Otherwise collect the top-level windows first and inspect them
        // afterwards, so no filter code runs inside the native enumeration callback.
        IReadOnlyList<nint>? topLevelWindows = null;
        IReadOnlyList<nint> candidates;
        if (query.Handle is { } handle)
        {
            candidates = IsValidWindow(handle) ? [handle] : [];
        }
        else
        {
            topLevelWindows = EnumerateTopLevelWindows(_nativeWrapper);
            candidates = topLevelWindows;
        }

        var session = new ReadSession(_nativeWrapper, topLevelWindows);
        var windows = new List<WindowInfo>();
        foreach (var hWnd in candidates)
        {
            if (Match(hWnd, query, titleRegex, session) is { } info)
                windows.Add(info);
        }

        return Order(windows, query.Sort, query.Limit);
    }

    /// <summary>
    /// The windows in <paramref name="sort"/> order, at most <paramref name="limit"/>. They arrive in z-order (EnumWindows
    /// enumerates front to back), and the sorts are stable, so equal titles or processes keep their z-order.
    /// </summary>
    private static IReadOnlyList<WindowInfo> Order(List<WindowInfo> windows, WindowSort sort, int? limit)
    {
        IEnumerable<WindowInfo> ordered = sort switch
        {
            WindowSort.Title => windows.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase),
            WindowSort.Process => windows.OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase),
            _ => windows
        };

        if (limit is { } max)
            ordered = ordered.Take(max);

        return ordered as List<WindowInfo> ?? ordered.ToList();
    }

    public WindowResolution ResolveWindow(WindowSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        if (SelectorRefusal(selector) is { } invalid)
            return WindowResolution.Failed(InvalidArgumentsCode, invalid);

        // A pattern that runs too long is the pattern's fault on every path (the listing, a handle's check, the
        // foreground's check): InvalidArguments, never a mismatch that blames the window
        try
        {
            return Resolve(selector);
        }
        catch (RegexMatchTimeoutException)
        {
            return WindowResolution.Failed(InvalidArgumentsCode, RegexTimeoutMessage(selector.TitleRegex));
        }
    }

    /// <summary>The InvalidArguments message of a titleRegex that took longer than <see cref="WindowTitles.RegexTimeout"/>.</summary>
    internal static string RegexTimeoutMessage(string? pattern) =>
        $"titleRegex '{pattern}' took longer than {WindowTitles.RegexTimeout.TotalMilliseconds:0} ms on a window title, so it was " +
        "given up. Use a simpler pattern, or titleContains or titleEquals. Nothing was done.";

    private WindowResolution Resolve(WindowSelector selector)
    {
        if (selector.Handle is { } handle)
            return ResolveHandle((nint)handle, selector);

        if (selector.Foreground)
            return ResolveForeground(selector);

        var matches = FindWindows(selector.ToQuery());
        if (selector.Index is { } index)
        {
            return index < matches.Count
                ? WindowResolution.Found(matches[index])
                : WindowResolution.Failed(nameof(WindowErrorCode.WindowNotFound),
                    $"window has index {index}, but only {matches.Count} window{(matches.Count == 1 ? "" : "s")} match{(matches.Count == 1 ? "es" : "")} " +
                    $"{selector.Describe()} (index counts from 0, front to back). Nothing was done.",
                    matches);
        }

        return matches.Count switch
        {
            1 => WindowResolution.Found(matches[0]),
            0 => WindowResolution.Failed(nameof(WindowErrorCode.WindowNotFound), NoMatchMessage(selector)),
            _ => WindowResolution.Failed(nameof(WindowErrorCode.AmbiguousWindow),
                $"{matches.Count} windows match {selector.Describe()}, so the window cannot be told; nothing was done. candidates " +
                $"lists {(matches.Count > WindowResolution.MaxCandidates ? $"the first {WindowResolution.MaxCandidates}, front to back" : "them, front to back")}" +
                $"; {AgentHostHint(matches)} Call again with the one you mean as window {{handle, processId}} " +
                "(a reused handle is then refused), or narrow the filters (titleEquals, processId, className). Do not turn a " +
                "candidate's position into window.index: index counts the z-order at the time of the next call, which can change.",
                matches)
        };
    }

    /// <summary>
    /// What AmbiguousWindow's candidates mark with isAgentHost: the agent's own terminal or editor, and, when one of the
    /// candidates shown counts as the host's only because the ancestors are unknown
    /// (<see cref="WindowInfo.IsAgentHostOnlyWhileAncestorsUnknown"/>, review A4), also every older program's window. Then
    /// the agent must not be told that each marked candidate is its own window, as the host refusals do not tell it either.
    /// </summary>
    private static string AgentHostHint(IReadOnlyList<WindowInfo> matches) =>
        matches.Take(WindowResolution.MaxCandidates).Any(m => m.IsAgentHostOnlyWhileAncestorsUnknown)
            ? "isAgentHost marks your own terminal or editor, and, while the server cannot read its ancestor processes, every " +
              "window of a program that started before the server too, so a marked candidate may be another app's " +
              "(get_server_info warns about it)."
            : "isAgentHost marks your own terminal or editor.";

    /// <summary>The errorCode of an argument a tool cannot use: InvalidArguments, as the tool-error filter reports it.</summary>
    private const string InvalidArgumentsCode = "InvalidArguments";

    /// <summary>Why a selector cannot be used at all (InvalidArguments), or null when it can.</summary>
    private static string? SelectorRefusal(WindowSelector selector)
    {
        if (!selector.HasFilters && !selector.Handle.HasValue && !selector.Foreground)
        {
            return "window names no window: pass at least one of handle, foreground, titleContains, titleEquals, titleRegex, " +
                   "processName, processId and className. An empty selector never matches every window. Nothing was done.";
        }

        if (BlankFilter(selector) is { } blank)
        {
            return $"window.{blank} is blank, so it would match every window; leave it out or give the text to match. Nothing was done.";
        }

        if (selector.Handle is <= 0)
            return $"window.handle must be a window handle (a positive integer from find_windows), but it is {selector.Handle}. Nothing was done.";

        if (selector.ProcessId is <= 0)
            return $"window.processId must be a positive process ID, but it is {selector.ProcessId}. Nothing was done.";

        if (selector.Index is < 0)
            return $"window.index counts matches from 0 (the front one), but it is {selector.Index}. Nothing was done.";

        if (selector.Handle.HasValue && (selector.Foreground || selector.Index.HasValue))
        {
            return "window.handle names one window, so foreground and index do not apply; pass the handle with the filters it must " +
                   "still match (processId, processName, a title or className), or leave the handle out. Nothing was done.";
        }

        if (selector.Foreground && selector.Index.HasValue)
            return "window.foreground names one window, so index does not apply. Nothing was done.";

        if (selector.TitleRegex is { Length: > 0 } pattern && WindowTitles.CompileTitleRegex(pattern, out var error) is null)
            return $"{error} Nothing was done.";

        return null;
    }

    /// <summary>
    /// The name of a filter that is given but empty once normalized (a blank title, a process name of only ".exe"): such a
    /// filter would count as naming a window while matching every one. Null when there is none.
    /// </summary>
    private static string? BlankFilter(WindowSelector selector)
    {
        // An empty string is a filter left out (HasFilters ignores it); text that normalizes to nothing is refused
        if (selector.TitleContains is { Length: > 0 } contains && WindowTitles.Normalize(contains).Length == 0)
            return "titleContains";
        if (selector.TitleEquals is { Length: > 0 } equals && WindowTitles.Normalize(equals).Length == 0)
            return "titleEquals";
        if (selector.ProcessName is { Length: > 0 } processName && WindowMatching.WithoutExe(processName).Length == 0)
            return "processName";
        if (selector.ClassName is { Length: > 0 } className && string.IsNullOrWhiteSpace(className))
            return "className";
        return null;
    }

    /// <summary>
    /// A selector's handle, checked against the selector's filters: InvalidHandle when it is no window, HandleMismatch
    /// when the window no longer matches what the caller expected of it (the handle was most likely reused).
    /// </summary>
    private WindowResolution ResolveHandle(nint handle, WindowSelector selector)
    {
        if (!IsValidWindow(handle) || _windowInfos.ReadWindowInfo(handle) is not { } window)
        {
            return WindowResolution.Failed(nameof(WindowErrorCode.InvalidHandle),
                $"{InvalidHandleMessage}: window.handle {handle} is no window (it closed). Nothing was done. Find the window again " +
                "with find_windows, or pass a selector without the handle.");
        }

        if (WindowMatching.Mismatches(window, selector) is { Count: > 0 } differences)
        {
            return WindowResolution.Failed(nameof(WindowErrorCode.HandleMismatch),
                $"Window {handle} is not the window expected: {string.Join("; ", differences)}. The window most likely closed and its " +
                $"handle was reused by another window ({Describe(window)}). Nothing was done. Find the window again with find_windows.");
        }

        return WindowResolution.Found(window);
    }

    /// <summary>The window in the foreground, checked against the selector's filters (WindowNotFound when it does not match).</summary>
    private WindowResolution ResolveForeground(WindowSelector selector)
    {
        var foreground = _nativeWrapper.GetForegroundWindow();
        if (foreground == nint.Zero || _windowInfos.ReadWindowInfo(foreground) is not { } window)
        {
            return WindowResolution.Failed(nameof(WindowErrorCode.WindowNotFound),
                "window.foreground: no window is in the foreground right now (windows may be switching, or a UAC prompt or the lock " +
                "screen is shown). Nothing was done. Try again shortly, or name the window by its handle.");
        }

        if (WindowMatching.Mismatches(window, selector) is { Count: > 0 } differences)
        {
            return WindowResolution.Failed(nameof(WindowErrorCode.WindowNotFound),
                $"The window in the foreground ({Describe(window)}) does not match {selector.Describe()}: " +
                $"{string.Join("; ", differences)}. Nothing was done. Bring the window you mean to the front with focus_window, or name it " +
                "without foreground.");
        }

        return WindowResolution.Found(window);
    }

    private static string Describe(WindowInfo window) =>
        $"'{window.Title}', {window.ProcessName}, PID {window.ProcessId}, class {window.ClassName}";

    /// <summary>The WindowNotFound message of a selector that matched nothing, with what the listing leaves out.</summary>
    private static string NoMatchMessage(WindowSelector selector)
    {
        var hints = new List<string>();
        if (!string.IsNullOrEmpty(selector.ProcessName) && !selector.ProcessNameContains)
            hints.Add("processName is the whole process name (set processNameContains for a part of it)");
        if (!selector.IncludeCloaked)
            hints.Add("windows on other virtual desktops need includeCloaked");
        if (!selector.IncludeUntitled && string.IsNullOrEmpty(selector.ClassName))
            hints.Add("untitled windows need includeUntitled or className");
        hints.Add("hidden windows only by handle");

        return $"No window matches {selector.Describe()}. Nothing was done. Check what is open with find_windows; {string.Join(", ", hints)}.";
    }

    public WindowDetails? GetWindowDetails(nint handle)
    {
        if (GetWindowInfo(handle) is not { } window)
            return null;

        var style = (long)_nativeWrapper.GetWindowLongPtr(handle, NativeEnums.GWL_STYLE);
        var extendedStyle = (long)_nativeWrapper.GetWindowLongPtr(handle, NativeEnums.GWL_EXSTYLE);
        var dpi = _nativeWrapper.GetDpiForWindow(handle);
        int? zOrder = null;
        if (_nativeWrapper.GetAncestor(handle, WindowAncestor.Root) == handle)
        {
            var index = EnumerateTopLevelWindows(_nativeWrapper).IndexOf(handle);
            zOrder = index >= 0 ? index : null;
        }

        return new WindowDetails(
            window,
            IsForeground: _nativeWrapper.GetForegroundWindow() == handle,
            ZOrder: zOrder,
            IsTopmost: (extendedStyle & NativeEnums.WS_EX_TOPMOST) != 0,
            IsEnabled: (style & NativeEnums.WS_DISABLED) == 0,
            IsResponding: !_nativeWrapper.IsHungAppWindow(handle),
            Dpi: dpi == 0 ? null : (int)dpi,
            DisplayAffinity: DisplayAffinities.Of(_nativeWrapper.GetWindowDisplayAffinity(handle)))
        {
            Restore = window.State == WindowState.Minimized ? ReadRestore(handle) : null
        };
    }

    /// <summary>
    /// Where a minimized window comes back to (issue #12): its restore rectangle in screen coordinates
    /// (<see cref="GetNormalBounds"/>), whether it comes back maximized, and the monitor of that rectangle. Null when the
    /// placement cannot be read.
    /// </summary>
    private WindowRestore? ReadRestore(nint handle)
    {
        if (_nativeWrapper.GetWindowPlacement(handle) is not { } placement)
            return null;

        var monitors = _monitorService.GetAllMonitors();
        var bounds = _reads.NormalBounds(handle, placement, monitors);
        var state = (placement.Flags & NativeEnums.WPF_RESTORETOMAXIMIZED) != 0 ? WindowState.Maximized : WindowState.Normal;
        return new WindowRestore(state, bounds, WorkspaceCoordinates.MonitorOf(bounds, monitors));
    }

    public WindowInfo? GetForegroundWindow()
    {
        var hWnd = _nativeWrapper.GetForegroundWindow();
        if (hWnd == nint.Zero)
            return null;

        return _windowInfos.ReadWindowInfo(hWnd);
    }

    public WindowInfo? GetWindowInfo(nint handle)
    {
        if (!IsValidWindow(handle))
            return null;

        return _windowInfos.ReadWindowInfo(handle);
    }

    public bool IsUserFacingAppWindow(nint handle) =>
        IsValidWindow(handle) &&
        // Listings only enumerate top-level windows; a child control is never an app window
        _nativeWrapper.GetAncestor(handle, WindowAncestor.Root) == handle &&
        _nativeWrapper.IsWindowVisible(handle) &&
        // Listings leave out the server's own windows (see Match)
        _nativeWrapper.GetWindowThreadProcessId(handle) != _nativeWrapper.GetCurrentProcessId() &&
        PassesUserFacingRules(
            handle,
            _nativeWrapper.GetClassName(handle) ?? string.Empty,
            _nativeWrapper.GetWindowText(handle) ?? string.Empty,
            includeToolWindows: false,
            includeCloaked: false,
            includeUntitled: false,
            out _);

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

    public bool IsValidWindow(nint handle) => _reads.IsValidWindow(handle);

    public WindowChangeResult SetWindowZOrder(nint handle, ZOrderMode mode, nint? above = null) =>
        _zOrder.SetWindowZOrder(handle, mode, above);

    /// <summary>The error of a window handle that is not a window.</summary>
    public const string InvalidHandleMessage = WindowChangeFailures.InvalidHandleMessage;

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

    public WindowCloseResult CloseWindow(nint handle, bool force) => _closing.CloseWindow(handle, force);

    public WindowCloseResult? CheckClose(nint handle, bool force) => _closing.CheckClose(handle, force);

    /// <summary>The longest prompt text the close results carry; longer text is cut with an ellipsis.</summary>
    public const int MaxPromptTextLength = WindowClosingService.MaxPromptTextLength;

    /// <summary>The most button captions a prompt reports.</summary>
    public const int MaxPromptButtons = WindowClosingService.MaxPromptButtons;

    public IReadOnlyList<OwnedWindowInfo> GetOwnedWindows(nint handle, bool includeHidden = false) =>
        _closing.GetOwnedWindows(handle, includeHidden);

    public ClosePrompt? FindBlockingDialog(nint handle) => _closing.FindBlockingDialog(handle);

    public IReadOnlyList<WindowInfo> GetApplicationWindows() => _closing.GetApplicationWindows();

    public IReadOnlyList<WindowInfo> GetApplicationWindowsOnOtherDesktops() => _closing.GetApplicationWindowsOnOtherDesktops();

    /// <inheritdoc cref="WindowClosingService.PromptText"/>
    public static string? PromptText(IEnumerable<string> lines) => WindowClosingService.PromptText(lines);

    /// <inheritdoc cref="WindowClosingService.WithoutAccelerators"/>
    public static string WithoutAccelerators(string caption) => WindowClosingService.WithoutAccelerators(caption);

    /// <inheritdoc cref="WindowTargetCheck.WindowChangeRefusal"/>
    public static string WindowChangeRefusal(nint handle, TargetPolicyVerdict verdict) => WindowTargetCheck.WindowChangeRefusal(handle, verdict);

    public TargetPolicyVerdict EvaluateTargetPolicy(nint handle) => _targetCheck.EvaluateTargetPolicy(handle);

    public Task<IReadOnlyList<WindowChangeResult>> SetWindowBoundsBatchAsync(
        IReadOnlyList<(nint Handle, int X, int Y, int Width, int Height, bool BringToFront)> placements,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default) =>
        RunBatchAsync(
            placements.Count,
            i => placements[i].Handle,
            i => SetWindowFrame(placements[i].Handle, placements[i].X, placements[i].Y, placements[i].Width, placements[i].Height,
                placements[i].BringToFront),
            WindowErrorCode.MoveFailed,
            delayBetweenMs,
            cancellationToken);

    public Task<IReadOnlyList<WindowChangeResult>> SnapWindowsBatchAsync(
        IReadOnlyList<(nint Handle, SnapPosition Position, int? MonitorIndex, bool BringToFront)> placements,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default) =>
        RunBatchAsync(
            placements.Count,
            i => placements[i].Handle,
            i => SnapWindow(placements[i].Handle, placements[i].Position, placements[i].MonitorIndex, placements[i].BringToFront),
            WindowErrorCode.MoveFailed,
            delayBetweenMs,
            cancellationToken);

    public Task<IReadOnlyList<WindowChangeResult>> SetWindowStateBatchAsync(
        IReadOnlyList<(nint Handle, WindowStateRequest State)> changes,
        int delayBetweenMs = 30,
        CancellationToken cancellationToken = default) =>
        RunBatchAsync(
            changes.Count,
            i => changes[i].Handle,
            i => SetWindowState(changes[i].Handle, changes[i].State),
            WindowErrorCode.StateChangeFailed,
            delayBetweenMs,
            cancellationToken);

    /// <summary>
    /// Changes <paramref name="count"/> windows one after another. A window that fails does not stop the batch: its result
    /// says why, with NotResponding for a window that did not answer in time and <paramref name="failure"/> for an
    /// unexpected error. The batch pauses <paramref name="delayBetweenMs"/> after each window that was changed.
    /// </summary>
    private static async Task<IReadOnlyList<WindowChangeResult>> RunBatchAsync(
        int count,
        Func<int, nint> handleOf,
        Func<int, WindowChangeResult> change,
        WindowErrorCode failure,
        int delayBetweenMs,
        CancellationToken cancellationToken)
    {
        var results = new List<WindowChangeResult>(count);

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            WindowChangeResult result;
            try
            {
                result = change(i);
            }
            catch (WindowNotRespondingException ex)
            {
                result = WindowChangeResult.Failed(handleOf(i), WindowErrorCode.NotResponding, ex.Message);
            }
            catch (Exception ex)
            {
                result = WindowChangeResult.Failed(handleOf(i), failure, ex.Message);
            }

            results.Add(result);

            if (result.Success)
            {
                await PauseBetweenWindowsAsync(delayBetweenMs, i, count, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<WindowCloseResult>> CloseWindowsBatchAsync(
        IReadOnlyList<nint> handles,
        bool force,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default)
    {
        var results = new List<WindowCloseResult>();

        for (var i = 0; i < handles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var handle = handles[i];
            WindowCloseResult result;
            try
            {
                result = CloseWindow(handle, force);
            }
            catch (Exception ex)
            {
                result = WindowCloseResult.Failed(handle, WindowErrorCode.CloseFailed, ex.Message);
            }

            results.Add(result);

            // Pause only after a close message was actually sent
            if (result.Success)
            {
                await PauseBetweenWindowsAsync(delayBetweenMs, i, handles.Count, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    /// <summary>
    /// The batches' pause after window <paramref name="index"/> of <paramref name="count"/>: none after the last one,
    /// <see cref="BatchDelay"/> otherwise. A cancelled call ends the pause at once.
    /// </summary>
    private static Task PauseBetweenWindowsAsync(int delayBetweenMs, int index, int count, CancellationToken cancellationToken) =>
        delayBetweenMs > 0 && index < count - 1
            ? Task.Delay(BatchDelay(delayBetweenMs), cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// Applies <paramref name="query"/> to one window and reads it if it matches. The cheap native
    /// reads and filters come first; the process name lookup comes last.
    /// </summary>
    private WindowInfo? Match(nint hWnd, WindowQuery query, Regex? titleRegex, ReadSession session)
    {
        if (query.VisibleOnly && !_nativeWrapper.IsWindowVisible(hWnd))
            return null;

        var className = _nativeWrapper.GetClassName(hWnd) ?? string.Empty;
        if (!string.IsNullOrEmpty(query.ClassName) &&
            !string.Equals(className, query.ClassName, StringComparison.OrdinalIgnoreCase))
            return null;

        var title = _nativeWrapper.GetWindowText(hWnd) ?? string.Empty;
        bool isCloaked;
        if (query.SkipsUserFacingFilter)
        {
            isCloaked = _nativeWrapper.IsCloaked(hWnd);
        }
        else if (!PassesUserFacingRules(hWnd, className, title, query.IncludeToolWindows, query.IncludeCloaked, query.IncludeUntitled,
                     out isCloaked))
        {
            return null;
        }

        // Titles as WindowTitles reads them: invisible format characters and Edge's " and N more pages" never hide a match
        if (!string.IsNullOrEmpty(query.TitleContains) && !WindowTitles.Contains(title, query.TitleContains))
            return null;

        if (!string.IsNullOrEmpty(query.TitleEquals) && !WindowTitles.AreEqual(title, query.TitleEquals))
            return null;

        if (titleRegex != null && !WindowTitles.Matches(title, titleRegex))
            return null;

        var windowProcessId = _nativeWrapper.GetWindowThreadProcessId(hWnd);
        if (!query.SkipsUserFacingFilter && windowProcessId == session.CurrentProcessId)
            return null; // the server's own windows are not app windows

        var process = _reads.ResolveProcess(hWnd, className, title, windowProcessId, session);
        if (query.ProcessId is { } processId && !process.Has(processId))
            return null;

        if (!string.IsNullOrEmpty(query.ProcessName) &&
            !WindowMatching.ProcessNameMatches(
                session.ProcessName(process.ProcessId),
                process.HostProcessId is { } hostId ? session.ProcessName(hostId) : null,
                query.ProcessName,
                query.ProcessNameContains))
            return null;

        var info = _windowInfos.BuildWindowInfo(hWnd, title, className, isCloaked, process, session);
        if (info == null || (!query.IncludeMinimized && info.State == WindowState.Minimized))
            return null;

        return info;
    }

    /// <summary>
    /// The user-facing rules that come after visibility, cheapest first: not the desktop or a
    /// taskbar, not a tool window, a title, and not cloaked. Owned windows (dialogs) pass: they are
    /// what an agent most often looks for, and some apps make even their main window an owned window.
    /// <paramref name="isCloaked"/> is read only when the cheaper rules pass.
    /// </summary>
    private bool PassesUserFacingRules(
        nint hWnd, string className, string title, bool includeToolWindows, bool includeCloaked, bool includeUntitled, out bool isCloaked)
    {
        isCloaked = false;

        if (WindowClassification.IsShellWindowClass(className))
            return false;

        if (!includeToolWindows &&
            WindowClassification.IsToolWindow(_nativeWrapper.GetWindowLongPtr(hWnd, NativeEnums.GWL_EXSTYLE)))
            return false;

        if (!includeUntitled && string.IsNullOrEmpty(title))
            return false;

        isCloaked = _nativeWrapper.IsCloaked(hWnd);
        return includeCloaked || !isCloaked;
    }
}
