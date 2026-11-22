using WinDesktopMcp.Models;

namespace WinDesktopMcp.Services.Interfaces;

/// <summary>
/// The close tools' work below the tool layer (close_window, close_windows_batch, close_application; roadmap M2.5, M2.F1):
/// it finds an application's windows, checks what a close would refuse and what must be confirmed, with the data the
/// question shows, closes, waits for the windows and builds the results. The tools resolve the window selectors, ask the
/// user (MCP elicitation) and pass the answer on, and hold the input gate around the calls that close: each method here
/// is one step, called in the order the tool's flow has always had, so a re-check after the user answered stays where it was.
/// </summary>
public interface IWindowCloseService
{
    /// <summary>close_windows_batch and close_application close at most this many windows without confirm=true.</summary>
    const int MaxCloseBatchWithoutConfirm = 3;

    /// <summary>
    /// What the agent must do about a prompt a close left open. Every PromptShown text and description carries it: not
    /// every client loads the skill, and answering 'Save changes?' with No or Don't Save destroys the user's work. Once the
    /// user chose, the prompt is answered by its own handle, since respond_to_dialog refuses a press asked of an owned window
    /// (which it takes for a dialog) that a modal dialog of its own disables (BlockedByModal); an in-window dialog has no
    /// window of its own, so its button is pressed with element_action.
    /// </summary>
    const string PromptAdvice =
        "Nothing was answered. Do not answer it yourself: show the user the prompt's text and buttons and ask which to " +
        "choose (No or Don't Save discards their unsaved work), then answer it as they say with respond_to_dialog, handle: " +
        "prompt.handle (inWindowDialog, which has no window of its own: element_action on its button).";

    /// <summary>The status of a window close_application did not send a close message to.</summary>
    const string NotSent = "notSent";

    /// <summary>
    /// The windows the user can see now, which the wait may report hidden (<see cref="IWindowCloseVerifier.ShownWindows"/>);
    /// none when <paramref name="waitMs"/> is 0 and nothing is waited for.
    /// </summary>
    IReadOnlySet<nint> ShownBefore(IReadOnlyList<nint> handles, int waitMs);

    /// <summary>
    /// HandleMismatch for a window the user did not see: it belongs to another process than when the user confirmed, or
    /// it was no window then and is one now (its handle was reused), or null. A window that is gone is left to the
    /// close, which reports InvalidHandle.
    /// </summary>
    WindowResultItem? HandleMismatchRefusal(long handle, IReadOnlyDictionary<nint, int?> confirmedOwners);

    /// <summary>close_window's InvalidHandle for a handle that is no window, or null.</summary>
    CloseWindowResult? CheckWindow(nint hwnd);

    /// <summary>Sends close_window's close message (WM_CLOSE) through the window service's close guards.</summary>
    WindowCloseResult Close(nint hwnd, bool force);

    /// <summary>
    /// What close_window does when force was asked for and its close without force was refused as ProtectedWindow: null
    /// when there is nothing to confirm; otherwise ConfirmationRequired (<see cref="ForceCloseCheck.Refusal"/>) when the
    /// client cannot ask the user, or why the window is protected and the window as the user is asked about it.
    /// </summary>
    ForceCloseCheck? CheckForce(nint hwnd, WindowCloseResult sent, bool checkBeforeForce, bool canAsk);

    /// <summary>
    /// close_window's result once its close message was sent or refused: the refusal, requested (waitMs 0), or what the
    /// wait found (closed, hidden, stillOpen, promptShown). The wait runs outside the input gate.
    /// </summary>
    Task<CloseWindowResult> FinishCloseAsync(nint hwnd, WindowCloseResult sent, IReadOnlySet<nint> shownBefore, int waitMs,
        CancellationToken cancellationToken);

    /// <summary>
    /// What close_windows_batch must have confirmed before anything is sent: the protected windows force would close (read
    /// only when force needs a confirmation), and either the windows to ask the user about, with their owners now, or
    /// the ConfirmationRequired text when the client cannot ask and confirm is false.
    /// </summary>
    BatchCloseCheck CheckBatch(int requested, IReadOnlyList<(nint Handle, WindowResultItem? Refusal)> items, bool force, bool confirm,
        bool canAsk);

    /// <summary>Sends close_windows_batch's close messages, in order, through the window service's close guards.</summary>
    Task<IReadOnlyList<WindowCloseResult>> CloseBatchAsync(IReadOnlyList<nint> handles, bool force, int delayBetweenMs,
        CancellationToken cancellationToken);

    /// <summary>
    /// close_windows_batch's item for each window it sent to, in order, after waiting for them all at once outside the input
    /// gate: notSent for a send that failed, requested when nothing was waited for, or what the wait found.
    /// </summary>
    Task<IReadOnlyList<WindowResultItem>> FinishBatchAsync(IReadOnlyList<WindowCloseResult> results, IReadOnlySet<nint> shownBefore,
        int waitMs, CancellationToken cancellationToken);

