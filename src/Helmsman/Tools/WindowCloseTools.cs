using System.ComponentModel;
using ModelContextProtocol.Server;
using Helmsman.Infrastructure;
using Helmsman.Models;
using Helmsman.Models.Results;
using Helmsman.Services.Interfaces;
using static Helmsman.Models.WindowErrorCode;
using static Helmsman.Services.Interfaces.IWindowCloseService;
using static Helmsman.Tools.WindowBatchItems;

namespace Helmsman.Tools;

/// <summary>
/// The close tools: close_window, close_windows_batch and close_application. They bind the arguments, resolve the window
/// selectors, ask the user when the close service says a close needs a confirmation, and hold the input gate while the
/// service sends the close messages; the service checks, closes, waits for the windows outside the gate and builds the results.
/// </summary>
[McpServerToolType]
public class WindowCloseTools
{
    private readonly IWindowService _windowService;
    private readonly IWindowCloseService _closeService;
    private readonly IInputSession _session;
    private readonly TimeSpan _answerTimeout;
    private readonly TimeSpan? _gateWaitTimeout;

    /// <param name="windowService">Resolves the window selectors and checks them again inside the input gate.</param>
    /// <param name="closeService">Checks, closes and waits for the windows, and builds the results.</param>
    /// <param name="session">The input session, whose pause the close tools respect (they take the input gate themselves).</param>
    /// <param name="answerTimeout">How long close_windows_batch and close_application wait for the user's answer; default <see cref="UserConfirmation.DefaultAnswerTimeout"/>.</param>
    /// <param name="gateWaitTimeout">How long the close tools wait for the input gate; default <see cref="InputGateToolFilter.GateWaitTimeout"/>.</param>
    public WindowCloseTools(
        IWindowService windowService,
        IWindowCloseService closeService,
        IInputSession session,
        TimeSpan? answerTimeout = null,
        TimeSpan? gateWaitTimeout = null)
    {
        _windowService = windowService;
        _closeService = closeService ?? throw new ArgumentNullException(nameof(closeService));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _answerTimeout = answerTimeout ?? UserConfirmation.DefaultAnswerTimeout;
        _gateWaitTimeout = gateWaitTimeout;
    }

    /// <summary>close_windows_batch and close_application close at most this many windows without confirm=true.</summary>
    public const int MaxCloseBatchWithoutConfirm = IWindowCloseService.MaxCloseBatchWithoutConfirm;

    /// <summary>How long close_window and close_windows_batch wait for the windows to close by default (roadmap M2.5).</summary>
    public const int DefaultCloseWaitMs = 2000;

    /// <summary>The longest wait close_window, close_windows_batch and close_application accept.</summary>
    public const int MaxCloseWaitMs = 30000;

    /// <summary>How long close_application waits for its windows to close and its processes to exit by default.</summary>
    public const int DefaultApplicationCloseTimeoutMs = 5000;

    private const string ForceDescription =
        "Close even a protected window: the desktop shell (Program Manager, the taskbars) or the agent's own host " +
        "(the terminal, editor or chat app this server was started from). Default false: such windows are refused " +
        "with errorCode ProtectedWindow. Set it only when the user explicitly asked to close that window. The user is asked " +
        "first when the client can ask (elicitation; ConfirmationDeclined), else confirm=true is needed too (ConfirmationRequired).";

    private const string ForceConfirmDescription =
        "With force=true, must also be true to close a protected window when the MCP client cannot ask the user " +
        "(elicitation); without it such a window is not closed (errorCode ConfirmationRequired). When the client can ask, " +
        "the user's answer decides and confirm does not count. Set it only when the user explicitly asked to close that window.";

    private const string WaitDescription =
        "How long to wait for the window to close, in milliseconds (0-30000, default 2000). 0 only sends the close message " +
        "(status requested). The wait ends early when the window closes or shows a prompt.";

    private const string StatusDescription =
        "status: closed; hidden (the app hid the window it showed instead of closing it, for example into the notification " +
        "area; it still runs: use close_application or kill_process to end it); stillOpen (errorCode StillOpen: the app is " +
        "busy or ignores the request, or the window was already hidden or on another virtual desktop); promptShown (errorCode " +
        "PromptShown: a prompt such as 'Save changes?' waits; prompt gives its handle, title, className, text, buttons and " +
        "kind (inWindowDialog: drawn inside the window). " + PromptAdvice + "); requested " +
        "(waitMs 0: sent, not checked). success is true for closed, hidden and requested.";

