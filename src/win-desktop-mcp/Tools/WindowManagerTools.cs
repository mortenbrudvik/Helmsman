using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using WinDesktopMcp.Infrastructure;
using WinDesktopMcp.Models;
using WinDesktopMcp.Models.Results;
using WinDesktopMcp.Services;
using WinDesktopMcp.Services.Interfaces;
using static WinDesktopMcp.Models.WindowErrorCode;
using static WinDesktopMcp.Tools.WindowBatchItems;

namespace WinDesktopMcp.Tools;

[McpServerToolType]
public class WindowManagerTools
{
    /// <summary>
    /// The errors of the single-window geometry tools (set_window_bounds, snap_window, move_window_to_monitor). Windows'
    /// refusal of a window running as administrator is AccessDenied for a placement; a state change sees it only as a state
    /// that was not reached, so set_window_state takes <see cref="StateChangeErrors"/> and its own StateChangeFailed.
    /// </summary>
    private const string WindowChangeErrors =
        PolicyErrors + "AccessDenied (Windows blocked the change: the window belongs to an app running as administrator; ask the user); " +
        NotRespondingError;

    /// <summary>The errors of set_window_state, which never returns AccessDenied (see <see cref="WindowChangeErrors"/>).</summary>
    private const string StateChangeErrors = PolicyErrors + NotRespondingError;

    private const string PolicyErrors =
        " errorCode: InvalidHandle; TargetDeniedByPolicy (the user's policy denies the app: nothing is changed, ask the user); ";

    private const string NotRespondingError =
        "NotResponding (the window's app is hung or did not answer within 5 s, or an earlier call to it, or 8 calls to apps that " +
        "do not answer, are still blocked; a minimized window may already have been restored, and a call that timed out can still " +
        "take effect once the app answers, so check the window before acting on it).";

    /// <summary>What the single-window geometry tools return (roadmap M2.3).</summary>
    private const string GeometryResult =
        " Returns success and the window as it is afterwards, also when the change failed: bounds (its visible frame), outerBounds " +
        "(its window rect with the invisible resize borders, about 7 px left, right and bottom at 100%; find_windows reports this " +
        "rect as bounds, and the visible frame as visibleBounds), state (normal, minimized or maximized; a minimized window has no " +
        "bounds) and monitorIndex.";

    /// <summary>How the placements read their coordinates and treat the window's state (roadmap M2.3).</summary>
    private const string PlacementRules =
        " Coordinates are physical virtual-screen pixels (negative left of or above the primary monitor) of the visible frame " +
        "(find_windows' visibleBounds, not its bounds). A minimized or maximized window is restored to normal first, without " +
        "activating it; one its app hid (in the notification area) is not shown, and the change fails.";

    /// <summary>The monitor errors of snap_window and move_window_to_monitor (roadmap M6.7).</summary>
    private const string MonitorErrors =
        " InvalidMonitorIndex, MonitorNotFound, AmbiguousMonitor (two stacked monitors): monitor names no single monitor (nothing changed).";

    /// <summary>How the window tools' argument errors end.</summary>
    private const string NothingChanged = "Nothing was changed.";

    private const string SettleDescription =
        "Wait up to this many milliseconds (clamped to 0-5000, default 0) for the window to stop moving before it is read: until two reads " +
        "20 ms apart agree. Use it for an app that animates or re-applies its own size after the change.";

    private const string BringToFrontDescription =
        "Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use focus_window for that). Default: true";

    /// <summary>The per-window errors of the geometry batches (set_windows_bounds_batch, snap_windows_batch).</summary>
    private const string BatchErrors = BatchItemErrors + "AccessDenied, " + BatchItemResult;

    /// <summary>The per-window errors of set_windows_state_batch, which never returns AccessDenied (see <see cref="WindowChangeErrors"/>).</summary>
    private const string StateBatchErrors = BatchItemErrors + BatchItemResult;

    private const string BatchItemErrors =
        " Each window is handled on its own and has its own success, error and errorCode: InvalidArguments for an item that is " +
        "null or misses a field (an unknown member refuses the whole call), WindowNotFound, AmbiguousWindow (with candidates) or " +
        "HandleMismatch for an item whose window selector names no single window, InvalidHandle, TargetDeniedByPolicy (the user's " +
        "policy denies the app; it is not changed), ";

    private const string BatchItemResult =
        "NotResponding (the window's app does not answer within 5 s). Each item reports its window afterwards, " +
        "failed or not: bounds (visible frame), outerBounds, state and monitorIndex.";

    private readonly IWindowService _windowService;
    private readonly IForegroundService _foregroundService;
    private readonly IInputSession _session;
    private readonly TimeSpan? _gateWaitTimeout;
    private readonly IMonitorService? _monitorService;