    /// <summary>
    /// close_application's application, named by exactly one of a window's handle, a process ID and a process name (without
    /// ".exe"), and its windows the user can switch to; or why there is none (a handle that is no window).
    /// </summary>
    ApplicationCloseTarget FindApplication(long? handle, int? processId, string? name);

    /// <summary>
    /// close_application's checks before anything is asked or sent: NoClosableWindow, or the refusal any of its windows'
    /// closes would get (for the whole application); otherwise each window's owner now, and whether the user must be asked
    /// or, when the client cannot ask and confirm is false, the ConfirmationRequired text.
    /// </summary>
    ApplicationCloseCheck CheckApplication(ApplicationCloseTarget app, long? handle, int? processId, string? name, bool confirm,
        bool canAsk);

    /// <summary>
    /// Sends close_application's close to each of its windows, inside the input gate: a window whose handle another process
    /// got meanwhile is not sent to (HandleMismatch), and one whose close fails or throws is reported alone (notSent).
    /// </summary>
    Task<(Dictionary<nint, ClosedWindowItem> Items, List<nint> Sent)> SendApplicationClosesAsync(ApplicationCloseTarget app,
        IReadOnlyDictionary<nint, int?> owners, CancellationToken cancellationToken);

    /// <summary>
    /// close_application's result once its close messages were sent: it waits outside the input gate for the windows and
    /// then the processes, and reports each window, the windows that remain and those on other virtual desktops.
    /// </summary>
    Task<CloseApplicationResult> FinishApplicationAsync(ApplicationCloseTarget app, Dictionary<nint, ClosedWindowItem> items,
        List<nint> sent, int timeoutMs, CancellationToken cancellationToken);
}

/// <summary>close_window's check of a force that met a protected window (<see cref="IWindowCloseService.CheckForce"/>).</summary>
/// <param name="Refusal">ConfirmationRequired when the client cannot ask the user; null when the user is to be asked.</param>
/// <param name="Why">Why the window is protected: its ProtectedWindow error without the advice to the agent.</param>
/// <param name="Asked">The window as the user is asked about it (null: it is no window any more).</param>
public sealed record ForceCloseCheck(CloseWindowResult? Refusal, string Why, WindowInfo? Asked);

/// <summary>What close_windows_batch must have confirmed (<see cref="IWindowCloseService.CheckBatch"/>).</summary>
/// <param name="Protected">The protected windows force would close.</param>
/// <param name="Question">The windows to ask the user about, with their owners now; null when the user is not asked.</param>
/// <param name="Required">The ConfirmationRequired text; null when the batch may go ahead or the user is asked.</param>
public sealed record BatchCloseCheck(
    IReadOnlySet<nint> Protected, IReadOnlyList<(nint Handle, WindowInfo? Window)>? Question, string? Required);

/// <param name="ProcessIds">The processes closed; empty for one frame closed by its own handle.</param>
/// <param name="Includes">Whether an application window belongs to the application.</param>
/// <param name="NamesFrameHost">The process named is ApplicationFrameHost: it has frames of packaged apps that cannot be told apart.</param>
public sealed record TargetedApplication(HashSet<int> ProcessIds, Func<WindowInfo, bool> Includes, bool NamesFrameHost);

/// <summary>close_application's application and its windows the user can switch to (<see cref="IWindowCloseService.FindApplication"/>).</summary>
/// <param name="Application">The application; null with <paramref name="Refusal"/>.</param>
/// <param name="Targets">Its windows the user can switch to, as they were listed; the audit log records them.</param>
/// <param name="Refusal">InvalidHandle's text for a handle that is no window; null when the application was found.</param>
public sealed record ApplicationCloseTarget(TargetedApplication? Application, IReadOnlyList<WindowInfo> Targets, string? Refusal = null);

/// <summary>close_application's checks before anything is asked or sent (<see cref="IWindowCloseService.CheckApplication"/>).</summary>
/// <param name="Refusal">The refusal of the whole application (NoClosableWindow, ProtectedWindow, TargetDeniedByPolicy...); null when it may be closed.</param>
/// <param name="Owners">The process each window belonged to when it was listed.</param>
/// <param name="Ask">The user must be asked (more than <see cref="IWindowCloseService.MaxCloseBatchWithoutConfirm"/> windows, and the client can ask).</param>
/// <param name="Required">The ConfirmationRequired text; null when the close may go ahead or the user is asked.</param>
public sealed record ApplicationCloseCheck(
    CloseApplicationResult? Refusal, IReadOnlyDictionary<nint, int?> Owners, bool Ask = false, string? Required = null);