    [McpServerTool(Name = "close_window", Title = "Close Window", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Close a window gracefully by sending WM_CLOSE, then wait up to waitMs and report what happened. " + StatusDescription + " " +
                 "Refuses the desktop shell and the agent's own host window (terminal, editor or chat app; errorCode ProtectedWindow) unless force is true; " +
                 "then, when the client can ask the user, the user is asked first (ConfirmationDeclined, or HandleMismatch when the " +
                 "window changed hands meanwhile), and when it cannot, confirm must be true too (ConfirmationRequired). It " +
                 "refuses an app the user's policy denies (TargetDeniedByPolicy; force does not override it), " +
                 "and reports AccessDenied when Windows blocks the message (the window belongs to an app running as administrator) " +
                 "and NotResponding when the app's message queue is full (it is hung). " +
                 "InvalidArguments: waitMs is outside 0-30000. To close every window of an app, use close_application.")]
    public async Task<CloseWindowResult> CloseWindow(
        [Description(WindowManagerTools.HandleDescription)]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description(ForceDescription)]
        bool force = false,
        [Description(WaitDescription)]
        int waitMs = DefaultCloseWaitMs,
        [Description(ForceConfirmDescription)]
        bool confirm = false,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        if (WaitRefusal(waitMs, nameof(waitMs)) is { } invalid)
        {
            return new CloseWindowResult(false, Error: invalid, ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode, Handle: handle);
        }

        // Nothing while the user has paused input, not even resolving: a pause is PausedByUser, never a selector error
        InputGateToolFilter.ThrowIfPaused(_session);

        // The selector is resolved before the gate; the close guards check the window it names as they check any handle
        var hwnd = WindowTargeting.Require(_windowService, handle, window, requireNamedWindow: true);
        var reported = hwnd.ToInt64();
        if (_closeService.CheckWindow(hwnd) is { } invalidHandle)
        {
            return invalidHandle;
        }

        // Only the close message is sent inside the input gate; the wait for the window runs outside it, so a slow app or a
        // prompt does not hold up every input and window call for up to waitMs
        var shownBefore = _closeService.ShownBefore([hwnd], waitMs);

