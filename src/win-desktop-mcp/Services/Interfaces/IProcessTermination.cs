using WinDesktopMcp.Models.Results;

namespace WinDesktopMcp.Services.Interfaces;

/// <summary>kill_process: the checks of a process before it is closed or ended, and the close or termination itself.</summary>
public interface IProcessTermination
{
    /// <summary>
    /// The checks of <see cref="KillProcessAsync"/> without acting: the refusal it would return for this process
    /// (except a missing confirm), or the process's name and, with <paramref name="forceKill"/>, the child processes it
    /// would also end. Without <paramref name="forceKill"/>, a process without a main window is refused with
    /// NoClosableWindow, as the graceful close would refuse it. kill_process uses it to ask the user before it enters the
    /// input gate.
    /// </summary>
    KillTargetCheck CheckKillTarget(int processId, bool forceKill, int gracefulTimeoutMs, string? expectedProcessName = null);

    /// <summary>
    /// Terminates a process by its ID.
    /// </summary>
    /// <param name="processId">The process ID to terminate.</param>
    /// <param name="confirm">Must be true to confirm termination (safety check).</param>
    /// <param name="forceKill">
    /// If true, terminates the process and all of its child processes immediately.
    /// If false, only requests a graceful close (WM_CLOSE to the main window) and never escalates:
    /// a process that cannot be closed, or is still running after the timeout, is left running and
    /// the result reports success=false.
    /// </param>
    /// <param name="gracefulTimeoutMs">Graceful mode: how long to wait for the process to exit (0-60000 ms).</param>
    /// <param name="expectedProcessName">Optional PID-reuse guard: the expected process name, with or without .exe.</param>
    /// <param name="safeguards">The process the user confirmed, and what to call before the wait for the exit (see <see cref="KillSafeguards"/>).</param>
    /// <param name="cancellationToken">
    /// Cancels the wait for the process to exit; the close request or termination already sent stands, and the call
    /// throws <see cref="OperationCanceledException"/>. Cancelled before anything was sent, nothing is done.
    /// </param>
    /// <returns>Result stating whether a close was requested, whether termination was forced, and whether the process exited.</returns>
    Task<KillResult> KillProcessAsync(
        int processId,
        bool confirm,
        bool forceKill = false,
        int gracefulTimeoutMs = 5000,
        string? expectedProcessName = null,
        KillSafeguards? safeguards = null,
        CancellationToken cancellationToken = default);
}
