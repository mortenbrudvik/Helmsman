using Helmsman.Models;
using Helmsman.Models.Results;

namespace Helmsman.Services.Interfaces;

/// <summary>
/// Starts applications: every request is planned by <see cref="ILaunchPlanner"/> before anything starts, then reuses an open
/// window (reuseExisting), starts the program or activates the packaged app, and waits for its window.
/// </summary>
public interface IApplicationLauncher
{
    /// <summary>
    /// Launches an application, and with <paramref name="waitForWindow"/> waits up to
    /// <paramref name="waitTimeoutMs"/> for its window: <see cref="LaunchApplicationAsync(LaunchRequest, bool, int, CancellationToken)"/>
    /// without a window hint.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the call: before the start nothing is started; during the wait for the window the started program keeps
    /// running and the call throws <see cref="OperationCanceledException"/>.
    /// </param>
    Task<LaunchResult> LaunchApplicationAsync(
        string executable,
        string[]? arguments = null,
        string? workingDirectory = null,
        bool waitForWindow = false,
        int waitTimeoutMs = 5000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Launches an application (roadmap M2.6/M2.7). Bare names are resolved to an absolute path, never from the working
    /// directory, and an unresolved one is refused (ExecutableNotFound); the working directory defaults to the program's
    /// folder. The start is waited for at most <see cref="ApplicationLauncher.StartTimeout"/>; a longer one is reported as
    /// <see cref="LaunchStatus.AwaitingUserConsent"/> (a UAC prompt). When Windows starts no process (the request went to
    /// a running app), the launch still succeeds, with no process ID.
    /// <para>
    /// With <paramref name="waitForWindow"/>, waits up to <paramref name="waitTimeoutMs"/> (at most 60000) for the window
    /// the launch opens, whichever process owns it (<see cref="IWindowWaitService"/>, <see cref="LaunchWindowOwner"/>),
    /// and reports it in <see cref="LaunchResult.Window"/> with a <see cref="LaunchResult.Status"/>.
    /// </para>
    /// <para>
    /// With <see cref="LaunchRequest.ReuseExisting"/> (roadmap M3.13), a valid request first looks for an open window of the
    /// same app that shows what the launch opens; when it finds one nothing is started and the result is
    /// <see cref="LaunchStatus.AlreadyOpen"/> with that window (no process ID). ReuseWindowFinder.FindOpenWindow defines which
    /// window counts.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">As for the other overload.</param>
    Task<LaunchResult> LaunchApplicationAsync(
        LaunchRequest request,
        bool waitForWindow,
        int waitTimeoutMs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Launches multiple applications: each one is checked and started in order, as by
    /// <see cref="LaunchApplicationAsync(LaunchRequest, bool, int, CancellationToken)"/>; with <paramref name="waitForWindows"/>
    /// the windows are then waited for together, against one deadline (<paramref name="waitTimeoutMs"/> after the last
    /// start), and a new window is reported for one launch only. An open window that one item reuses
    /// (<see cref="LaunchRequest.ReuseExisting"/>) is not reused by a later item.
    /// </summary>
    /// <param name="applications">The applications to launch.</param>
    /// <param name="waitForWindows">Whether to wait for each application's window.</param>
    /// <param name="waitTimeoutMs">How long to wait for the windows, in milliseconds.</param>
    /// <param name="progress">Receives "i of n" each time an application is done: refused, started, or its window wait ended. Optional.</param>
    /// <param name="cancellationToken">
    /// Cancels the batch: no further application is started, the window waits end, and the call throws
    /// <see cref="OperationCanceledException"/>.
    /// </param>
    /// <returns>A result for each application, in request order.</returns>
    Task<IReadOnlyList<LaunchResult>> LaunchApplicationsBatchAsync(
        IReadOnlyList<LaunchRequest> applications,
        bool waitForWindows = false,
        int waitTimeoutMs = 5000,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
