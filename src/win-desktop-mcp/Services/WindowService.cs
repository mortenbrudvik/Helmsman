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
    public static readonly TimeSpan StateChangeTimeout = WindowGeometryService.StateChangeTimeout;

    private readonly IMonitorService _monitorService;
    private readonly INativeWindowWrapper _nativeWrapper;
    private readonly IWindowGeometry _geometry;
    private readonly IWindowZOrder _zOrder;
    private readonly IWindowClosing _closing;
    private readonly WindowReads _reads;
    private readonly WindowInfoReader _windowInfos;
    private readonly WindowTargetCheck _targetCheck;

    /// <param name="monitorService">Monitor lookups.</param>
    /// <param name="nativeWrapper">The window calls.</param>
    /// <param name="hostGuard">Recognizes the agent's host windows (WindowInfo.IsAgentHost).</param>
    /// <param name="targetPolicy">
    /// The server's target policy (roadmap M1.12), whose verdict on a window <see cref="EvaluateTargetPolicy"/> reports.
    /// </param>
    /// <param name="geometry">The window placements and state changes.</param>
    /// <param name="zOrder">set_window_zorder.</param>
    /// <param name="closing">close_window's close and the reads of what a close leaves behind.</param>
    public WindowService(
        IMonitorService monitorService,
        INativeWindowWrapper nativeWrapper,
        IHostGuard hostGuard,
        ITargetPolicy targetPolicy,
        IWindowGeometry geometry,
        IWindowZOrder zOrder,
        IWindowClosing closing)
    {
        _monitorService = monitorService;
        _nativeWrapper = nativeWrapper;
        _reads = new WindowReads(nativeWrapper);
        _windowInfos = new WindowInfoReader(nativeWrapper, monitorService, hostGuard ?? throw new ArgumentNullException(nameof(hostGuard)), _reads);
        _targetCheck = new WindowTargetCheck(nativeWrapper, _reads, targetPolicy ?? throw new ArgumentNullException(nameof(targetPolicy)));
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _zOrder = zOrder ?? throw new ArgumentNullException(nameof(zOrder));
        _closing = closing ?? throw new ArgumentNullException(nameof(closing));
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
        _geometry.MoveWindow(handle, x, y, bringToFront);

    public WindowChangeResult ResizeWindow(nint handle, int width, int height) => _geometry.ResizeWindow(handle, width, height);

    public WindowChangeResult ResizeWindow(nint handle, int width, int height, bool bringToFront) =>
        _geometry.ResizeWindow(handle, width, height, bringToFront);

    public WindowChangeResult SetWindowBounds(nint handle, int x, int y, int width, int height, bool bringToFront = true) =>
        _geometry.SetWindowBounds(handle, x, y, width, height, bringToFront);

    public WindowChangeResult SetWindowFrame(nint handle, int x, int y, int width, int height, bool bringToFront = true) =>
        _geometry.SetWindowFrame(handle, x, y, width, height, bringToFront);

    /// <summary>The longest settleMs the geometry tools wait for a window to stop moving: they wait inside the input gate.</summary>
    public const int MaxSettleMs = WindowGeometryService.MaxSettleMs;

    /// <inheritdoc cref="WindowGeometryService.DpiSettleTimeout"/>
    public static readonly TimeSpan DpiSettleTimeout = WindowGeometryService.DpiSettleTimeout;

    /// <inheritdoc cref="WindowGeometryService.SettleDelay"/>
    public static TimeSpan SettleDelay(int settleMs) => WindowGeometryService.SettleDelay(settleMs);

    public void WaitForSettledFrame(nint handle, TimeSpan timeout) => _geometry.WaitForSettledFrame(handle, timeout);

    public WindowChangeResult SetWindowState(nint handle, WindowStateRequest state) => _geometry.SetWindowState(handle, state);

    public WindowRect? GetNormalBounds(nint handle) => _geometry.GetNormalBounds(handle);

    public WindowChangeResult SetMinimizedBounds(nint handle, int x, int y, int width, int height) =>
        _geometry.SetMinimizedBounds(handle, x, y, width, height);

    public WindowChangeResult SnapWindow(nint handle, SnapPosition position, int? monitorIndex = null, bool bringToFront = true) =>
        _geometry.SnapWindow(handle, position, monitorIndex, bringToFront);

    public WindowChangeResult MoveWindowToMonitor(
        nint handle, int monitorIndex, MonitorPositioning positioning = MonitorPositioning.Center, bool bringToFront = true) =>
        _geometry.MoveWindowToMonitor(handle, monitorIndex, positioning, bringToFront);

    /// <inheritdoc cref="WindowGeometryService.MonitorIndexMessage"/>
    public static string MonitorIndexMessage(int monitorIndex, int monitorCount) =>
        WindowGeometryService.MonitorIndexMessage(monitorIndex, monitorCount);

    public bool IsValidWindow(nint handle) => _reads.IsValidWindow(handle);

    public WindowChangeResult SetWindowZOrder(nint handle, ZOrderMode mode, nint? above = null) =>
        _zOrder.SetWindowZOrder(handle, mode, above);

    /// <summary>The error of a window handle that is not a window.</summary>
    public const string InvalidHandleMessage = WindowChangeFailures.InvalidHandleMessage;

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
