using Helmsman.Models;
using Helmsman.Services.Interfaces;

namespace Helmsman.Services;

/// <summary>
/// The tools' one entry to the window services (<see cref="IWindowService"/>): it only hands each call to the service that
/// does it. <see cref="IWindowQuery"/> lists, reads and resolves windows and gives the target policy's verdict,
/// <see cref="IWindowGeometry"/> moves, resizes, snaps and changes their state, <see cref="IWindowZOrder"/> changes their
/// place in the z-order, <see cref="IWindowClosing"/> closes them and reads what a close leaves behind, and
/// <see cref="IWindowBatch"/> runs the batches. A consumer that needs only one of them takes that one.
/// </summary>
public class WindowService : IWindowService
{
    /// <inheritdoc cref="WindowBatchService.MaxDelayBetweenMs"/>
    public const int MaxDelayBetweenMs = WindowBatchService.MaxDelayBetweenMs;

    /// <summary>
    /// How long a state change (minimize, maximize, restore) waits for the window to be seen in the new state before it
    /// reports StateChangeFailed: applications that animate the change take a moment.
    /// </summary>
    public static readonly TimeSpan StateChangeTimeout = WindowGeometryService.StateChangeTimeout;

    private readonly IWindowQuery _query;
    private readonly IWindowGeometry _geometry;
    private readonly IWindowZOrder _zOrder;
    private readonly IWindowClosing _closing;
    private readonly IWindowBatch _batch;

    /// <param name="query">The listings, the window reads, selector resolution and the target policy's verdict.</param>
    /// <param name="geometry">The window placements and state changes.</param>
    /// <param name="zOrder">set_window_zorder.</param>
    /// <param name="closing">close_window's close and the reads of what a close leaves behind.</param>
    /// <param name="batch">The batches of placements, state changes and closes.</param>
    public WindowService(
        IWindowQuery query,
        IWindowGeometry geometry,
        IWindowZOrder zOrder,
        IWindowClosing closing,
        IWindowBatch batch)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _zOrder = zOrder ?? throw new ArgumentNullException(nameof(zOrder));
        _closing = closing ?? throw new ArgumentNullException(nameof(closing));
        _batch = batch ?? throw new ArgumentNullException(nameof(batch));
    }

    /// <inheritdoc cref="WindowBatchService.BatchDelay"/>
    public static TimeSpan BatchDelay(int delayBetweenMs) => WindowBatchService.BatchDelay(delayBetweenMs);

    public IReadOnlyList<WindowInfo> GetAllWindows(bool includeMinimized = true) => _query.GetAllWindows(includeMinimized);

    public IReadOnlyList<WindowInfo> FindWindows(
        string? titleContains = null,
        string? processName = null,
        int? processId = null,
        nint? handle = null,
        bool visibleOnly = true) =>
        _query.FindWindows(titleContains, processName, processId, handle, visibleOnly);

    public IReadOnlyList<WindowInfo> FindWindows(WindowQuery query) => _query.FindWindows(query);

    public WindowResolution ResolveWindow(WindowSelector selector) => _query.ResolveWindow(selector);

    /// <inheritdoc cref="WindowQueryService.RegexTimeoutMessage"/>
    internal static string RegexTimeoutMessage(string? pattern) => WindowQueryService.RegexTimeoutMessage(pattern);

    public WindowDetails? GetWindowDetails(nint handle) => _query.GetWindowDetails(handle);

    public WindowInfo? GetForegroundWindow() => _query.GetForegroundWindow();

    public WindowInfo? GetWindowInfo(nint handle) => _query.GetWindowInfo(handle);

    public bool IsUserFacingAppWindow(nint handle) => _query.IsUserFacingAppWindow(handle);

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

    public bool IsValidWindow(nint handle) => _query.IsValidWindow(handle);

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

    public TargetPolicyVerdict EvaluateTargetPolicy(nint handle) => _query.EvaluateTargetPolicy(handle);

    public Task<IReadOnlyList<WindowChangeResult>> SetWindowBoundsBatchAsync(
        IReadOnlyList<(nint Handle, int X, int Y, int Width, int Height, bool BringToFront)> placements,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default) =>
        _batch.SetWindowBoundsBatchAsync(placements, delayBetweenMs, cancellationToken);

    public Task<IReadOnlyList<WindowChangeResult>> SnapWindowsBatchAsync(
        IReadOnlyList<(nint Handle, SnapPosition Position, int? MonitorIndex, bool BringToFront)> placements,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default) =>
        _batch.SnapWindowsBatchAsync(placements, delayBetweenMs, cancellationToken);

    public Task<IReadOnlyList<WindowChangeResult>> SetWindowStateBatchAsync(
        IReadOnlyList<(nint Handle, WindowStateRequest State)> changes,
        int delayBetweenMs = 30,
        CancellationToken cancellationToken = default) =>
        _batch.SetWindowStateBatchAsync(changes, delayBetweenMs, cancellationToken);

    public Task<IReadOnlyList<WindowCloseResult>> CloseWindowsBatchAsync(
        IReadOnlyList<nint> handles,
        bool force,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default) =>
        _batch.CloseWindowsBatchAsync(handles, force, delayBetweenMs, cancellationToken);
}
