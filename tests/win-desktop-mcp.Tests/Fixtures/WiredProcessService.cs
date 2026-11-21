namespace WinDesktopMcp.Tests.Fixtures;

/// <summary>
/// A <see cref="ProcessService"/> wired from its parts as ServiceRegistration wires them, for the tests that drive the
/// launch, listing, kill and debug-browser paths through one object. <see cref="Create"/> takes the collaborators
/// ProcessService took before it was split into those parts, in the same order, and hands each part the ones it uses.
/// </summary>
public sealed class WiredProcessService : ProcessService
{
    private WiredProcessService(
        LaunchPlanner planner,
        ProcessListingService listing,
        ProcessTerminationService termination,
        IWindowService windowService,
        IHostGuard hostGuard,
        DebugBrowserLocator debugBrowserLocator,
        IProcessSecurityProbe securityProbe,
        INativeProcessWrapper processWrapper,
        IWindowWaitService? windowWait,
        IDebugBrowserReadiness? debugBrowserReadiness,
        DebugBrowserProfiles? debugBrowserProfiles,
        INativeAppsWrapper? appsWrapper)
        : base(planner, listing, termination, windowService, hostGuard, debugBrowserLocator, securityProbe, processWrapper, windowWait,
            debugBrowserReadiness, debugBrowserProfiles, appsWrapper)
    {
        Planner = planner;
        Listing = listing;
        Termination = termination;
    }

    /// <summary>Plans the launches.</summary>
    public LaunchPlanner Planner { get; }

    /// <summary>list_processes and the reads of a process's name and path.</summary>
    public ProcessListingService Listing { get; }

    /// <summary>kill_process.</summary>
    public ProcessTerminationService Termination { get; }

    /// <summary><see cref="Planner"/>'s <see cref="LaunchPlanner.PlanLaunch"/>.</summary>
    public LaunchPlan PlanLaunch(
        string executable, string[]? arguments = null, string? workingDirectory = null, bool forceAccessibility = false,
        bool runAsAdmin = false, bool debugBrowserLaunch = false) =>
        Planner.PlanLaunch(executable, arguments, workingDirectory, forceAccessibility, runAsAdmin, debugBrowserLaunch);

    /// <summary>
    /// The parts wired together. <paramref name="startTimeout"/>, <paramref name="activationTimeout"/> and
    /// <paramref name="devToolsPortTimeout"/> shorten the launch's waits; left out, the production values apply.
    /// </summary>
    public static WiredProcessService Create(
        IWindowService windowService,
        IInputValidationService validationService,
        IBrowserDetectionService browserDetection,
        IExecutableResolver executableResolver,
        ITargetPolicy targetPolicy,
        IHostGuard hostGuard,
        ChromiumPrograms chromiumPrograms,
        InterpreterGuard interpreterGuard,
        DebugBrowserLocator debugBrowserLocator,
        IProcessSecurityProbe securityProbe,
        ChromiumSwitchGuard switchGuard,
        INativeProcessWrapper processWrapper,
        IWindowWaitService? windowWait = null,
        IDebugBrowserReadiness? debugBrowserReadiness = null,
        DebugBrowserProfiles? debugBrowserProfiles = null,
        INativeAppsWrapper? appsWrapper = null,
        IInstalledAppsService? installedApps = null,
        TimeSpan? startTimeout = null,
        TimeSpan? activationTimeout = null,
        TimeSpan? devToolsPortTimeout = null)
    {
        var planner = new LaunchPlanner(validationService, browserDetection, executableResolver, chromiumPrograms, interpreterGuard,
            switchGuard, securityProbe, installedApps);
        var listing = new ProcessListingService(windowService, processWrapper, targetPolicy, securityProbe);
        var termination = new ProcessTerminationService(processWrapper, targetPolicy, hostGuard);
        if (devToolsPortTimeout is not { } portTimeout)
        {
            return new WiredProcessService(planner, listing, termination, windowService, hostGuard, debugBrowserLocator, securityProbe,
                processWrapper, windowWait, debugBrowserReadiness, debugBrowserProfiles, appsWrapper)
            {
                StartTimeout = startTimeout ?? DefaultStartTimeout,
                ActivationTimeout = activationTimeout ?? DefaultActivationTimeout
            };
        }

        return new WiredProcessService(planner, listing, termination, windowService, hostGuard, debugBrowserLocator, securityProbe,
            processWrapper, windowWait, debugBrowserReadiness, debugBrowserProfiles, appsWrapper)
        {
            StartTimeout = startTimeout ?? DefaultStartTimeout,
            ActivationTimeout = activationTimeout ?? DefaultActivationTimeout,
            DevToolsPortTimeout = portTimeout
        };
    }
}
