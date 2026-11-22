using System.ComponentModel;
using Helmsman.Models;
using Helmsman.Models.Results;
using Helmsman.Services.Interfaces;
using static Helmsman.Services.ProcessHelpers;

namespace Helmsman.Services;

/// <summary>
/// kill_process (<see cref="IProcessTermination"/>): the checks of a process before it is closed or ended (the protected
/// processes, the target policy, the expected name, the agent's host, and with forceKill its whole tree) and the graceful
/// close or forced termination itself.
/// </summary>
public sealed class ProcessTerminationService : IProcessTermination
{
    // Protected system and shell processes that should not be terminated
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "smss", "csrss", "wininit", "services", "lsass", "lsm",
        "svchost", "winlogon", "dwm", "explorer", "Registry", "Memory Compression",
        // User-session shell infrastructure: killing these breaks keyboard input, the Start menu or search
        "sihost", "ctfmon", "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "TextInputHost", "fontdrvhost",
        // Review F1: the frame host of every Store app, whose windows are the frames of apps the target policy denies too
        // (Windows Security, the sign-in window); the policy, which decides by the process name, cannot see them here
        UwpFrames.FrameHostProcessName
    };

    public const int DefaultGracefulTimeoutMs = 5000;
    public const int MaxGracefulTimeoutMs = 60000;
    private const int ForceKillExitWaitMs = 5000;
    private const int ErrorAccessDenied = 5;
    private const string ForceKillConsequence =
        "this terminates the process and all of its child processes immediately, and unsaved work is lost.";

    /// <summary>What counts as the agent's host process, for the CannotTerminateHost messages.</summary>
    private const string HostProcessKinds =
        "this server, an ancestor of it such as the terminal or editor hosting the agent, the process that shows " +
        "its console or the console server behind it (conhost or OpenConsole), or the app that was in front when the server started";

    private readonly INativeProcessWrapper _processWrapper;
    private readonly ITargetPolicy _targetPolicy;
    private readonly IHostGuard _hostGuard;

    /// <param name="processWrapper">Opens, closes and ends the process, and reads the process tree.</param>
    /// <param name="targetPolicy">The server's target policy, which kill_process enforces (roadmap M1.12).</param>
    /// <param name="hostGuard">
    /// The agent's host as recorded at server start (roadmap M1.1/M1.7): kill_process refuses its
    /// processes, and a forced kill whose process tree contains one.
    /// </param>
    public ProcessTerminationService(INativeProcessWrapper processWrapper, ITargetPolicy targetPolicy, IHostGuard hostGuard)
    {
        _processWrapper = processWrapper ?? throw new ArgumentNullException(nameof(processWrapper));
        _targetPolicy = targetPolicy ?? throw new ArgumentNullException(nameof(targetPolicy));
        _hostGuard = hostGuard ?? throw new ArgumentNullException(nameof(hostGuard));
    }

    public async Task<KillResult> KillProcessAsync(
        int processId,
        bool confirm,
        bool forceKill = false,
        int gracefulTimeoutMs = DefaultGracefulTimeoutMs,
        string? expectedProcessName = null,
        KillSafeguards? safeguards = null,
        CancellationToken cancellationToken = default)
    {
        // Safety check: require explicit confirmation
        if (!confirm)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ErrorMessage: "Confirmation required. Set confirm=true to terminate the process.",
                ErrorCode: nameof(ProcessErrorCode.ConfirmationRequired));
        }

        if (ArgumentRefusal(processId, gracefulTimeoutMs) is { } invalid)
        {
            return invalid;
        }

        IProcessHandle? process;
        try
        {
            process = _processWrapper.OpenProcess(processId);
        }
        catch (Exception ex)
        {
            return KillFailureFromException(ex, processId, processName: null);
        }

        if (process == null)
        {
            return ProcessNotFound(processId);
        }

        using (process)
        {
            string processName;
            try
            {
                processName = process.ProcessName;
            }
            catch (InvalidOperationException)
            {
                // The process exited between opening it and reading its name
                return ProcessNotFound(processId);
            }

            if (TargetRefusal(processId, processName, forceKill, expectedProcessName, out var tree) is { } refused)
            {
                return refused;
            }

            if (safeguards?.Confirmed is { } confirmed && ConfirmedTargetRefusal(processId, processName, forceKill, confirmed, tree) is { } changed)
            {
                return changed;
            }

            // A call cancelled before anything is sent does nothing
            cancellationToken.ThrowIfCancellationRequested();

            var beforeWait = safeguards?.BeforeWait;
            return forceKill
                ? await TerminateForcefullyAsync(process, processId, processName, beforeWait, cancellationToken).ConfigureAwait(false)
                : await CloseGracefullyAsync(process, processId, processName, gracefulTimeoutMs, beforeWait, cancellationToken).ConfigureAwait(false);
        }
    }

    public KillTargetCheck CheckKillTarget(int processId, bool forceKill, int gracefulTimeoutMs, string? expectedProcessName = null)
    {
        if (ArgumentRefusal(processId, gracefulTimeoutMs) is { } invalid)
        {
            return KillTargetCheck.Refused(invalid);
        }

        IProcessHandle? process;
        try
        {
            process = _processWrapper.OpenProcess(processId);
        }
        catch (Exception ex)
        {
            return KillTargetCheck.Refused(KillFailureFromException(ex, processId, processName: null));
        }

        if (process == null)
        {
            return KillTargetCheck.Refused(ProcessNotFound(processId));
        }

        using (process)
        {
            string processName;
            try
            {
                processName = process.ProcessName;
            }
            catch (InvalidOperationException)
            {
                return KillTargetCheck.Refused(ProcessNotFound(processId));
            }

            if (TargetRefusal(processId, processName, forceKill, expectedProcessName, out var tree) is { } refused)
            {
                return KillTargetCheck.Refused(refused);
            }

            // A graceful close needs a main window to ask; without one the kill would refuse after the user's yes.
            // (A main window that a modal dialog disables is only found by the close request itself.)
            if (!forceKill && !HasMainWindow(process))
            {
                return KillTargetCheck.Refused(NoClosableWindow(processId, processName));
            }

            // Read while the process is open: it holds a handle that pins the PID (review F4; when none could be opened, the
            // start time is what the kill compares to tell a reused PID)
            return new KillTargetCheck(null, processName, tree ?? [], _processWrapper.GetProcessStartTime(processId));
        }
    }

    /// <summary>
    /// Why the open process is not the one the user confirmed, or null: it started at another time than the confirmed one
    /// (the PID was reused while the question was open), or, with <paramref name="forceKill"/>, its tree has a process the
    /// user was not shown. The process is open and holds a handle that pins its PID (review F4), so the start time read here
    /// is the process that is then closed or ended; when no handle could be opened, this check is what is left.
    /// </summary>
    private KillResult? ConfirmedTargetRefusal(
        int processId, string processName, bool forceKill, KillTargetCheck confirmed, IReadOnlyList<(int ProcessId, string ImageName)>? tree)
    {
        if (confirmed.StartTime is { } confirmedStart && _processWrapper.GetProcessStartTime(processId) is var start && start != confirmedStart)
        {
            var started = start is { } now ? $"at {now:u}" : "at a time that cannot be read";
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ProcessName: processName,
                ErrorMessage: $"PID {processId} now belongs to a '{processName}' process that started {started}, not the one the user " +
                              $"confirmed (started at {confirmedStart:u}): that one exited and its PID was reused while the question was " +
                              "open. Nothing was done; call list_processes to get the current PID.",
                ErrorCode: nameof(ProcessErrorCode.ProcessNameMismatch));
        }

        if (forceKill && tree != null)
        {
            var shown = confirmed.ChildProcesses.Select(c => c.ProcessId).ToHashSet();
            var added = tree.Where(c => !shown.Contains(c.ProcessId)).ToList();
            if (added.Count > 0)
            {
                var names = string.Join(", ", added.Take(10).Select(c => $"{c.ImageName} (PID {c.ProcessId})")) +
                            (added.Count > 10 ? $" and {added.Count - 10} more" : string.Empty);
                return new KillResult(
                    Success: false,
                    ProcessId: processId,
                    ProcessName: processName,
                    ErrorMessage: $"forceKill would also end {names}, which started under '{processName}' (PID {processId}) while the " +
                                  "user was asked, so the user never saw them. Nothing was terminated. Call kill_process again to ask " +
                                  "about the process tree as it is now.",
                    ErrorCode: nameof(ProcessErrorCode.ProcessTreeChanged));
            }
        }

        return null;
    }

    /// <summary>True when the process has a main window, or when that cannot be read (the close request then finds out).</summary>
    private static bool HasMainWindow(IProcessHandle process)
    {
        try
        {
            return process.RefreshMainWindowHandle() != nint.Zero;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return true;
        }
    }

    private static KillResult NoClosableWindow(int processId, string processName) => new(
        Success: false,
        ProcessId: processId,
        ProcessName: processName,
        ErrorMessage: $"'{processName}' (PID {processId}) has no main window that can receive a close request " +
                      "(it may be windowless, running only in the notification area, or its window is blocked by a " +
                      "modal dialog). It is still running and nothing was terminated. To terminate it anyway, call " +
                      $"kill_process again with forceKill=true: {ForceKillConsequence}",
        ErrorCode: nameof(ProcessErrorCode.NoClosableWindow));

    /// <summary>The refusal of a process ID or timeout that is not valid, or null.</summary>
    private static KillResult? ArgumentRefusal(int processId, int gracefulTimeoutMs)
    {
        // Validate process ID
        if (processId <= 0)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ErrorMessage: "Invalid process ID. Must be a positive integer.",
                ErrorCode: nameof(ProcessErrorCode.InvalidProcessId));
        }

        // Cannot kill self
        if (processId == Environment.ProcessId)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ErrorMessage: "Cannot terminate the current process.",
                ErrorCode: nameof(ProcessErrorCode.CannotTerminateSelf));
        }

        if (gracefulTimeoutMs < 0 || gracefulTimeoutMs > MaxGracefulTimeoutMs)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ErrorMessage: $"gracefulTimeoutMs must be between 0 and {MaxGracefulTimeoutMs}.",
                ErrorCode: nameof(ProcessErrorCode.InvalidTimeout));
        }

        return null;
    }

    /// <summary>
    /// Why the open process must not be closed or terminated, or null: a protected system process, the target
    /// policy, a PID that now belongs to another process than <paramref name="expectedProcessName"/>, the agent's
    /// host, and with <paramref name="forceKill"/> a child process that is denied or belongs to the host (or a tree
    /// that cannot be read). <paramref name="tree"/> is the process tree a forced kill would end, when it was read.
    /// </summary>
    private KillResult? TargetRefusal(
        int processId, string processName, bool forceKill, string? expectedProcessName, out IReadOnlyList<(int ProcessId, string ImageName)>? tree)
    {
        tree = null;

        // Check if it's a protected system process
        if (ProtectedProcesses.Contains(processName))
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ProcessName: processName,
                ErrorMessage: ProtectedProcessMessage(processId, processName),
                ErrorCode: nameof(ProcessErrorCode.ProtectedProcess));
        }

        // The user's policy (HELMSMAN_DENY_PROCESSES, HELMSMAN_ALLOW_PROCESSES); no argument overrides it
        if (_targetPolicy.Evaluate([processName]) is { Allowed: false } verdict)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ProcessName: processName,
                ErrorMessage: $"'{processName}' (PID {processId}) is refused by the server's target policy: {verdict.Reason}. " +
                              "Nothing was done. The policy is set by the user in the server's environment; ask the user to end this process.",
                ErrorCode: nameof(ProcessErrorCode.TargetDeniedByPolicy));
        }

        // PID reuse guard: the process the agent saw in list_processes may have exited
        if (!string.IsNullOrWhiteSpace(expectedProcessName) && !ProcessNameMatches(processName, expectedProcessName))
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ProcessName: processName,
                ErrorMessage: $"PID {processId} belongs to '{processName}', not '{expectedProcessName.Trim()}'. " +
                              "The process you meant may have exited and its PID been reused. Nothing was done; " +
                              "call list_processes to get the current PID.",
                ErrorCode: nameof(ProcessErrorCode.ProcessNameMismatch));
        }

        // Closing or killing the agent's host (the terminal or editor hosting the agent) ends its own session
        var isHost = IsHostProcess(processId);
        if (isHost == true)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ProcessName: processName,
                ErrorMessage: $"'{processName}' (PID {processId}) belongs to the agent's host: {HostProcessKinds}. " +
                              "Closing it would end or disrupt the agent's own session, so it is refused. Nothing was done.",
                ErrorCode: nameof(ProcessErrorCode.CannotTerminateHost));
        }

        // Review F3: fails closed. A graceful close has no other check behind it (only Kill(entireProcessTree) refuses to end
        // an ancestor of the caller on its own)
        if (isHost == null)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ProcessName: processName,
                ErrorMessage: $"The process list could not be read, so the server cannot tell whether '{processName}' (PID {processId}) " +
                              "is an ancestor of this server, such as the terminal or editor hosting the agent: closing it could end " +
                              "the agent's own session (the agent's host). Nothing was done. Try again.",
                ErrorCode: nameof(ProcessErrorCode.ProcessTreeUnreadable));
        }

        // forceKill ends the whole process tree, so every process in it must pass the policy and the
        // host check too. A graceful close asks only the process's own main window to close.
        if (forceKill)
        {
            // Fails closed: the runtime's tree kill finds the children on its own, so an unread tree would
            // still be ended, unchecked
            if (_processWrapper.GetDescendantsOf(processId) is not { } descendants)
            {
                return new KillResult(
                    Success: false,
                    ProcessId: processId,
                    ProcessName: processName,
                    ErrorMessage: $"forceKill also ends the child processes of '{processName}' (PID {processId}), but the process list " +
                                  "could not be read, or did not list the process (it may just have exited), so they could not be " +
                                  "checked against the target policy and the agent's host. Nothing was terminated. Try again, or close " +
                                  "it without forceKill (only its main window is asked to close).",
                    ErrorCode: nameof(ProcessErrorCode.ProcessTreeUnreadable));
            }

            tree = descendants;
            if (ProtectedDescendant(descendants) is { } protectedChild)
            {
                return new KillResult(
                    Success: false,
                    ProcessId: processId,
                    ProcessName: processName,
                    ErrorMessage: $"forceKill also ends the child processes of '{processName}' (PID {processId}), and its child process " +
                                  $"'{protectedChild.ImageName}' (PID {protectedChild.ProcessId}) is a protected system process. " +
                                  "Nothing was terminated. Close it without forceKill (only its main window is asked to close), or ask the user.",
                    ErrorCode: nameof(ProcessErrorCode.ProtectedProcess));
            }

            if (DeniedDescendant(descendants) is { } denied)
            {
                return new KillResult(
                    Success: false,
                    ProcessId: processId,
                    ProcessName: processName,
                    ErrorMessage: $"forceKill also ends the child processes of '{processName}' (PID {processId}), and its child process " +
                                  $"'{denied.ImageName}' (PID {denied.ProcessId}) is refused by the server's target policy: {denied.Reason}. " +
                                  "Nothing was terminated. Close it without forceKill (only its main window is asked to close), or ask the user.",
                    ErrorCode: nameof(ProcessErrorCode.TargetDeniedByPolicy));
            }

            if (HostDescendant(descendants) is { } host)
            {
                return new KillResult(
                    Success: false,
                    ProcessId: processId,
                    ProcessName: processName,
                    ErrorMessage: $"forceKill also ends the child processes of '{processName}' (PID {processId}), and its child process " +
                                  $"'{host.ImageName}' (PID {host.ProcessId}) belongs to the agent's host: {HostProcessKinds}. " +
                                  "Ending it would end or disrupt the agent's own session, so nothing was terminated. Close it without " +
                                  "forceKill (only its main window is asked to close), or ask the user.",
                    ErrorCode: nameof(ProcessErrorCode.CannotTerminateHost));
            }
        }

        return null;
    }

    /// <summary>
    /// The first process of a process tree that is a protected system process (by its image name without .exe), or null: a
    /// forced kill would end it with the tree.
    /// </summary>
    private static (int ProcessId, string ImageName)? ProtectedDescendant(IReadOnlyList<(int ProcessId, string ImageName)> tree)
    {
        foreach (var child in tree)
        {
            if (ProtectedProcesses.Contains(ProcessNames.Normalize(child.ImageName)))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>
    /// The first process of a process tree (children, grandchildren, ...) that the target policy refuses,
    /// or null.
    /// </summary>
    private (int ProcessId, string ImageName, string? Reason)? DeniedDescendant(IReadOnlyList<(int ProcessId, string ImageName)> tree)
    {
        foreach (var (childId, imageName) in tree)
        {
            if (_targetPolicy.Evaluate([imageName]) is { Allowed: false } verdict)
            {
                return (childId, imageName, verdict.Reason);
            }
        }

        return null;
    }

    /// <summary>
    /// The first process of a process tree that belongs to the agent's host (<see cref="IHostGuard.IsHostProcess"/>,
    /// or this server itself), or null. The live ancestor walk of <see cref="IsHostProcess"/> is not repeated
    /// for each child: a child that is an ancestor of the server makes the tree's root one too, and the
    /// root has been checked already.
    /// </summary>
    private (int ProcessId, string ImageName)? HostDescendant(IReadOnlyList<(int ProcessId, string ImageName)> tree)
    {
        foreach (var child in tree)
        {
            if (child.ProcessId == Environment.ProcessId || _hostGuard.IsHostProcess(child.ProcessId))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>
    /// Graceful mode: posts WM_CLOSE to the main window and waits. Never escalates to a forced kill;
    /// a process that cannot be closed or is still running is left alone and reported as such.
    /// </summary>
    private static async Task<KillResult> CloseGracefullyAsync(
        IProcessHandle process, int processId, string processName, int timeoutMs, Action? beforeWait, CancellationToken cancellationToken)
    {
        bool closeRequested;
        try
        {
            closeRequested = process.CloseMainWindow();
        }
        catch (Exception ex)
        {
            return KillFailureFromException(ex, processId, processName);
        }

        if (!closeRequested)
        {
            if (HasExitedSafe(process))
            {
                return new KillResult(Success: true, ProcessId: processId, ProcessName: processName, Exited: true);
            }

            return NoClosableWindow(processId, processName);
        }

        beforeWait?.Invoke();
        bool exited;
        try
        {
            exited = await process.WaitForExitAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The close request was sent; the process may still exit, or show a prompt
            throw;
        }
        catch (Exception ex)
        {
            return KillFailureFromException(ex, processId, processName) with { CloseRequested = true };
        }

        if (exited)
        {
            return new KillResult(Success: true, ProcessId: processId, ProcessName: processName, CloseRequested: true, Exited: true);
        }

        return new KillResult(
            Success: false,
            ProcessId: processId,
            ProcessName: processName,
            ErrorMessage: $"A close request was sent to '{processName}' (PID {processId}), but it was still running after " +
                          $"{timeoutMs} ms. It may be showing a prompt such as 'Save changes?', still shutting down, or " +
                          "running elevated (Windows then drops the close request). Nothing was force-terminated. Handle " +
                          "the prompt, retry with a longer gracefulTimeoutMs, or call kill_process with forceKill=true: " +
                          ForceKillConsequence,
            ErrorCode: nameof(ProcessErrorCode.GracefulCloseTimedOut),
            CloseRequested: true);
    }

    /// <summary>
    /// Force mode: terminates the process and its child processes, then confirms that it exited.
    /// </summary>
    private static async Task<KillResult> TerminateForcefullyAsync(
        IProcessHandle process, int processId, string processName, Action? beforeWait, CancellationToken cancellationToken)
    {
        AggregateException? treeFailures = null;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (AggregateException ex)
        {
            // Kill(entireProcessTree: true) tries the root and every descendant, then reports all
            // failures together without saying which process failed. Whether the root is gone,
            // which decides the outcome, is checked below.
            treeFailures = ex;
        }
        catch (InvalidOperationException) when (HasExitedSafe(process))
        {
            return new KillResult(Success: true, ProcessId: processId, ProcessName: processName, Exited: true);
        }
        catch (Exception ex)
        {
            return KillFailureFromException(ex, processId, processName);
        }

        var accessDenied = treeFailures != null && IsAccessDenied(treeFailures);
        var warning = treeFailures == null
            ? null
            : "Some child processes could not be terminated and may still be running" +
              (accessDenied ? " (access denied: they probably run elevated or as another user)" : "") +
              ": " + string.Join("; ", treeFailures.InnerExceptions.Select(e => e.Message));

        beforeWait?.Invoke();
        bool exited;
        try
        {
            exited = await process.WaitForExitAsync(ForceKillExitWaitMs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The termination was requested; only the confirmation that the process exited is not waited for
            throw;
        }
        catch (Exception ex)
        {
            return KillFailureFromException(ex, processId, processName) with { Forced = true, Warning = warning };
        }

        if (exited)
        {
            return new KillResult(Success: true, ProcessId: processId, ProcessName: processName, Forced: true, Exited: true, Warning: warning);
        }

        if (accessDenied)
        {
            // The root is still running, so the denial most likely came from the root itself
            return KillFailureFromException(treeFailures!, processId, processName) with
            {
                Forced = true,
                Warning = "Termination was attempted for the whole process tree, so some of its child processes may " +
                          "have been terminated even though the process itself is still running."
            };
        }

        return new KillResult(
            Success: false,
            ProcessId: processId,
            ProcessName: processName,
            ErrorMessage: $"Termination of '{processName}' (PID {processId}) was requested, but the process had not exited " +
                          $"after {ForceKillExitWaitMs} ms. It may still be shutting down; check with list_processes.",
            ErrorCode: nameof(ProcessErrorCode.TerminationNotConfirmed),
            Forced: true,
            Warning: warning);
    }

    /// <summary>
    /// True for a process of the agent's host: what the host guard recorded at server start (this server,
    /// its ancestors, the console host's process and the startup foreground window's process), or an
    /// ancestor found by a live walk of the process tree. The live walk keeps the check working when the
    /// host guard could not read the ancestors at start (it then logs a warning, and get_server_info warns).
    /// Null when the process is not a recorded host process and the walk cannot read the process list
    /// (review F3): the caller refuses then, since a graceful close has no other check behind it (only
    /// Kill(entireProcessTree: true) refuses an ancestor of the caller on its own).
    /// </summary>
    private bool? IsHostProcess(int processId)
    {
        if (_hostGuard.IsHostProcess(processId))
        {
            return true;
        }

        try
        {
            return _processWrapper.IsAncestorOfCurrentProcess(processId);
        }
        catch (Exception)
        {
            // The process tree could not be read: unknown, not "no ancestor"
            return null;
        }
    }

    /// <summary>
    /// Why a protected process is refused. ApplicationFrameHost (review F1) gets its own reason and what to do instead: its
    /// windows are the frames of every Store app, those the target policy denies among them, so the agent is pointed at
    /// the app's own process and window.
    /// </summary>
    private static string ProtectedProcessMessage(int processId, string processName) =>
        UwpFrames.IsFrameHostProcess(processName)
            ? $"'{processName}' (PID {processId}) holds the window frames of every Store (UWP) app, including apps the server's " +
              "target policy refuses, such as Windows Security and the sign-in window: a close request could close any of them, " +
              "and terminating it would end the windows of them all. Nothing was done. Close the app by its own process instead " +
              "(find_windows reports a Store app's window with the app's own processId, and hostProcessName 'ApplicationFrameHost'), " +
              "or close its window with close_window."
            : $"Cannot terminate protected system process '{processName}'.";

    /// <summary>
    /// Whether the PID's process has the expected name, read as <see cref="ProcessNames"/> reads an argument: a path never
    /// passes (kill_process refuses it first). The process's own name is compared as Windows reports it.
    /// </summary>
    private static bool ProcessNameMatches(string actualName, string expectedName) =>
        ProcessNames.FromArgument(expectedName) is { Length: > 0 } name && actualName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool IsAccessDenied(Exception ex) => ex switch
    {
        Win32Exception win32 => win32.NativeErrorCode == ErrorAccessDenied,
        UnauthorizedAccessException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsAccessDenied),
        _ => false
    };

    private static KillResult KillFailureFromException(Exception ex, int processId, string? processName)
    {
        if (IsAccessDenied(ex))
        {
            var target = processName != null ? $"'{processName}' (PID {processId})" : $"PID {processId}";
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ProcessName: processName,
                ErrorMessage: $"Access denied for {target}. It is probably running elevated (as administrator), as another " +
                              "user, or as a protected process; terminating it requires the server to run with at least the " +
                              "same privileges.",
                ErrorCode: nameof(ProcessErrorCode.AccessDenied));
        }

        return new KillResult(
            Success: false,
            ProcessId: processId,
            ProcessName: processName,
            ErrorMessage: ex.Message,
            ErrorCode: nameof(ProcessErrorCode.TerminationException));
    }

    private static KillResult ProcessNotFound(int processId) => new(
        Success: false,
        ProcessId: processId,
        ErrorMessage: "Process not found. It may have already exited.",
        ErrorCode: nameof(ProcessErrorCode.ProcessNotFound));
}