        // force overrides the protection of the desktop shell and the agent's own host only with the user's yes when the
        // client can ask, and only with confirm=true when it cannot (roadmap M2.F1: two explicit decisions either way): the
        // window is first closed without force, and only a ProtectedWindow refusal asks the user or needs confirm
        var canAsk = UserConfirmation.CanAsk(server);
        var forceNow = force && !canAsk && confirm;
        var checkBeforeForce = force && !forceNow;
        WindowCloseResult sent;
        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            // The window the selector named must still be that window: it may have changed while the call waited
            WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);
            sent = _closeService.Close(hwnd, forceNow);
        }

        if (_closeService.CheckForce(hwnd, sent, checkBeforeForce, canAsk) is { } check)
        {
            if (check.Refusal is { } required)
            {
                return required;
            }

            // Asked outside the gate: a waiting user must not hold up every input and window call
            var (_, why, asked) = check;
            var answer = await UserConfirmation.AskAsync(server!, ForceQuestion(hwnd, asked, why),
                "Close this protected window", _answerTimeout, cancellationToken).ConfigureAwait(false);
            if (!answer.Confirmed)
            {
                return new CloseWindowResult(false,
                    Error: $"{answer.Reason} {why} force closes it only with the user's yes. Nothing was closed. Ask the user how " +
                           "to continue.",
                    ErrorCode: nameof(ConfirmationDeclined), Handle: reported);
            }

            InputGateToolFilter.ThrowIfPaused(_session);
            using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
            {
                WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);

                // The user agreed to close that window of that process; a handle reused meanwhile is not closed
                if (_closeService.HandleMismatchRefusal(reported, new Dictionary<nint, int?> { [hwnd] = asked?.ProcessId }) is { } mismatch)
                {
                    return new CloseWindowResult(false, Error: mismatch.Error, ErrorCode: mismatch.ErrorCode, Handle: reported);
                }

                sent = _closeService.Close(hwnd, force: true);
            }
        }

        return await _closeService.FinishCloseAsync(hwnd, sent, shownBefore, waitMs, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "close_windows_batch", Title = "Close Windows (Batch)", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Close multiple windows gracefully in a single batch operation, then wait up to waitMs for all of them and report each " +
                 "window's status as close_window does (closed, hidden, stillOpen, promptShown with its prompt, or requested), or notSent " +
                 "for a window refused or failed before its close message (see its errorCode), as close_application reports it. A prompt " +
                 "is never answered: ask the user which button to choose, and do not answer it yourself (then answer it as close_window says). " +
                 "More than 3 windows need a confirmation, and so does force=true on a protected window when the MCP client can ask " +
                 "the user (elicitation): the user is asked, with each window's title, process and PID, and nothing is closed unless " +
                 "they confirm (errorCode ConfirmationDeclined). When the client cannot ask, more than 3 windows, and force=true on a " +
                 "protected window, need confirm=true (ConfirmationRequired; nothing is closed). A window that changed hands after the user confirmed is not closed " +
                 "(HandleMismatch). Protected windows (the desktop shell, the agent's own host) " +
                 "are refused per window with errorCode ProtectedWindow unless force is true, and apps the user's policy denies with TargetDeniedByPolicy; " +
                 "the other windows are still closed. An item that is null, or names its window by both handle and window, fails alone " +
                 "with InvalidArguments; an item whose selector names no single window fails alone with WindowNotFound, " +
                 "AmbiguousWindow (with candidates) or HandleMismatch. InvalidArguments (the whole call): waitMs is outside 0-30000, or an " +
                 "item has an unknown member.")]
    public async Task<BatchWindowResult> CloseWindowsBatch(
        [Description("Array of windows to close, each with handle or window (a selector, as close_window takes it)")]
        WindowCloseRequest[] windows,
        [Description("Delay between operations in milliseconds (clamped to 0-5000, default: 50)")]
        int delayBetweenMs = 50,
        [Description("Must be true to close more than 3 windows in one batch, or (with force=true) a protected window, when the client cannot ask the user. Check the list (find_windows) first: closing many windows at once can lose unsaved work.")]
        bool confirm = false,
        [Description(ForceDescription + " Applies to every window in the batch.")]
        bool force = false,
        [Description("How long to wait for the windows to close after the last close message, in milliseconds (0-30000, default 2000); " +
                     "every window is waited for at the same time. 0 only sends the close messages (status requested).")]
        int waitMs = DefaultCloseWaitMs,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        RequireItems(windows, nameof(windows));
        if (WaitRefusal(waitMs, nameof(waitMs)) is { } invalid)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, invalid);
        }

        // Nothing while the user has paused input, not even a question
        InputGateToolFilter.ThrowIfPaused(_session);

        // Each item's window, resolved before the question and the gate (roadmap M2.4); an item that names no single window
        // fails alone
        var items = ResolveItems(_windowService, windows, nameof(windows), "a handle or a window", w => w.Handle, w => w.Window, requireNamedWindow: true);

        // More than 3 windows, and force on a window protected now, need the user's yes when the client can ask, and
        // confirm=true when it cannot (roadmap M2.F1); the service says which, and lists the windows to ask about
        var canAsk = UserConfirmation.CanAsk(server);
        var check = _closeService.CheckBatch(windows.Length, items, force, confirm, canAsk);
        Dictionary<nint, int?>? confirmedOwners = null;
        if (check.Question is { } targets)
        {
            // Ask the user, not the model, and before the input gate: a waiting user must not hold up every input call
            var answer = await UserConfirmation.AskAsync(server!, CloseQuestion(targets, force, check.Protected), $"Close these {targets.Count} windows",
                _answerTimeout, cancellationToken).ConfigureAwait(false);
            if (!answer.Confirmed)
            {
                throw new ToolErrorException(nameof(ConfirmationDeclined),
                    $"{answer.Reason} Nothing was closed. Ask the user how to continue.");
            }

            // Every handle the user was asked about, with its owner then (null: it was no window)
            confirmedOwners = targets.GroupBy(t => t.Handle).ToDictionary(g => g.Key, g => g.First().Window?.ProcessId);
        }
        else if (check.Required is { } required)
        {
            throw new ToolErrorException(nameof(ConfirmationRequired), required);
        }

        // Which windows the user could see before the close: only those can be reported hidden
        var shownBefore = _closeService.ShownBefore(items.Where(t => t.Refusal == null).Select(t => t.Handle).ToList(), waitMs);

        List<WindowResultItem?> refusals;
        IReadOnlyList<WindowCloseResult> results;
        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            // A window the user was asked about must still belong to the same process; a reused handle is not closed
            refusals = items.Select((t, i) => t.Refusal ??
                (confirmedOwners == null ? null : _closeService.HandleMismatchRefusal(t.Handle.ToInt64(), confirmedOwners)) ??
                ItemRecheck(_windowService, i, t.Handle, windows[i].Window)).ToList();
            var handles = items.Where((_, i) => refusals[i] == null).Select(t => t.Handle).ToList();

            var effectiveForce = force && (canAsk ? confirmedOwners != null : confirm);
            results = await _closeService.CloseBatchAsync(handles, effectiveForce, delayBetweenMs, cancellationToken).ConfigureAwait(false);
        }

        // The wait runs outside the gate, for every window at once
        var closed = await _closeService.FinishBatchAsync(results, shownBefore, waitMs, cancellationToken).ConfigureAwait(false);

        // A window that got no close message (refused, or the send failed) is notSent, as close_application reports it
        return Summary(windows.Length, InOrder(refusals.Select(r => r == null ? null : r with { Status = NotSent }).ToList(), closed));
    }

    [McpServerTool(Name = "close_application", Title = "Close Application", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Close an application gracefully: send WM_CLOSE to each of its windows the user can switch to (the Alt+Tab set: visible, " +
                 "not owned, not tool windows; never hidden helper or tray windows), then wait up to timeoutMs. It never forces: apps may ask " +
                 "to save, and kill_process is the way to end an app that will not close. Name the app by exactly one of handle (one of its " +
                 "windows), window (a selector for one of its windows), processId or processName (every process with that name that has such a window). A packaged (UWP) app is closed " +
                 "through its frame window, and its processId is the app's own (as find_windows reports it), not ApplicationFrameHost's; " +
                 "a frame whose app cannot be told is closed only by its own handle, alone. Of the shell's process (explorer) only File " +
                 "Explorer windows are closed. Returns status closed (every window on the current virtual desktop gone; exited says whether " +
                 "the processes ended, " +
                 "runningProcessIds lists those that keep running, for example in the notification area or with otherDesktopWindows: its " +
                 "windows on other virtual desktops, which are not closed), promptShown (errorCode " +
                 "PromptShown, promptDetected, and prompt with its handle, title, text and buttons. " + PromptAdvice + ") or stillOpen " +
                 "(errorCode StillOpen), with windows (each window's status) and remainingWindows; timeoutMs 0 only sends (status " +
                 "requested). Refused before anything is sent, with nothing closed: NoClosableWindow (no such window), " +
                 "ProtectedWindow (the agent's own host or the desktop shell; there is no force), TargetDeniedByPolicy, InvalidHandle, " +
                 "InvalidArguments. More than 3 windows need a confirmation as close_windows_batch does (ConfirmationDeclined, " +
                 "ConfirmationRequired), and a window that changed hands meanwhile is not closed (HandleMismatch).")]
    public async Task<CloseApplicationResult> CloseApplication(
        [Description("A window of the application (as integer); its process is closed")]
        long? handle = null,
        [Description("Alternative to handle: a window of the application as a window selector, resolved to " +
                     "exactly one window; its process is closed.")]
        WindowSelector? window = null,
        [Description("The application's process ID")]
        int? processId = null,
        [Description("The application's process name, with or without .exe (e.g. 'notepad'); matched exactly, ignoring case")]
        string? processName = null,
        [Description("How long to wait for the windows to close and the processes to exit, in milliseconds (0-30000, default 5000). " +
                     "0 only sends the close messages (status requested).")]
        int timeoutMs = DefaultApplicationCloseTimeoutMs,
        [Description("Must be true to close more than 3 windows when the client cannot ask the user. Check the windows (find_windows) first.")]
        bool confirm = false,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        if (WaitRefusal(timeoutMs, nameof(timeoutMs)) is { } invalidWait)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, invalidWait);
        }

        var name = ApplicationName(handle, window != null, processId, processName);

        // Nothing while the user has paused input, not even a question
        InputGateToolFilter.ThrowIfPaused(_session);

        // A window selector names the application by one of its windows, as handle does (roadmap M2.4); it is resolved
        // before anything is asked or sent, and the window it names goes through the same checks as a handle
        if (window != null)
        {
            handle = WindowTargeting.Require(_windowService, null, window, requireNamedWindow: true).ToInt64();
        }

        var app = _closeService.FindApplication(handle, processId, name);
        if (app.Refusal is { } noWindow)
        {
            throw new ToolErrorException(nameof(InvalidHandle), noWindow);
        }

        var targets = app.Targets;

        // The audit log records each window with its process, as it is before the close
        AuditScope.NoteWindows(targets);

        // A close that would be refused is refused for the whole app before anything is asked or sent; more than 3 windows
        // need the user's yes, or confirm=true when the client cannot ask
        var check = _closeService.CheckApplication(app, handle, processId, name, confirm, UserConfirmation.CanAsk(server));
        if (check.Refusal is { } refused)
        {
            return refused;
        }

        if (check.Ask)
        {
            var answer = await UserConfirmation.AskAsync(server!, ApplicationQuestion(targets), $"Close these {targets.Count} windows",
                _answerTimeout, cancellationToken).ConfigureAwait(false);
            if (!answer.Confirmed)
            {
                throw new ToolErrorException(nameof(ConfirmationDeclined),
                    $"{answer.Reason} Nothing was closed. Ask the user how to continue.");
            }
        }
        else if (check.Required is { } required)
        {
            throw new ToolErrorException(nameof(ConfirmationRequired), required);
        }

        Dictionary<nint, ClosedWindowItem> items;
        List<nint> sent;
        using (await InputGateToolFilter.EnterAsync(_session, cancellationToken, _gateWaitTimeout).ConfigureAwait(false))
        {
            // The window the selector named the application by must still be that window, or nothing is closed
            if (window != null && handle is { } named)
            {
                WindowTargeting.ThrowIfChanged(_windowService, window, (nint)named);
            }

            (items, sent) = await _closeService.SendApplicationClosesAsync(app, check.Owners, cancellationToken).ConfigureAwait(false);
        }

        // The waits run outside the gate
        return await _closeService.FinishApplicationAsync(app, items, sent, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>InvalidArguments text for a wait outside 0 to <see cref="MaxCloseWaitMs"/>, or null.</summary>
    private static string? WaitRefusal(int waitMs, string name) =>
        waitMs is >= 0 and <= MaxCloseWaitMs
            ? null
            : $"{name} must be between 0 and {MaxCloseWaitMs} milliseconds, but it is {waitMs}. Nothing was closed.";

    /// <summary>
    /// close_application's one target, checked: exactly one of handle, window, processId and processName, and the name read
    /// as <see cref="ProcessNames"/> reads it (a path is refused). Returns the name (null unless processName was given).
    /// </summary>
    private static string? ApplicationName(long? handle, bool window, int? processId, string? processName)
    {
        var given = (handle.HasValue ? 1 : 0) + (window ? 1 : 0) + (processId.HasValue ? 1 : 0) + (processName != null ? 1 : 0);
        if (given != 1)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"Pass exactly one of handle, window, processId and processName ({given} given). Nothing was closed.");
        }

        if (processId is <= 0)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"processId must be a positive process ID, but it is {processId}. Nothing was closed.");
        }

        if (processName == null)
        {
            return null;
        }

        if (ProcessNames.PathRefusal(nameof(processName), processName) is { } path)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, $"{path} Nothing was closed.");
        }

        var name = ProcessNames.Normalize(processName);
        return name.Length > 0
            ? name
            : throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, "processName is empty. Nothing was closed.");
    }

    /// <summary>The question close_application asks the user: the application's windows, with their titles, processes and PIDs.</summary>
    private static string ApplicationQuestion(IReadOnlyList<WindowInfo> targets)
    {
        var apps = string.Join(", ", targets.Select(t => $"{t.ProcessName} (PID {t.ProcessId})").Distinct());
        var lines = targets.Take(20).Select((w, i) =>
            $"{i + 1}. {UserConfirmation.Quote(string.IsNullOrEmpty(w.Title) ? w.ClassName : w.Title)} ({w.ProcessName}, PID {w.ProcessId})");
        var more = targets.Count > 20 ? $"\n... and {targets.Count - 20} more" : string.Empty;
        return $"The AI agent asks to close {apps}: {targets.Count} windows. Each may ask to save your work, or close without asking.\n" +
               string.Join("\n", lines) + more + "\nAllow it?";
    }

    /// <summary>
    /// The question close_window asks before force overrides a window's protection: the window, why it is protected, and
    /// what closing it does.
    /// </summary>
    private static string ForceQuestion(nint handle, WindowInfo? window, string protection)
    {
        var line = window is { } w
            ? $"{UserConfirmation.Quote(string.IsNullOrEmpty(w.Title) ? w.ClassName : w.Title)} ({w.ProcessName}, PID {w.ProcessId}){HostLabel(w)}"
            : $"window {handle} (no longer exists)";
        return "The AI agent asks to close a protected window with force: the desktop shell (closing it can open the Shut Down " +
               "dialog) or the window of the terminal, editor or chat app the agent runs in (closing it can end the agent's " +
               $"session and lose unsaved work).\n{line}\n{protection}\nAllow it?";
    }

    /// <summary>
    /// How a question to the user marks a window of the agent's host: as the agent's own window, or, while the server cannot read
    /// its ancestor processes and the window counts as the host's only for that (HostGuard), as possibly the agent's own.
    /// </summary>
    private static string HostLabel(WindowInfo window) =>
        !window.IsAgentHost ? string.Empty
        : window.IsAgentHostOnlyWhileAncestorsUnknown ? ", possibly the agent's own window"
        : ", the agent's own window";

    /// <summary>How many windows close_windows_batch's question lists one by one, besides the protected ones.</summary>
    private const int MaxListedInCloseQuestion = 20;

    /// <summary>
    /// The question close_windows_batch asks the user: the windows, with their title, process and PID; past the first
    /// <see cref="MaxListedInCloseQuestion"/> only a count, except for the protected windows force would close, which are
    /// always named, so a yes never closes a protected window the user was not shown.
    /// </summary>
    private static string CloseQuestion(IReadOnlyList<(nint Handle, WindowInfo? Window)> targets, bool force,
        IReadOnlySet<nint> protectedWindows)
    {
        var lines = targets
            .Select((t, i) => (Target: t, Number: i + 1, Protected: force && protectedWindows.Contains(t.Handle)))
            .Where(t => t.Number <= MaxListedInCloseQuestion || t.Protected)
            .Select(t => t.Target.Window is { } w
                ? $"{t.Number}. {UserConfirmation.Quote(string.IsNullOrEmpty(w.Title) ? w.ClassName : w.Title)} ({w.ProcessName}, PID {w.ProcessId}){HostLabel(w)}{(t.Protected ? ", protected" : string.Empty)}"
                : $"{t.Number}. window {t.Target.Handle} (no longer exists)")
            .ToList();
        var more = targets.Count > lines.Count ? $"\n... and {targets.Count - lines.Count} more{(force ? " (none of them protected)" : string.Empty)}" : string.Empty;
        var protection = force
            ? " force is set: protected windows (the desktop shell, the agent's own terminal or editor) are closed too."
            : string.Empty;
        return $"The AI agent asks to close {targets.Count} windows. Each app may ask to save your work, or close without asking.{protection}\n" +
               string.Join("\n", lines) + more + "\nAllow it?";
    }

    /// <summary>
    /// The input gate for a window-changing tool, after its window was resolved: waits, refuses (InputBusy) and reports a
    /// pause (PausedByUser) exactly as the gate filter does for the tools it gates.
    /// </summary>
    private Task<IDisposable> EnterGateAsync(CancellationToken cancellationToken) =>
        InputGateToolFilter.EnterAsync(_session, cancellationToken, _gateWaitTimeout);
}
