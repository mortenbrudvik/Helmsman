using Helmsman.Models;
using Helmsman.Models.Results;
using Helmsman.Services.Interfaces;

namespace Helmsman.Services;

/// <summary>
/// The tools' one entry to the process services (<see cref="IProcessService"/>): it only hands each call to the service that
/// does it. <see cref="ILaunchPlanner"/> plans the launches, <see cref="IApplicationLauncher"/> starts them,
/// <see cref="IProcessListing"/> lists the processes, <see cref="IProcessTermination"/> ends them and
/// <see cref="IDebugBrowserLauncher"/> starts a debug browser. A consumer that needs only one of them takes that one.
/// </summary>
public class ProcessService : IProcessService
{
    private readonly ILaunchPlanner _planner;
    private readonly IApplicationLauncher _launcher;
    private readonly IProcessListing _listing;
    private readonly IProcessTermination _termination;
    private readonly IDebugBrowserLauncher _debugBrowsers;

    /// <param name="planner">Plans a launch without starting it: CheckLaunch and the runAsAdmin preview.</param>
    /// <param name="launcher">Starts the launches.</param>
    /// <param name="listing">list_processes and the reads of a process's name and path.</param>
    /// <param name="termination">kill_process.</param>
    /// <param name="debugBrowsers">launch_browser_with_debug's launch.</param>
    public ProcessService(
        ILaunchPlanner planner,
        IApplicationLauncher launcher,
        IProcessListing listing,
        IProcessTermination termination,
        IDebugBrowserLauncher debugBrowsers)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _listing = listing ?? throw new ArgumentNullException(nameof(listing));
        _termination = termination ?? throw new ArgumentNullException(nameof(termination));
        _debugBrowsers = debugBrowsers ?? throw new ArgumentNullException(nameof(debugBrowsers));
    }

    public Task<LaunchResult> LaunchApplicationAsync(
        string executable,
        string[]? arguments = null,
        string? workingDirectory = null,
        bool waitForWindow = false,
        int waitTimeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        _launcher.LaunchApplicationAsync(executable, arguments, workingDirectory, waitForWindow, waitTimeoutMs, cancellationToken);

    public Task<LaunchResult> LaunchApplicationAsync(
        LaunchRequest request,
        bool waitForWindow,
        int waitTimeoutMs,
        CancellationToken cancellationToken = default) =>
        _launcher.LaunchApplicationAsync(request, waitForWindow, waitTimeoutMs, cancellationToken);

    public Task<IReadOnlyList<LaunchResult>> LaunchApplicationsBatchAsync(
        IReadOnlyList<LaunchRequest> applications,
        bool waitForWindows = false,
        int waitTimeoutMs = 5000,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _launcher.LaunchApplicationsBatchAsync(applications, waitForWindows, waitTimeoutMs, progress, cancellationToken);

    public ElevationPreview PreviewElevation(LaunchRequest request) => _planner.PreviewElevation(request);

    public LaunchRefusal? CheckLaunch(LaunchRequest request) => _planner.CheckLaunch(request);

    public IReadOnlyList<ProcessInfo> GetRunningProcesses(ProcessQuery query) => _listing.GetRunningProcesses(query);

    public string? GetProcessName(int processId) => _listing.GetProcessName(processId);

    public string? GetProcessPath(int processId) => _listing.GetProcessPath(processId);

    public Task<KillResult> KillProcessAsync(
        int processId,
        bool confirm,
        bool forceKill = false,
        int gracefulTimeoutMs = ProcessTerminationService.DefaultGracefulTimeoutMs,
        string? expectedProcessName = null,
        KillSafeguards? safeguards = null,
        CancellationToken cancellationToken = default) =>
        _termination.KillProcessAsync(processId, confirm, forceKill, gracefulTimeoutMs, expectedProcessName, safeguards, cancellationToken);

    public KillTargetCheck CheckKillTarget(int processId, bool forceKill, int gracefulTimeoutMs, string? expectedProcessName = null) =>
        _termination.CheckKillTarget(processId, forceKill, gracefulTimeoutMs, expectedProcessName);

    public Task<DebugBrowserLaunchResult> LaunchBrowserWithDebugAsync(
        DebugBrowserLaunchRequest request,
        CancellationToken cancellationToken = default) =>
        _debugBrowsers.LaunchBrowserWithDebugAsync(request, cancellationToken);
}
