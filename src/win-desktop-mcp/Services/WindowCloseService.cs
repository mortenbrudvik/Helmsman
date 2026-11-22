using System.Globalization;
using Microsoft.Extensions.Logging;
using WinDesktopMcp.Models;
using WinDesktopMcp.Services.Interfaces;
using static WinDesktopMcp.Models.WindowErrorCode;
using static WinDesktopMcp.Services.Interfaces.IWindowCloseService;

namespace WinDesktopMcp.Services;

/// <summary>
/// The close tools' work below the tool layer (<see cref="IWindowCloseService"/>): it reads and closes windows through the
/// window service, whose close guards (the desktop shell, the agent's host, the target policy) refuse what must not close,
/// and waits for them through the close verifier.
/// </summary>
public sealed class WindowCloseService : IWindowCloseService
{
    /// <summary>How long close_application waits at most for its processes to exit once their windows are gone.</summary>
    private static readonly TimeSpan MaxExitWait = TimeSpan.FromSeconds(2);

    private readonly IWindowService _windowService;
    private readonly IWindowCloseVerifier _closeVerifier;
    private readonly ILogger<WindowCloseService>? _logger;

    /// <param name="windowService">Reads and closes windows, behind its close guards.</param>
    /// <param name="closeVerifier">Waits for closed windows and finds their prompts (roadmap M2.5).</param>
    /// <param name="logger">Logs what goes wrong unexpectedly, such as a close_application window whose close throws.</param>
    public WindowCloseService(IWindowService windowService, IWindowCloseVerifier closeVerifier, ILogger<WindowCloseService>? logger = null)
    {
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
        _closeVerifier = closeVerifier ?? throw new ArgumentNullException(nameof(closeVerifier));
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlySet<nint> ShownBefore(IReadOnlyList<nint> handles, int waitMs) =>
        waitMs == 0 ? EmptyHandles : _closeVerifier.ShownWindows(handles);

    // ---- close_window ----

    /// <inheritdoc />
    public CloseWindowResult? CheckWindow(nint hwnd)
    {
        var reported = hwnd.ToInt64();
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new CloseWindowResult(false, Error: WindowService.InvalidHandleMessage, ErrorCode: nameof(InvalidHandle), Handle: reported);
        }

        return null;
    }

    /// <inheritdoc />
    public WindowCloseResult Close(nint hwnd, bool force) => _windowService.CloseWindow(hwnd, force);

