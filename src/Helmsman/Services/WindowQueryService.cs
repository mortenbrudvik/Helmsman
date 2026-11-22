using System.Text.RegularExpressions;
using Helmsman.Models;
using Helmsman.Native;
using Helmsman.Services.Interfaces;
using static Helmsman.Services.WindowChangeFailures;
using static Helmsman.Services.WindowReads;

namespace Helmsman.Services;

/// <summary>
/// The window reads (<see cref="IWindowQuery"/>): the listings and their user-facing filter, a window selector resolved to
/// exactly one window, one window read by handle or in full, the foreground window, and the target policy's verdict on a
/// window. A UWP frame is reported under the app it hosts. Only reads; never sends a message to a window.
/// </summary>
public sealed class WindowQueryService : IWindowQuery
{
    private readonly IMonitorService _monitorService;
    private readonly INativeWindowWrapper _nativeWrapper;
    private readonly WindowReads _reads;
    private readonly WindowInfoReader _windowInfos;
    private readonly WindowTargetCheck _targetCheck;

    /// <param name="monitorService">Monitor lookups.</param>
    /// <param name="nativeWrapper">The window calls.</param>
    /// <param name="hostGuard">Recognizes the agent's host windows (WindowInfo.IsAgentHost).</param>
    /// <param name="targetPolicy">
    /// The server's target policy (roadmap M1.12), whose verdict on a window <see cref="EvaluateTargetPolicy"/> reports.
    /// </param>
    public WindowQueryService(
        IMonitorService monitorService,
        INativeWindowWrapper nativeWrapper,
        IHostGuard hostGuard,
        ITargetPolicy targetPolicy)
    {
        _monitorService = monitorService;
        _nativeWrapper = nativeWrapper;
        _reads = new WindowReads(nativeWrapper);
        _windowInfos = new WindowInfoReader(nativeWrapper, monitorService, hostGuard ?? throw new ArgumentNullException(nameof(hostGuard)), _reads);
        _targetCheck = new WindowTargetCheck(nativeWrapper, _reads, targetPolicy ?? throw new ArgumentNullException(nameof(targetPolicy)));
    }

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

        // A path before a blank name: 'C:\Windows\' is a path, not a blank filter
        if (ProcessNames.PathRefusal("window.processName", selector.ProcessName) is { } path)
        {
            return $"{path} Nothing was done.";
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
        if (selector.ProcessName is { Length: > 0 } processName && ProcessNames.Normalize(processName).Length == 0)
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
    /// (<see cref="IWindowService.GetNormalBounds"/>), whether it comes back maximized, and the monitor of that rectangle. Null when the
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

    public bool IsValidWindow(nint handle) => _reads.IsValidWindow(handle);

    public TargetPolicyVerdict EvaluateTargetPolicy(nint handle) => _targetCheck.EvaluateTargetPolicy(handle);

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
