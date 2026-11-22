using Helmsman.Models;
using Helmsman.Models.Results;

namespace Helmsman.Services.Interfaces;

/// <summary>
/// The process services in one interface, for the tools and services that use several of them: each member is the one of
/// <see cref="ILaunchPlanner"/>, <see cref="IApplicationLauncher"/>, <see cref="IProcessListing"/>,
/// <see cref="IProcessTermination"/> or <see cref="IDebugBrowserLauncher"/> it names (<see cref="ProcessService"/> hands
/// each call on).
/// </summary>
public interface IProcessService
{
    /// <inheritdoc cref="IApplicationLauncher.LaunchApplicationAsync(string, string[], string, bool, int, CancellationToken)"/>
    Task<LaunchResult> LaunchApplicationAsync(
        string executable,
        string[]? arguments = null,
        string? workingDirectory = null,
        bool waitForWindow = false,
        int waitTimeoutMs = 5000,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IApplicationLauncher.LaunchApplicationAsync(LaunchRequest, bool, int, CancellationToken)"/>
    Task<LaunchResult> LaunchApplicationAsync(
        LaunchRequest request,
        bool waitForWindow,
        int waitTimeoutMs,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IProcessListing.GetRunningProcesses"/>
    IReadOnlyList<ProcessInfo> GetRunningProcesses(ProcessQuery query);

    /// <inheritdoc cref="IProcessListing.GetProcessName"/>
    string? GetProcessName(int processId);

    /// <inheritdoc cref="IProcessListing.GetProcessPath"/>
    string? GetProcessPath(int processId);

    /// <inheritdoc cref="IProcessTermination.CheckKillTarget"/>
    KillTargetCheck CheckKillTarget(int processId, bool forceKill, int gracefulTimeoutMs, string? expectedProcessName = null);

    /// <inheritdoc cref="IProcessTermination.KillProcessAsync"/>
    Task<KillResult> KillProcessAsync(
        int processId,
        bool confirm,
        bool forceKill = false,
        int gracefulTimeoutMs = 5000,
        string? expectedProcessName = null,
        KillSafeguards? safeguards = null,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IApplicationLauncher.LaunchApplicationsBatchAsync"/>
    Task<IReadOnlyList<LaunchResult>> LaunchApplicationsBatchAsync(
        IReadOnlyList<LaunchRequest> applications,
        bool waitForWindows = false,
        int waitTimeoutMs = 5000,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IDebugBrowserLauncher.LaunchBrowserWithDebugAsync"/>
    Task<DebugBrowserLaunchResult> LaunchBrowserWithDebugAsync(
        DebugBrowserLaunchRequest request,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ILaunchPlanner.PreviewElevation"/>
    ElevationPreview PreviewElevation(LaunchRequest request);

    /// <inheritdoc cref="ILaunchPlanner.CheckLaunch"/>
    LaunchRefusal? CheckLaunch(LaunchRequest request);
}

/// <summary>Why a launch would not start: the error code and message the launch tools report.</summary>
public sealed record LaunchRefusal(string ErrorCode, string Error);

/// <summary>One running process, as <see cref="IProcessService.GetRunningProcesses"/> lists it.</summary>
/// <param name="ProcessId">The process ID.</param>
/// <param name="Name">The process name without the .exe extension.</param>
/// <param name="ExecutablePath">The full path of the executable; null when the process cannot be opened.</param>
/// <param name="WindowCount">The process's visible, titled top-level windows (see GetRunningProcesses).</param>
/// <param name="MemoryUsageMB">The working set in MB; null when it cannot be read.</param>
/// <param name="ParentProcessId">
/// The process that started it, when that process is still running and both start times can be read and show it
/// started no later than the child (so a later process that reused the parent's PID is not named); null otherwise:
/// when either process cannot be opened for its start time, or when the process snapshot cannot be read.
/// </param>
/// <param name="StartTime">When the process started (UTC); null when it cannot be read.</param>
/// <param name="Windows">
/// The first <see cref="MaxWindows"/> of the windows <paramref name="WindowCount"/> counts, front to back; empty when it
/// has none.
/// </param>
/// <param name="Integrity">Its integrity level and elevation (IProcessSecurityProbe); null when the service has no probe.</param>
/// <param name="AboveServer">
/// True when it is known or likely to run at a higher integrity level than the server
/// (<see cref="ProcessIntegrity.IsMorePrivilegedThan"/>, the input guard's TargetElevated test): Windows drops the server's
/// input and UI Automation to its windows.
/// </param>
/// <param name="CommandLine">
/// Its command line, redacted (<see cref="Infrastructure.CommandLineRedactor"/>) and cut to
/// <see cref="MaxCommandLineLength"/> characters; null unless asked for, and when it cannot be read or the user's target
/// policy denies the process.
/// </param>
/// <param name="CommandLineRedacted">Whether something in <paramref name="CommandLine"/> was masked.</param>
public record ProcessInfo(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    int WindowCount,
    double? MemoryUsageMB,
    int? ParentProcessId = null,
    DateTime? StartTime = null,
    IReadOnlyList<WindowInfo>? Windows = null,
    ProcessIntegrity? Integrity = null,
    bool AboveServer = false,
    string? CommandLine = null,
    bool CommandLineRedacted = false
)
{
    /// <summary>The most windows <see cref="Windows"/> holds; find_windows with the process ID lists the rest.</summary>
    public const int MaxWindows = 10;

    /// <summary>The longest command line returned, the ellipsis of a cut one included.</summary>
    public const int MaxCommandLineLength = 500;
}
