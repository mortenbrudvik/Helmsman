using Helmsman.Models;
using Helmsman.Services.Interfaces;
using static Helmsman.Services.WindowChangeFailures;
using static Helmsman.Services.WindowReads;

namespace Helmsman.Services;

/// <summary>
/// The server's target policy (roadmap M1.12) applied to a window, as every window service applies it: the verdict on the
/// window, matched by its process and a UWP frame also by the app it hosts, and the refusal of a window change before
/// anything is sent. Holds nothing between calls.
/// </summary>
internal sealed class WindowTargetCheck
{
    private readonly INativeWindowWrapper _nativeWrapper;
    private readonly WindowReads _reads;
    private readonly ITargetPolicy _targetPolicy;

    /// <param name="nativeWrapper">The window calls.</param>
    /// <param name="reads">Whether a handle is a window, and the process a window belongs to.</param>
    /// <param name="targetPolicy">The server's target policy.</param>
    public WindowTargetCheck(INativeWindowWrapper nativeWrapper, WindowReads reads, ITargetPolicy targetPolicy)
    {
        _nativeWrapper = nativeWrapper;
        _reads = reads;
        _targetPolicy = targetPolicy;
    }

    /// <summary>
    /// Why a window change is refused before anything is sent: the handle is not a window (InvalidHandle), or the
    /// target policy denies the window (TargetDeniedByPolicy). Null when the change can go ahead.
    /// </summary>
    public WindowChangeResult? ChangeRefusal(nint handle)
    {
        if (!_reads.IsValidWindow(handle))
            return WindowChangeResult.Failed(handle, WindowErrorCode.InvalidHandle, InvalidHandleMessage);

        return EvaluateTargetPolicy(handle) is { Allowed: false } verdict
            ? WindowChangeResult.Failed(handle, WindowErrorCode.TargetDeniedByPolicy, WindowChangeRefusal(handle, verdict))
            : null;
    }

    /// <summary>
    /// The error of a window change that the target policy refuses with <paramref name="verdict"/>: the
    /// Window Control tools report it with TargetDeniedByPolicy.
    /// </summary>
    public static string WindowChangeRefusal(nint handle, TargetPolicyVerdict verdict) =>
        $"Window {handle} is refused by the server's target policy: {verdict.Reason}. Nothing was changed: the window was not " +
        "moved, restored, raised or activated. The policy is set by the user in the server's environment and no tool argument " +
        "changes it; ask the user to do this step.";

    /// <inheritdoc cref="IWindowService.EvaluateTargetPolicy"/>
    public TargetPolicyVerdict EvaluateTargetPolicy(nint handle)
    {
        var session = new ReadSession(_nativeWrapper, topLevelWindows: null);
        var className = _nativeWrapper.GetClassName(handle) ?? string.Empty;
        var title = _nativeWrapper.GetWindowText(handle) ?? string.Empty;
        var process = _reads.ResolveProcess(handle, className, title, _nativeWrapper.GetWindowThreadProcessId(handle), session);

        // A process that cannot be named cannot be checked, so it is refused while the policy denies anything, as the input
        // guard, the captures and the UI Automation reads refuse it; a security prompt of a shared process is refused by its
        // structure (roadmap M3.F5)
        var name = session.ProcessName(process.ProcessId);
        return _targetPolicy.EvaluateFailClosed(handle, process.HostProcessId is { } hostId ? [name, session.ProcessName(hostId)] : [name]);
    }
}