    /// <param name="windowService">Reads and changes windows.</param>
    /// <param name="foregroundService">Brings a window to the front (focus_window).</param>
    /// <param name="session">The input session, whose pause the window tools respect (they take the input gate themselves).</param>
    /// <param name="gateWaitTimeout">How long the window tools wait for the input gate; default <see cref="InputGateToolFilter.GateWaitTimeout"/>.</param>
    /// <param name="monitorService">
    /// Resolves a `monitor` name (roadmap M6.7) and the monitor set_window_bounds' percentages are of. The server always
    /// passes it; without it a monitor name or unit 'percent' fails.
    /// </param>
    public WindowManagerTools(
        IWindowService windowService,
        IForegroundService foregroundService,
        IInputSession session,
        TimeSpan? gateWaitTimeout = null,
        IMonitorService? monitorService = null)
    {
        _windowService = windowService;
        _foregroundService = foregroundService;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _gateWaitTimeout = gateWaitTimeout;
        _monitorService = monitorService;
    }

    [McpServerTool(Name = "set_window_bounds", Title = "Set Window Bounds", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move, resize, or move and resize a window in a single operation: x, y, width and height are its visible frame. " +
                 "Pass x and y to move it (it keeps its size), width and height to resize it (its top-left corner stays), or all four." +
                 PlacementRules +
                 " unit 'percent': percent (decimals allowed) of monitor's work area (relativeTo 'monitor': its bounds; default the " +
                 "window's monitor); adjoining values tile exactly." +
                 " A window moved to a monitor with another scale is placed again once the app has rescaled itself." +
                 GeometryResult + WindowChangeErrors +
                 " MoveFailed (ResizeFailed for a size only): Windows did not place it, or it did not return to normal from minimized or maximized (error has the reason). " +
                 "InvalidArguments (nothing changed): half a pair, no pair, a width or height below 1, pixels that are not whole, " +
                 "percentages outside 0-100, monitor or relativeTo without percent." + MonitorErrors)]
    public async Task<WindowOperationResult> SetWindowBounds(
        [Description("X of the visible frame's left edge (with y)")]
        double? x = null,
        [Description("Y of the visible frame's top edge (with x)")]
        double? y = null,
        [Description("Width of the visible frame, at least 1 pixel (with height)")]
        double? width = null,
        [Description("Height of the visible frame, at least 1 pixel (with width)")]
        double? height = null,
        [Description(HandleDescription)]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description("Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use " +
                     "focus_window for that). Default: true with x and y, false for a size only")]
        bool? bringToFront = null,
        [Description(SettleDescription)]
        int settleMs = 0,
        [Description("pixels (default) or percent")]
        BoundsUnit? unit = null,
        [Description("With unit 'percent': workArea (default) or monitor (its full bounds)")]
        RelativeArea? relativeTo = null,
        [Description(MonitorSelectorDescriptions.Monitor + " With unit 'percent'; default: the window's own.")]
        MonitorSelector? monitor = null,
        CancellationToken cancellationToken = default)
    {
        var percent = unit == BoundsUnit.Percent;
        if (BoundsRefusal(x, y, width, height, percent, relativeTo, monitor) is { } invalid)
        {
            return invalid;
        }

        var hwnd = Target(handle, window);
        var choice = percent ? MonitorTargeting.Choose(_monitorService, monitor, allowAll: false, NothingChanged) : default;
        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);
            if (Precheck(hwnd) is { } refused)
                return refused;

            if (!percent)
                return Change(hwnd, BoundsChange(hwnd, Pixels(x), Pixels(y), Pixels(width), Pixels(height), bringToFront), settleMs);

            // A named monitor is found again now, inside the gate: the displays may have changed while the call waited
            var monitorIndex = MonitorTargeting.Current(_monitorService, choice, NothingChanged);

            // Percentages become pixels once, against the target monitor, before the window moves: the second placement after
            // a DPI change places the same pixels again (RelativeRectMath)
            if (PercentBounds(hwnd, monitorIndex, relativeTo ?? RelativeArea.WorkArea, x, y, width, height, out var pixels) is { } notPlaced)
                return notPlaced;

            return Change(hwnd, BoundsChange(hwnd, pixels.X, pixels.Y, pixels.Width, pixels.Height, bringToFront), settleMs);
        }
    }

    private static int? Pixels(double? value) => value is { } v ? (int)v : null;

    /// <summary>
    /// set_window_bounds' percentages in pixels (roadmap M6.7): of the work area or bounds of monitor
    /// <paramref name="monitorIndex"/>, or of the window's own monitor; each value given is converted, as a pair. A monitor
    /// index that is no monitor is InvalidMonitorIndex, a rectangle under one pixel InvalidArguments. Null when converted.
    /// </summary>
    private WindowOperationResult? PercentBounds(nint hwnd, int? monitorIndex, RelativeArea relativeTo, double? x, double? y,
        double? width, double? height, out (int? X, int? Y, int? Width, int? Height) pixels)
    {
        pixels = default;
        var monitors = MonitorService().GetAllMonitors();
        var target = monitorIndex is { } index
            ? index >= 0 && index < monitors.Count ? monitors[index] : null
            : MonitorService().GetMonitorForWindow(hwnd);
        if (target == null)
        {
            return new WindowOperationResult(false, Error: monitorIndex is { } missing
                    ? WindowService.MonitorIndexMessage(missing, monitors.Count)
                    : $"The window's monitor could not be read. {NothingChanged} Pass monitor.",
                ErrorCode: nameof(InvalidMonitorIndex), Handle: hwnd.ToInt64());
        }

        var area = RelativeRectMath.AreaOf(target, relativeTo);
        try
        {
            if (x is { } left && y is { } top && width is { } w && height is { } h)
            {
                var rect = RelativeRectMath.FromPercent(area, left, top, w, h);
                pixels = (rect.X, rect.Y, rect.Width, rect.Height);
            }
            else if (x is { } px && y is { } py)
            {
                var (pointX, pointY) = RelativeRectMath.PointFromPercent(area, px, py);
                pixels = (pointX, pointY, null, null);
            }
            else
            {
                var (sizeWidth, sizeHeight) = RelativeRectMath.SizeFromPercent(area, width!.Value, height!.Value);
                pixels = (null, null, sizeWidth, sizeHeight);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Checked before, so only a size that floors to 0 px on this monitor is left
            return new WindowOperationResult(false,
                Error: $"width and height in percent give less than one pixel on monitor {target.Index}. {NothingChanged}",
                ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode, Handle: hwnd.ToInt64());
        }

        return null;
    }

    private IMonitorService MonitorService() =>
        _monitorService ?? throw new InvalidOperationException("No monitor service is registered.");

    /// <summary>
    /// set_window_bounds' change (roadmap M6.1): both pairs place the visible frame in one SetWindowPos; a position alone is a
    /// move (it keeps the size) and a size alone a resize (it keeps the corner and, unless asked, the z-order), as the
    /// move_window and resize_window that 3.0.0 removed did, so each keeps its own error code and the second placement after a DPI change.
    /// </summary>
    private Func<WindowChangeResult> BoundsChange(nint hwnd, int? x, int? y, int? width, int? height, bool? bringToFront) =>
        (x, y, width, height) switch
        {
            ({ } left, { } top, { } w, { } h) => () => _windowService.SetWindowFrame(hwnd, left, top, w, h, bringToFront ?? true),
            ({ } left, { } top, _, _) => () => _windowService.MoveWindow(hwnd, left, top, bringToFront ?? true),
            (_, _, { } w, { } h) when bringToFront == true => () => _windowService.ResizeWindow(hwnd, w, h, bringToFront: true),
            (_, _, { } w, { } h) => () => _windowService.ResizeWindow(hwnd, w, h),
            _ => throw new InvalidOperationException("BoundsRefusal lets only a whole pair through.")
        };

    [McpServerTool(Name = "set_window_state", Title = "Minimize, Maximize or Restore Window", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Set the window state (minimize, maximize, restore). success is true only when the window was seen in that state " +
                 "afterwards (within 300 ms); state reports the state it is in (minimized, maximized or normal)." + GeometryResult +
                 StateChangeErrors +
                 " StateChangeFailed: the window did not reach the state (the app refused or delayed it, or it runs as administrator). " +
                 "An unknown state fails with InvalidState and the allowed values.")]
    public async Task<WindowOperationResult> SetWindowState(
        [Description("Desired state: 'minimize', 'maximize' or 'restore' ('minimized', 'maximized' and 'normal' are accepted too). " +
                     "Restore returns a minimized window to its previous state (normal, or maximized if it was maximized before) and makes a maximized window normal.")]
        WindowStateRequest state,
        [Description(HandleDescription)]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description(SettleDescription)]
        int settleMs = 0,
        CancellationToken cancellationToken = default)
    {
        var hwnd = Target(handle, window);
        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);
            return Precheck(hwnd) ?? Change(hwnd, () => _windowService.SetWindowState(hwnd, state), settleMs);
        }
    }

    [McpServerTool(Name = "focus_window", Title = "Focus Window", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Bring a window to the foreground and verify it. Works around the Windows foreground lock without sending keys. " +
        "Returns isForeground (also as success), method (already, setForeground, attachThreadInput or inputUnlock), " +
        "the window actually in front (foregroundHandle, foregroundTitle, foregroundProcess) and blockingPopup, a dialog of the " +
        "window that takes keyboard input instead of it. inMenuMode: a menu is still open, so typed letters would pick menu items. " +
        "errorCode: FocusFailed, NotResponding, Cloaked, WindowHidden (the window is hidden, so keys would go to a window the user cannot see), " +
        "TargetDeniedByPolicy (the user's policy denies the app), PausedByUser or InvalidHandle. " +
        "The foreground can change again before your next call, so confirm with get_foreground_window immediately before keyboard or mouse input.")]
    public async Task<FocusResult> FocusWindow(
        [Description(HandleDescription)]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        CancellationToken cancellationToken = default)
    {
        var hwnd = Target(handle, window);
        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);
            return FocusResult.From(_foregroundService.EnsureForeground(hwnd));
        }
    }

    [McpServerTool(Name = "snap_window", Title = "Snap Window", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Snap a window to predefined screen positions (like Windows Snap Assist). Supported positions: left_half, right_half, top_half, bottom_half, top_left_quarter, top_right_quarter, bottom_left_quarter, bottom_right_quarter, left_third, center_third, right_third, left_two_thirds, right_two_thirds, fullscreen; any other position fails with errorCode InvalidSnapPosition. " +
                 "The visible frames of snapped windows tile the work area exactly: no gap from the invisible borders, no pixel lost on odd sizes." +
                 PlacementRules + GeometryResult + WindowChangeErrors +
                 MonitorErrors + " MoveFailed: Windows did not place it, " +
                 "or it did not return to normal from minimized or maximized.")]
    public async Task<WindowOperationResult> SnapWindow(
        [Description("Snap position: left_half, right_half, top_half, bottom_half, top_left_quarter, top_right_quarter, bottom_left_quarter, bottom_right_quarter, left_third, center_third, right_third, left_two_thirds, right_two_thirds, fullscreen")]
        SnapPosition position,
        [Description(HandleDescription)]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description(MonitorSelectorDescriptions.Monitor + " Default: the window's own.")]
        MonitorSelector? monitor = null,
        [Description(BringToFrontDescription)]
        bool bringToFront = true,
        [Description(SettleDescription)]
        int settleMs = 0,
        CancellationToken cancellationToken = default)
    {
        var hwnd = Target(handle, window);
        var choice = MonitorTargeting.Choose(_monitorService, monitor, allowAll: false, NothingChanged);
        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);
            if (Precheck(hwnd) is { } refused)
                return refused;

            // A named monitor is found again now, inside the gate: the displays may have changed while the call waited
            var index = MonitorTargeting.Current(_monitorService, choice, NothingChanged);
            return Change(hwnd, () => _windowService.SnapWindow(hwnd, position, index, bringToFront), settleMs);
        }
    }

    [McpServerTool(Name = "move_window_to_monitor", Title = "Move Window to Monitor", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move a window to a specific monitor, optionally positioning it. The window keeps its normal size, made smaller when " +
                 "needed so that it fits the monitor's work area. To a monitor with other scaling it is moved first, and sized after " +
                 "the app has rescaled itself. success requires the window to be on that monitor afterwards." + PlacementRules +
                 GeometryResult + WindowChangeErrors + MonitorErrors +
                 " MoveFailed: Windows did not place it, " +
                 "it did not return to normal from minimized or maximized, or it is not on that monitor afterwards (with 'maximize': not " +
                 "maximized there). An unknown positioning fails with InvalidPositioning.")]
    public async Task<WindowOperationResult> MoveWindowToMonitor(
        [Description(MonitorSelectorDescriptions.Monitor)]
        MonitorSelector monitor,
        [Description(HandleDescription)]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description("Position on the monitor's work area: 'center' (default), 'topleft', 'topright', 'bottomleft', 'bottomright' (each at the window's normal size, fitted to the work area), 'maximize' (moved there, then maximized) or 'restore' (normal size, at the work area's top-left corner)")]
        MonitorPositioning positioning = MonitorPositioning.Center,
        [Description(BringToFrontDescription + ". Positioning 'maximize' ignores it and activates the window: Windows has no maximize that does not activate.")]
        bool bringToFront = true,
        [Description(SettleDescription)]
        int settleMs = 0,
        CancellationToken cancellationToken = default)
    {
        if (monitor == null)
        {
            // A JSON null binds; it is the caller's argument error, not the server's
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"Pass monitor, the monitor to move the window to (an index or a name). {NothingChanged}");
        }

        var hwnd = Target(handle, window);
        var choice = MonitorTargeting.Choose(_monitorService, monitor, allowAll: false, NothingChanged);

        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);
            if (Precheck(hwnd) is { } refused)
                return refused;

            // A named monitor is found again now, inside the gate: the displays may have changed while the call waited
            var index = MonitorTargeting.Current(_monitorService, choice, NothingChanged)!.Value;
            return Change(hwnd, () => _windowService.MoveWindowToMonitor(hwnd, index, positioning, bringToFront), settleMs);
        }
    }

    [McpServerTool(Name = "set_window_zorder", Title = "Set Window Z-Order", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Change a window's place in the z-order without moving or activating it (focus stays). mode: 'top' (above the " +
                 "other normal windows), 'bottom' (behind all), 'topmost' (pinned above all normal windows, also while another is " +
                 "active), 'notopmost' (unpinned), 'above' (directly above aboveHandle; topmost if that window is). get_server_info " +
                 "lists the windows this server pinned (pinnedWindows); unpin them when done. Returns isTopmost and zOrder (0 is the " +
                 "front) read back. errorCode: InvalidHandle, TargetDeniedByPolicy, AccessDenied (an app running as administrator), " +
                 "NotResponding, ZOrderNotApplied (the app changed it back).")]
    public async Task<ZOrderResult> SetWindowZOrder(
        [Description("Where to put the window")]
        ZOrderMode mode,
        [Description(HandleDescription)]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description("With mode 'above': the window to place it above (a handle from find_windows)")]
        long? aboveHandle = null,
        CancellationToken cancellationToken = default)
    {
        if (mode == ZOrderMode.Above && aboveHandle == null || mode != ZOrderMode.Above && aboveHandle != null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, mode == ZOrderMode.Above
                ? $"mode 'above' needs aboveHandle, the window to place it above. {NothingChanged}"
                : $"aboveHandle goes only with mode 'above'. {NothingChanged}");
        }

        var hwnd = Target(handle, window);
        if (aboveHandle == hwnd.ToInt64())
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"aboveHandle is the window itself. {NothingChanged} Pass another window to place it above.");
        }

        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);
            if (Precheck(hwnd) is { } refused)
                return new ZOrderResult(false, hwnd.ToInt64(), Error: refused.Error, ErrorCode: refused.ErrorCode);

            WindowChangeResult result;
            try
            {
                result = _windowService.SetWindowZOrder(hwnd, mode, aboveHandle is { } above ? (nint)above : null);
            }
            catch (WindowNotRespondingException ex)
            {
                result = WindowChangeResult.Failed(hwnd, NotResponding, ex.Message);
            }

            var details = result.ErrorCode == InvalidHandle ? null : _windowService.GetWindowDetails(hwnd);
            return new ZOrderResult(result.Success, hwnd.ToInt64(), details?.IsTopmost, details?.ZOrder, result.Error, result.ErrorCodeName);
        }
    }

    [McpServerTool(Name = "set_windows_bounds_batch", Title = "Set Window Bounds (Batch)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move and resize multiple windows in a single batch operation: each item's x, y, width and height, all four in " +
                 "pixels, are its window's visible frame (no unit, relativeTo or monitor: use set_window_bounds for percent). More " +
                 "efficient than multiple individual calls." + PlacementRules + BatchErrors +
                 " MoveFailed: Windows did not place the window, or it did not return to normal from minimized or maximized.")]
    public async Task<BatchWindowResult> SetWindowsBoundsBatch(
        [Description("Array of window placements, each with x, y, width, height (the visible frame), the window as handle or window " +
                     "(a selector, as the single-window tools take it), and optional bringToFront (raises the window without activating it)")]
        WindowBoundsPlacement[] placements,
        [Description("Delay between operations in milliseconds (clamped to 0-5000, default: 50)")]
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default)
    {
        RequireItems(placements, nameof(placements));
        InputGateToolFilter.ThrowIfPaused(_session);

        // Each item's window, resolved before the gate (roadmap M2.4); an item that names no single window, or misses a
        // coordinate or size (which would otherwise bind 0 and move its window to the origin), fails alone
        var items = ResolveItems(_windowService, placements, nameof(placements), "x, y, width, height and a handle or window", p => p.Handle, p => p.Window);
        var invalid = items.Select((t, i) => t.Refusal ?? ItemBoundsRefusal(i, t.Handle.ToInt64(), placements[i])).ToList();

        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            // Items for windows the policy denies get their result here; the others go to the service
            var refusals = invalid.Select((r, i) => r ?? ItemRecheck(_windowService, i, items[i].Handle, placements[i].Window) ??
                BatchPolicyRefusal(items[i].Handle.ToInt64())).ToList();
            var servicePlacements = new List<(nint, int, int, int, int, bool)>();
            for (var i = 0; i < placements.Length; i++)
            {
                if (refusals[i] == null)
                {
                    // Every item that is not refused has all four (ItemBoundsRefusal)
                    var p = placements[i];
                    servicePlacements.Add((items[i].Handle, p.X!.Value, p.Y!.Value, p.Width!.Value, p.Height!.Value, p.BringToFront));
                }
            }

            var results = await _windowService.SetWindowBoundsBatchAsync(servicePlacements, delayBetweenMs, cancellationToken).ConfigureAwait(false);

            return Summary(placements.Length, InOrder(refusals, results.Select(r => Item(r))));
        }
    }

    [McpServerTool(Name = "snap_windows_batch", Title = "Snap Windows (Batch)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Snap multiple windows to predefined positions in a single batch operation. More efficient than multiple individual calls." + BatchErrors +
                 " InvalidSnapPosition: the item's position is missing or unknown (error lists the positions). InvalidMonitorIndex, " +
                 "MonitorNotFound, AmbiguousMonitor: its monitor names no single monitor. " +
                 "MoveFailed: Windows did not place the window, or it did not return to normal from minimized or maximized.")]
    public async Task<BatchWindowResult> SnapWindowsBatch(
        [Description("Array of snap placements, each with position, the window as handle or window (a selector, as the single-window " +
                     "tools take it), optional monitor, and optional bringToFront (raises the window without activating it; a " +
                     "minimized or maximized window is restored first, without activating it)")]
        WindowSnapPlacement[] placements,
        [Description("Delay between operations in milliseconds (clamped to 0-5000, default: 50)")]
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default)
    {
        RequireItems(placements, nameof(placements));
        InputGateToolFilter.ThrowIfPaused(_session);

        // Each item's window, resolved before the gate, and its position, checked here, so one bad item fails alone
        var items = ResolveItems(_windowService, placements, nameof(placements), "a position and a handle or window", p => p.Handle, p => p.Window);
        var positions = new SnapPosition[placements.Length];
        var monitors = new MonitorChoice[placements.Length];
        var invalid = items.Select((t, i) =>
        {
            if (t.Refusal != null)
                return t.Refusal;

            if (!ToolEnum<SnapPosition>.TryParse(placements[i].Position, out positions[i], out var error))
                return new WindowResultItem(t.Handle.ToInt64(), false, Error: $"Item {i}: {error}", ErrorCode: nameof(InvalidSnapPosition));

            // The item's monitor, resolved before the gate (roadmap M6.7); one that names no single monitor fails the item alone
            return MonitorTargeting.TryChoose(_monitorService, placements[i].Monitor, allowAll: false,
                $"{nameof(placements)}[{i}].", out monitors[i], out var monitorCode, out var monitorError)
                ? null
                : new WindowResultItem(t.Handle.ToInt64(), false, Error: $"Item {i}: {monitorError}", ErrorCode: monitorCode);
        }).ToList();

        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            // Each item's named monitor is found again now, inside the gate: the displays may have changed while the call waited
            var monitorIndexes = new int?[placements.Length];
            var refusals = invalid.Select((r, i) => r ?? ItemRecheck(_windowService, i, items[i].Handle, placements[i].Window) ??
                BatchPolicyRefusal(items[i].Handle.ToInt64()) ??
                (MonitorTargeting.TryCurrent(_monitorService, monitors[i], $"{nameof(placements)}[{i}].", out monitorIndexes[i], out var gone)
                    ? null
                    : new WindowResultItem(items[i].Handle.ToInt64(), false, Error: $"Item {i}: {gone}", ErrorCode: nameof(MonitorNotFound))))
                .ToList();
            var servicePlacements = new List<(nint, SnapPosition, int?, bool)>();
            for (var i = 0; i < placements.Length; i++)
            {
                if (refusals[i] == null)
                {
                    servicePlacements.Add((items[i].Handle, positions[i], monitorIndexes[i], placements[i].BringToFront));
                }
            }

            var results = await _windowService.SnapWindowsBatchAsync(servicePlacements, delayBetweenMs, cancellationToken).ConfigureAwait(false);

            return Summary(placements.Length, InOrder(refusals, results.Select(r => Item(r))));
        }
    }

    [McpServerTool(Name = "set_windows_state_batch", Title = "Set Window States (Batch)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Set the state (minimize, maximize, restore) of multiple windows in a single batch operation. Each item's success is true only " +
                 "when its window was seen in that state afterwards, and state reports the state it is in." + StateBatchErrors +
                 " InvalidState: the item's state is missing or unknown (error lists the states). StateChangeFailed: the window did not reach the state.")]
    public async Task<BatchWindowResult> SetWindowsStateBatch(
        [Description("Array of state changes, each with state ('minimize', 'maximize' or 'restore'; 'minimized', 'maximized' and 'normal' " +
                     "are accepted too) and the window as handle or window (a selector, as the single-window tools take it)")]
        WindowStateChange[] changes,
        [Description("Delay between operations in milliseconds (clamped to 0-5000, default: 30)")]
        int delayBetweenMs = 30,
        CancellationToken cancellationToken = default)
    {
        RequireItems(changes, nameof(changes));
        InputGateToolFilter.ThrowIfPaused(_session);

        var items = ResolveItems(_windowService, changes, nameof(changes), "a state and a handle or window", c => c.Handle, c => c.Window);
        var states = new WindowStateRequest[changes.Length];
        var invalid = items.Select((t, i) =>
        {
            if (t.Refusal != null)
                return t.Refusal;

            return ToolEnum<WindowStateRequest>.TryParse(changes[i].State, out states[i], out var error)
                ? null
                : new WindowResultItem(t.Handle.ToInt64(), false, Error: $"Item {i}: {error}", ErrorCode: nameof(InvalidState));
        }).ToList();

        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            var refusals = invalid.Select((r, i) => r ?? ItemRecheck(_windowService, i, items[i].Handle, changes[i].Window) ??
                BatchPolicyRefusal(items[i].Handle.ToInt64())).ToList();
            var serviceChanges = new List<(nint, WindowStateRequest)>();
            for (var i = 0; i < changes.Length; i++)
            {
                if (refusals[i] == null)
                {
                    serviceChanges.Add((items[i].Handle, states[i]));
                }
            }

            var results = await _windowService.SetWindowStateBatchAsync(serviceChanges, delayBetweenMs, cancellationToken).ConfigureAwait(false);

            return Summary(changes.Length, InOrder(refusals, results.Select(r => Item(r))));
        }
    }

    /// <summary>
    /// The refusal of a change before anything is sent: InvalidHandle, or TargetDeniedByPolicy for a window the user's
    /// target policy denies (these tools never restore, raise or move such a window). Null when the change can go ahead.
    /// </summary>
    private WindowOperationResult? Precheck(nint handle)
    {
        if (!_windowService.IsValidWindow(handle))
        {
            return new WindowOperationResult(false, Error: WindowService.InvalidHandleMessage, ErrorCode: nameof(InvalidHandle),
                Handle: handle.ToInt64());
        }

        return _windowService.EvaluateTargetPolicy(handle) is { Allowed: false } verdict
            ? new WindowOperationResult(false, Error: WindowService.WindowChangeRefusal(handle, verdict), ErrorCode: nameof(TargetDeniedByPolicy),
                Handle: handle.ToInt64())
            : null;
    }

    /// <summary>The handle parameter of the single-window tools: kept for compatibility beside the window selector (roadmap M2.4).</summary>
    internal const string HandleDescription = "Window handle (as integer) from find_windows. Pass handle or window, not both.";

    /// <summary>
    /// The window a window-changing tool acts on (roadmap M2.4). Nothing while the user has paused input; then the handle,
    /// or the selector resolved, before the tool takes the input gate: resolution only reads windows, and the window then
    /// goes through the same checks (<see cref="Precheck"/>, the service's own) as a handle the caller passed.
    /// </summary>
    /// <exception cref="ToolErrorException">Input is paused, or the arguments name no single window.</exception>
    private nint Target(long? handle, WindowSelector? window)
    {
        InputGateToolFilter.ThrowIfPaused(_session);
        return WindowTargeting.Require(_windowService, handle, window);
    }

    /// <summary>
    /// The input gate for a window-changing tool, after its window was resolved: waits, refuses (InputBusy) and reports a
    /// pause (PausedByUser) exactly as the gate filter does for the tools it gates.
    /// </summary>
    private Task<IDisposable> EnterGateAsync(CancellationToken cancellationToken) =>
        InputGateToolFilter.EnterAsync(_session, cancellationToken, _gateWaitTimeout);

    /// <summary>
    /// Runs a window change and reports its outcome with the window as it is afterwards (roadmap M2.3): its visible frame,
    /// window rect, state and monitor, read after waiting up to <paramref name="settleMs"/> for it to stop moving. A window
    /// that did not answer in time (<see cref="WindowNotRespondingException"/>, from the service's <see cref="IWindowCallRunner"/>)
    /// is NotResponding: the application is hung or busy, and the tool returns instead of holding the input gate until it
    /// answers. The reads afterwards never wait for the window's thread.
    /// </summary>
    private WindowOperationResult Change(nint handle, Func<WindowChangeResult> change, int settleMs)
    {
        WindowChangeResult result;
        try
        {
            result = change();
        }
        catch (WindowNotRespondingException ex)
        {
            result = WindowChangeResult.Failed(handle, NotResponding, ex.Message);
        }

        if (result.Success)
        {
            _windowService.WaitForSettledFrame(handle, WindowService.SettleDelay(settleMs));
        }

        var window = result.ErrorCode == InvalidHandle ? null : _windowService.GetWindowInfo(handle);
        return new WindowOperationResult(
            Success: result.Success,
            Bounds: Bounds(window),
            OuterBounds: OuterBounds(window),
            State: State(window),
            MonitorIndex: window?.MonitorIndex,
            Error: result.Error,
            ErrorCode: result.ErrorCodeName,
            Handle: handle.ToInt64());
    }

    /// <summary>A batch item for the service's result, with the window as it is afterwards.</summary>
    private WindowResultItem Item(WindowChangeResult result)
    {
        // An invalid handle has nothing to read; any other window is read, failed or not, so the agent sees where it is
        var window = result.ErrorCode == InvalidHandle ? null : _windowService.GetWindowInfo(result.Handle);
        return new WindowResultItem(
            Handle: result.Handle.ToInt64(),
            Success: result.Success,
            Bounds: Bounds(window),
            OuterBounds: OuterBounds(window),
            State: State(window),
            MonitorIndex: window?.MonitorIndex,
            Error: result.Error,
            ErrorCode: result.ErrorCodeName);
    }

    /// <summary>The visible frame (the window rect when DWM cannot report it); none for a minimized window, which is off screen.</summary>
    private static BoundsDto? Bounds(WindowInfo? window) =>
        window is null || window.State == WindowState.Minimized ? null : Dto(window.VisibleBounds ?? window.Bounds);

    /// <summary>The window rect (GetWindowRect, as find_windows reports it); none for a minimized window.</summary>
    private static BoundsDto? OuterBounds(WindowInfo? window) =>
        window is null || window.State == WindowState.Minimized ? null : Dto(window.Bounds);

    private static BoundsDto Dto(WindowRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static string? State(WindowInfo? window) => window?.State.ToString().ToLowerInvariant();

    /// <summary>InvalidArguments when a size is not positive (roadmap M2.2): nothing is sent.</summary>
    private static WindowOperationResult? SizeRefusal(int width, int height) =>
        width >= 1 && height >= 1
            ? null
            : new WindowOperationResult(false, Error: SizeMessage(width, height), ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);

    private static WindowResultItem? ItemSizeRefusal(long handle, int width, int height) =>
        width >= 1 && height >= 1
            ? null
            : new WindowResultItem(handle, false, Error: SizeMessage(width, height), ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);

    /// <summary>
    /// InvalidArguments for a set_windows_bounds_batch item without x, y, width or height (missing or null: bound, it would
    /// be 0, and the window would go to the origin), or with a size below 1; null when it has all four. Nothing is sent for it.
    /// </summary>
    private static WindowResultItem? ItemBoundsRefusal(int index, long handle, WindowBoundsPlacement placement)
    {
        var missing = new[] { ("x", placement.X), ("y", placement.Y), ("width", placement.Width), ("height", placement.Height) }
            .Where(field => field.Item2 is null)
            .Select(field => field.Item1)
            .ToList();
        if (missing.Count > 0)
        {
            return new WindowResultItem(handle, false,
                Error: $"Item {index} of placements has no {string.Join(" or ", missing)}: each item needs x, y, width and height, in " +
                       "pixels. Nothing was done for it.",
                ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);
        }

        return ItemSizeRefusal(handle, placement.Width!.Value, placement.Height!.Value);
    }

    /// <summary>
    /// InvalidArguments for set_window_bounds' optional pairs (roadmap M6.1): x goes with y and width with height, at least one
    /// pair is given, and a size is positive; and for its unit (roadmap M6.7): pixels are whole numbers in the int range, with
    /// no relativeTo or monitor, and percentages are 0-100 (sizes above 0) and end inside the area. Nothing is sent.
    /// </summary>
    private static WindowOperationResult? BoundsRefusal(double? x, double? y, double? width, double? height, bool percent,
        RelativeArea? relativeTo, MonitorSelector? monitor)
    {
        string? message = null;
        if (x.HasValue != y.HasValue)
        {
            message = $"x and y go together, but only {(x.HasValue ? "x" : "y")} was given. Pass both to move the window, or neither to keep its position. Nothing was changed.";
        }
        else if (width.HasValue != height.HasValue)
        {
            message = $"width and height go together, but only {(width.HasValue ? "width" : "height")} was given. Pass both to resize the window, or neither to keep its size. Nothing was changed.";
        }
        else if (!x.HasValue && !width.HasValue)
        {
            message = "Pass x and y to move the window, width and height to resize it, or all four. Nothing was changed.";
        }
        else if (!percent && (relativeTo != null || monitor != null))
        {
            message = $"{(monitor != null ? "monitor" : "relativeTo")} goes with unit 'percent'; pixels are virtual-screen coordinates, " +
                      $"which already name the monitor. {NothingChanged}";
        }
        else if (percent)
        {
            message = (x, y, width, height) switch
            {
                ({ } left, { } top, { } w, { } h) => RelativeRectMath.CheckPercent(left, top, w, h),
                ({ } left, { } top, _, _) => RelativeRectMath.CheckPercentPosition(left, top),
                (_, _, { } w, { } h) => RelativeRectMath.CheckPercentSize(w, h),
                _ => null
            };
            message = message == null ? null : $"{message} {NothingChanged}";
        }
        else if (new[] { (x, "x"), (y, "y"), (width, "width"), (height, "height") }
                     .FirstOrDefault(v => v.Item1 is { } value && (value != Math.Floor(value) || value is < int.MinValue or > int.MaxValue))
                     is ({ } fraction, { } name))
        {
            message = string.Create(CultureInfo.InvariantCulture,
                $"{name} must be a whole number of pixels, not {fraction}; pass unit 'percent' for a share of a monitor. {NothingChanged}");
        }

        if (message != null)
        {
            return new WindowOperationResult(false, Error: message, ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);
        }

        return !percent && width is { } pw && height is { } ph ? SizeRefusal((int)pw, (int)ph) : null;
    }

    private static string SizeMessage(int width, int height) =>
        $"width and height must be at least 1 pixel, but they are {width} x {height}. Nothing was changed.";

    /// <summary>A batch item for a window the target policy denies, or null when the window is allowed or not a window.</summary>
    private WindowResultItem? BatchPolicyRefusal(long handle) =>
        _windowService.IsValidWindow((nint)handle) && Precheck((nint)handle) is { } denied
            ? new WindowResultItem(Handle: handle, Success: false, Error: denied.Error, ErrorCode: denied.ErrorCode)
            : null;
}

/// <summary>
/// set_window_zorder's result (roadmap M6.7): whether the change was made, and the window's topmost state and place in the
/// z-order (0 is the front; get_window_info's zOrder) read back afterwards, failed or not (left out when the window is gone).
/// </summary>
public sealed record ZOrderResult(
    bool Success,
    long? Handle = null,
    bool? IsTopmost = null,
    int? ZOrder = null,
    string? Error = null,
    string? ErrorCode = null);

/// <summary>
/// The result of a single-window tool. The geometry tools (roadmap M2.3) report the window as it is afterwards, failed or
/// not (left out for an invalid handle): Bounds is its visible frame, OuterBounds its window rect with the invisible
/// resize borders (the bounds find_windows reports), State normal, minimized or maximized, MonitorIndex its monitor. A
/// minimized window has no bounds. Handle is the window the tool acted on (roadmap M2.4: the one a window selector
/// resolved to); it is left out when the arguments named no window.
/// </summary>
public record WindowOperationResult(
    bool Success,
    BoundsDto? Bounds = null,
    BoundsDto? OuterBounds = null,
    string? State = null,
    int? MonitorIndex = null,
    string? Error = null,
    string? ErrorCode = null,
    long? Handle = null
);