    /// <inheritdoc />
    public ForceCloseCheck? CheckForce(nint hwnd, WindowCloseResult sent, bool checkBeforeForce, bool canAsk)
    {
        var reported = hwnd.ToInt64();
        if (checkBeforeForce && !sent.Success && sent.ErrorCode == ProtectedWindow)
        {
            var why = ProtectionReason(sent.Error);
            if (!canAsk)
            {
                return new ForceCloseCheck(new CloseWindowResult(false,
                    Error: $"{why} force closes it only with confirm=true too when the client cannot ask the user. Nothing was " +
                           "closed. Call again with force=true and confirm=true only if the user explicitly asked to close this window.",
                    ErrorCode: nameof(ConfirmationRequired), Handle: reported), why, null);
            }

            var asked = _windowService.GetWindowInfo(hwnd);
            return new ForceCloseCheck(null, why, asked);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<CloseWindowResult> FinishCloseAsync(nint hwnd, WindowCloseResult sent, IReadOnlySet<nint> shownBefore, int waitMs,
        CancellationToken cancellationToken)
    {
        var reported = hwnd.ToInt64();
        if (!sent.Success)
        {
            return new CloseWindowResult(false, Error: sent.Error, ErrorCode: sent.ErrorCode.ToString(), Handle: reported);
        }

        if (waitMs == 0)
        {
            return new CloseWindowResult(true, CloseNames.Of(CloseStatus.Requested), Handle: reported);
        }

        var observed = await _closeVerifier.WaitForCloseAsync([hwnd], shownBefore, TimeSpan.FromMilliseconds(waitMs), cancellationToken)
            .ConfigureAwait(false);
        var outcome = Outcome(observed[0], waitMs);
        return new CloseWindowResult(outcome.Success, outcome.Status, outcome.Prompt, outcome.Error, outcome.ErrorCode, reported);
    }

    // ---- close_windows_batch ----

    /// <inheritdoc />
    public BatchCloseCheck CheckBatch(int requested, IReadOnlyList<(nint Handle, WindowResultItem? Refusal)> items, bool force, bool confirm,
        bool canAsk)
    {
        // force overrides the protection of the desktop shell and the agent's own host only with the user's yes when the
        // client can ask, and only with confirm=true when it cannot (roadmap M2.F1), whatever the number of windows: a
        // window protected now needs that confirmation, and force is passed on only when the user was asked (or, when they
        // cannot be, confirm is true)
        var protectedNow = force && (canAsk || !confirm)
            ? items.Where(t => t.Refusal == null && _windowService.CheckClose(t.Handle, force: false) is { ErrorCode: ProtectedWindow })
                .Select(t => t.Handle).ToHashSet()
            : [];
        var forceOverrides = protectedNow.Count > 0;
        if (requested > MaxCloseBatchWithoutConfirm || forceOverrides)
        {
            if (canAsk)
            {
                // The user is asked about each window the batch would close, as it is now
                var targets = items.Where(t => t.Refusal == null).Select(t => (t.Handle, Window: _windowService.GetWindowInfo(t.Handle)))
                    .ToList();
                return new BatchCloseCheck(protectedNow, targets, null);
            }
            else if (!confirm)
            {
                return new BatchCloseCheck(protectedNow, null,
                    BatchConfirmationRequired(requested, items.Select(t => t.Handle).Where(protectedNow.Contains).Distinct().ToList()));
            }
        }

        return new BatchCloseCheck(protectedNow, null, null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WindowCloseResult>> CloseBatchAsync(IReadOnlyList<nint> handles, bool force, int delayBetweenMs,
        CancellationToken cancellationToken) =>
        _windowService.CloseWindowsBatchAsync(handles, force, delayBetweenMs, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<WindowResultItem>> FinishBatchAsync(IReadOnlyList<WindowCloseResult> results, IReadOnlySet<nint> shownBefore,
        int waitMs, CancellationToken cancellationToken)
    {
        // The wait runs outside the gate, for every window at once
        var sent = results.Where(r => r.Success).Select(r => r.Handle).ToList();
        var observed = waitMs == 0 || sent.Count == 0
            ? new Dictionary<nint, CloseObservation>()
            : (await _closeVerifier.WaitForCloseAsync(sent, shownBefore, TimeSpan.FromMilliseconds(waitMs), cancellationToken).ConfigureAwait(false))
                .GroupBy(o => o.Handle).ToDictionary(g => g.Key, g => g.First());

        // A window whose close message could not be sent is notSent, as close_application reports it
        return results.Select(r =>
        {
            if (!r.Success)
            {
                return new WindowResultItem(r.Handle.ToInt64(), false, Error: r.Error, ErrorCode: r.ErrorCode.ToString(), Status: NotSent);
            }

            if (!observed.TryGetValue(r.Handle, out var observation))
            {
                return new WindowResultItem(r.Handle.ToInt64(), true, Status: CloseNames.Of(CloseStatus.Requested));
            }

            var outcome = Outcome(observation, waitMs);
            return new WindowResultItem(r.Handle.ToInt64(), outcome.Success, Error: outcome.Error, ErrorCode: outcome.ErrorCode,
                Status: outcome.Status, Prompt: outcome.Prompt);
        }).ToList();
    }

    // ---- close_application ----

    /// <inheritdoc />
    public ApplicationCloseTarget FindApplication(long? handle, int? processId, string? name)
    {
        var appWindows = _windowService.GetApplicationWindows();
        if (TargetApplication(handle, processId, name, appWindows) is not { } application)
        {
            return new ApplicationCloseTarget(null, [], $"{WindowService.InvalidHandleMessage}: {handle} is no window. Nothing was closed.");
        }

        var targets = appWindows.Where(application.Includes).ToList();
        return new ApplicationCloseTarget(application, targets);
    }

    /// <inheritdoc />
    public ApplicationCloseCheck CheckApplication(ApplicationCloseTarget app, long? handle, int? processId, string? name, bool confirm,
        bool canAsk)
    {
        var application = app.Application!;
        var processIds = application.ProcessIds;
        var targets = app.Targets;
        if (targets.Count == 0)
        {
            return new ApplicationCloseCheck(ApplicationRefusal(processIds, nameof(ProcessErrorCode.NoClosableWindow), application.NamesFrameHost
                ? $"{Describe(handle, processId, name)} is ApplicationFrameHost, which hosts the frame windows of every packaged " +
                  "(UWP) app, so closing it would close apps nobody named. Nothing was sent. Pass the handle of the one frame " +
                  "window to close instead (find_windows reports it)."
                : $"{Describe(handle, processId, name)} has no window the user can switch to (no visible, unowned window in Alt+Tab), " +
                  "so there is nothing to close gracefully: it may run without windows, only in the notification area, or only on another " +
                  "virtual desktop (windows there are closed only from that desktop). Nothing was " +
                  "sent. Check with find_windows or list_processes; kill_process ends a process without a window."),
                new Dictionary<nint, int?>());
        }

        // A close that would be refused is refused for the whole app before anything is asked or sent
        foreach (var target in targets)
        {
            if (_windowService.CheckClose(target.Handle, force: false) is { ErrorCode: not InvalidHandle } refused)
            {
                return new ApplicationCloseCheck(ApplicationRefusal(processIds, refused.ErrorCode.ToString(),
                    $"{refused.Error} Nothing was closed: close_application closes all of an application's windows or none."),
                    new Dictionary<nint, int?>());
            }
        }

        // The process each window belonged to when it was listed: a window that closed while the call waited (for the user or
        // the gate) and whose handle another process's window got is not closed (HandleMismatch), whether or not the user was asked
        var owners = targets.ToDictionary(t => t.Handle, t => (int?)t.ProcessId);
        if (targets.Count > MaxCloseBatchWithoutConfirm)
        {
            if (canAsk)
            {
                return new ApplicationCloseCheck(null, owners, Ask: true);
            }
            else if (!confirm)
            {
                return new ApplicationCloseCheck(null, owners, Required:
                    $"close_application would close {targets.Count} windows; more than {MaxCloseBatchWithoutConfirm} require confirm=true. " +
                    "Nothing was closed. Check the windows (find_windows) and call again with confirm=true.");
            }
        }

        return new ApplicationCloseCheck(null, owners);
    }

    /// <inheritdoc />
    public async Task<(Dictionary<nint, ClosedWindowItem> Items, List<nint> Sent)> SendApplicationClosesAsync(ApplicationCloseTarget app,
        IReadOnlyDictionary<nint, int?> owners, CancellationToken cancellationToken)
    {
        var items = new Dictionary<nint, ClosedWindowItem>();
        var sent = new List<nint>();
        foreach (var target in app.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HandleMismatchRefusal(target.Handle, owners) is { } mismatch)
            {
                items[target.Handle] = new ClosedWindowItem(target.Handle, target.Title, NotSent, mismatch.Error, mismatch.ErrorCode);
                continue;
            }

            // One window that fails does not stop the others: some may already have their close message, and the result
            // must still say what became of each
            WindowCloseResult result;
            try
            {
                result = await SendApplicationCloseAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Reported for this window alone, as a refusal would be; the exception is logged, since it can be a bug
                // (a NullReferenceException) that the message alone would hide
                _logger?.LogWarning(ex, "close_application could not send the close to window {Handle} of process {ProcessId}",
                    target.Handle, target.ProcessId);
                result = WindowCloseResult.Failed(target.Handle, CloseFailed, $"The close could not be sent: {ex.Message}");
            }

            if (result.Success)
            {
                sent.Add(target.Handle);
            }
            else
            {
                items[target.Handle] = new ClosedWindowItem(target.Handle, target.Title, NotSent, result.Error, result.ErrorCode.ToString());
            }
        }

        return (items, sent);
    }

    /// <inheritdoc />
    public async Task<CloseApplicationResult> FinishApplicationAsync(ApplicationCloseTarget app, Dictionary<nint, ClosedWindowItem> items,
        List<nint> sent, int timeoutMs, CancellationToken cancellationToken)
    {
        var application = app.Application!;
        var processIds = application.ProcessIds;
        var targets = app.Targets;

        // The waits run outside the gate. Every target was shown (Alt+Tab has only visible, uncloaked windows), so one the
        // app hid can be reported hidden
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var observed = sent.Count == 0 || timeoutMs == 0
            ? []
            : await _closeVerifier.WaitForCloseAsync(sent, targets.Select(t => t.Handle).ToHashSet(), TimeSpan.FromMilliseconds(timeoutMs),
                cancellationToken).ConfigureAwait(false);
        foreach (var observation in observed)
        {
            var target = targets.First(t => t.Handle == observation.Handle);
            var outcome = Outcome(observation, timeoutMs);
            items[observation.Handle] = new ClosedWindowItem(target.Handle, target.Title, outcome.Status, outcome.Error, outcome.ErrorCode);
        }

        if (timeoutMs == 0)
        {
            foreach (var target in targets.Where(t => sent.Contains(t.Handle)))
            {
                items[target.Handle] = new ClosedWindowItem(target.Handle, target.Title, CloseNames.Of(CloseStatus.Requested));
            }
        }

        var prompt = observed.Select(o => o.Prompt).FirstOrDefault(p => p != null);
        var remaining = _windowService.GetApplicationWindows().Where(application.Includes)
            .Select(w => new RemainingWindowDto(w.Handle.ToInt64(), w.Title, w.ProcessId, w.ProcessName)).ToList();
        var windowItems = targets.Select(t => items[t.Handle]).ToList();
        var pids = processIds.Order().ToList();

        // Windows of the application on other virtual desktops are not closed (Alt+Tab shows only this desktop's): they are
        // reported, so a status closed is never read as 'nothing of the application is left open'
        var otherDesktops = (_windowService.GetApplicationWindowsOnOtherDesktops() ?? []).Where(application.Includes)
            .Select(w => new RemainingWindowDto(w.Handle.ToInt64(), w.Title, w.ProcessId, w.ProcessName)).ToList();
        CloseApplicationResult Result(CloseApplicationResult result) =>
            otherDesktops.Count == 0 ? result : result with { OtherDesktopWindows = otherDesktops };

        if (prompt != null)
        {
            return Result(new CloseApplicationResult(false, CloseNames.Of(CloseStatus.PromptShown), pids, windowItems, remaining, true,
                ClosePromptDto.From(prompt),
                Error: $"The application did not close: it shows a prompt (see prompt) that waits for an answer. {PromptAdvice} " +
                       "Then call close_application again if it is still open.",
                ErrorCode: nameof(PromptShown)));
        }

        if (timeoutMs == 0 && sent.Count > 0)
        {
            // Only sent, as asked: nothing was checked
            return Result(new CloseApplicationResult(true, CloseNames.Of(CloseStatus.Requested), pids, windowItems, remaining, false));
        }

        if (remaining.Count > 0)
        {
            return Result(new CloseApplicationResult(false, CloseNames.Of(CloseStatus.StillOpen), pids, windowItems, remaining, false,
                Error: $"{remaining.Count} window{(remaining.Count == 1 ? " is" : "s are")} still open after {timeoutMs} ms, without a " +
                       "prompt (see windows for why: a close Windows refused, or an app that is busy or ignores it). Check them with " +
                       "find_windows; kill_process ends an app that will not close.",
                ErrorCode: nameof(StillOpen)));
        }

        if (pids.Count == 0)
        {
            // A frame closed by its own handle: its app cannot be told, so there is no process to wait for
            return Result(new CloseApplicationResult(true, CloseNames.Of(CloseStatus.Closed), pids, windowItems, remaining, false));
        }

        // A process with a window on another desktop keeps running: it is not waited for
        var staying = otherDesktops.Select(w => w.ProcessId).Where(pids.Contains).ToHashSet();
        var exitWait = TimeSpan.FromMilliseconds(timeoutMs) - waited.Elapsed;
        IReadOnlyList<int> running = staying.Count == pids.Count
            ? []
            : await _closeVerifier.WaitForProcessesToExitAsync(pids.Where(p => !staying.Contains(p)).ToList(),
                exitWait > MaxExitWait ? MaxExitWait : exitWait > TimeSpan.Zero ? exitWait : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        var stillRunning = running.Concat(staying).Distinct().Order().ToList();
        return Result(new CloseApplicationResult(true, CloseNames.Of(CloseStatus.Closed), pids, windowItems, remaining, false,
            Exited: stillRunning.Count == 0, RunningProcessIds: stillRunning.Count == 0 ? null : stillRunning));
    }

    /// <summary>
    /// Sends close_application's close to one window: WM_CLOSE, or for a packaged app's frame that does not take it,
    /// WindowPattern.Close. A WindowPattern.Close that did not return in time may still close the window, so it counts as
    /// sent and the wait reports what became of it.
    /// </summary>
    private async Task<WindowCloseResult> SendApplicationCloseAsync(WindowInfo target, CancellationToken cancellationToken)
    {
        var result = _windowService.CloseWindow(target.Handle, force: false);
        if (result.Success || result.ErrorCode != CloseFailed || !WindowClassification.IsApplicationFrameWindow(target.ClassName))
        {
            return result;
        }

        // A packaged app's frame that did not take the message (a full queue is NotResponding, not CloseFailed: a hung app
        // would not answer UI Automation either), asked through its Window pattern instead
        return await _closeVerifier.CloseWithUiAutomationAsync(target.Handle, cancellationToken).ConfigureAwait(false) is false
            ? result
            : WindowCloseResult.Sent(target.Handle);
    }

    /// <summary>The success, status, prompt and error of a window after its close, as the close tools report it.</summary>
    private static (bool Success, string Status, ClosePromptDto? Prompt, string? Error, string? ErrorCode) Outcome(CloseObservation observation, int waitMs) =>
        observation.Status switch
        {
            CloseStatus.Closed or CloseStatus.Hidden or CloseStatus.Requested => (true, CloseNames.Of(observation.Status), null, null, null),
            CloseStatus.PromptShown => (false, CloseNames.Of(CloseStatus.PromptShown), ClosePromptDto.From(observation.Prompt),
                $"The window did not close: it shows a prompt (see prompt) that waits for an answer. {PromptAdvice}" + WarningOf(observation),
                nameof(PromptShown)),
            _ => (false, CloseNames.Of(CloseStatus.StillOpen), null,
                $"The close message was sent, but the window was still open after {waitMs} ms and shows no prompt: the app may be busy " +
                "or slow, or ignore the request (a window that was already hidden or on another virtual desktop counts as open too). " +
                "Check it with find_windows, or wait longer." + WarningOf(observation), nameof(StillOpen))
        };

    /// <summary>The verifier's warning (a prompt it could not inspect), as a sentence to add to an error; empty without one.</summary>
    private static string WarningOf(CloseObservation observation) =>
        observation.Warning is { Length: > 0 } warning ? " " + warning : string.Empty;

    /// <summary>No windows: the shown-before set of a close that is not waited for.</summary>
    private static readonly IReadOnlySet<nint> EmptyHandles = new HashSet<nint>();

    /// <summary>
    /// The application close_application closes: its processes, and which application windows belong to it. A packaged
    /// app's frame whose app cannot be told (it is minimized or suspended, and its title is not unique) is reported under
    /// ApplicationFrameHost, which hosts the frames of every packaged app: such a frame is closed only when its own handle
    /// names it, and then alone, so one call never closes packaged apps nobody named. Null for a handle that is no window.
    /// </summary>
    private TargetedApplication? TargetApplication(long? handle, int? processId, string? name, IReadOnlyList<WindowInfo> appWindows)
    {
        if (handle is { } window)
        {
            // The handle's own process (for a UWP frame, the app's), whether or not the window itself is in Alt+Tab
            var info = _windowService.GetWindowInfo((nint)window);
            if (info == null)
            {
                return null;
            }

            return IsUnattributedFrame(info)
                ? new TargetedApplication([], w => w.Handle == info.Handle, NamesFrameHost: false)
                : ForProcesses([info.ProcessId], namesFrameHost: false);
        }

        Func<WindowInfo, bool> named = processId is { } id
            ? w => w.ProcessId == id
            : w => string.Equals(w.ProcessName, name, StringComparison.OrdinalIgnoreCase);
        var ids = processId is { } pid
            ? new HashSet<int> { pid }
            : appWindows.Where(w => named(w) && !IsUnattributedFrame(w)).Select(w => w.ProcessId).ToHashSet();
        return ForProcesses(ids, appWindows.Any(w => named(w) && IsUnattributedFrame(w)));

        static TargetedApplication ForProcesses(HashSet<int> ids, bool namesFrameHost) =>
            new(ids, w => ids.Contains(w.ProcessId) && !IsUnattributedFrame(w), namesFrameHost);
    }

    /// <summary>A packaged app's frame whose app could not be told: it is reported under ApplicationFrameHost.</summary>
    private static bool IsUnattributedFrame(WindowInfo window) =>
        WindowClassification.IsApplicationFrameWindow(window.ClassName) && window.HostProcessId == null;

    private static string Describe(long? handle, int? processId, string? name) =>
        handle is { } h ? $"The application of window {h}" : processId is { } id ? $"Process {id}" : $"No process named '{name}'";

    private static CloseApplicationResult ApplicationRefusal(IEnumerable<int> processIds, string errorCode, string error) =>
        new(false, null, processIds.Order().ToList(), [], [], false, Error: error, ErrorCode: errorCode);

    /// <summary>Why the close guard protects a window: its ProtectedWindow error without the advice to the agent.</summary>
    private static string ProtectionReason(string? error)
    {
        var text = error ?? "The window is protected.";
        var advice = text.IndexOf(" Nothing was sent.", StringComparison.Ordinal);
        return advice < 0 ? text : text[..advice];
    }

    /// <summary>
    /// Why close_windows_batch needs confirm=true on a client that cannot ask the user: more than
    /// <see cref="IWindowCloseService.MaxCloseBatchWithoutConfirm"/> windows, force on the <paramref name="protectedWindows"/> (roadmap M2.F1), or both.
    /// </summary>
    private static string BatchConfirmationRequired(int count, IReadOnlyList<nint> protectedWindows)
    {
        if (protectedWindows.Count == 0)
        {
            return $"close_windows_batch was asked to close {count} windows; more than {MaxCloseBatchWithoutConfirm} " +
                   "require confirm=true. Nothing was closed. Check the handles (find_windows) and call again with confirm=true.";
        }

        var handles = string.Join(", ", protectedWindows.Select(h => h.ToInt64().ToString(CultureInfo.InvariantCulture)));
        var one = protectedWindows.Count == 1;
        var rule = count > MaxCloseBatchWithoutConfirm
            ? $"More than {MaxCloseBatchWithoutConfirm} windows, and force on a protected window, require"
            : "force on a protected window requires";
        return $"close_windows_batch was asked to close {count} windows with force=true, and {(one ? "window" : "windows")} " +
               $"{handles} {(one ? "is" : "are")} protected (the desktop shell or the agent's own terminal or editor). {rule} " +
               "confirm=true when the client cannot ask the user. Nothing was closed. Call again with confirm=true only if the " +
               $"user explicitly asked to close {(one ? "that window" : "those windows")}, or without force to close the others.";
    }

    /// <inheritdoc />
    public WindowResultItem? HandleMismatchRefusal(long handle, IReadOnlyDictionary<nint, int?> confirmedOwners)
    {
        if (!confirmedOwners.TryGetValue((nint)handle, out var asked) || _windowService.GetWindowInfo((nint)handle) is not { } now ||
            now.ProcessId == asked)
        {
            return null;
        }

        var then = asked is { } pid
            ? $"not to the process (PID {pid}) it belonged to when it was chosen: the window closed and its handle was reused"
            : "but it was no window when the user was asked (the user saw it as 'no longer exists'): a new window got its handle";
        return new WindowResultItem(handle, false,
            Error: $"Window {handle} now belongs to {now.ProcessName} (PID {now.ProcessId}), {then}. It was not closed.",
            ErrorCode: nameof(HandleMismatch));
    }
}
