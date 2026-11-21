using System.ComponentModel;
using System.Diagnostics;
using WinDesktopMcp.Infrastructure;
using WinDesktopMcp.Models;
using WinDesktopMcp.Models.Results;
using WinDesktopMcp.Native;
using WinDesktopMcp.Services.Interfaces;
using static WinDesktopMcp.Services.ProcessHelpers;

namespace WinDesktopMcp.Services;

public class ProcessService : IProcessService
{
    private readonly ILaunchPlanner _planner;
    private readonly IApplicationLauncher _launcher;
    private readonly IProcessListing _listing;
    private readonly IProcessTermination _termination;
    private readonly INativeProcessWrapper _processWrapper;
    private readonly IHostGuard _hostGuard;
    private readonly DebugBrowserLocator _debugBrowserLocator;
    private readonly IDebugBrowserReadiness? _debugBrowserReadiness;
    private readonly DebugBrowserProfiles _debugBrowserProfiles;

    /// <param name="planner">Plans every launch before anything starts, and previews runAsAdmin.</param>
    /// <param name="launcher">Starts the launches.</param>
    /// <param name="listing">list_processes and the reads of a process's name and path.</param>
    /// <param name="termination">kill_process.</param>
    /// <param name="processWrapper">Reads and ends a debug browser's process.</param>
    /// <param name="hostGuard">The agent's host as recorded at server start (roadmap M1.1/M1.7), whose processes a cancelled
    /// debug launch never ends.</param>
    /// <param name="debugBrowserLocator">Finds the browser for launch_browser_with_debug (roadmap M5.1).</param>
    /// <param name="debugBrowserReadiness">Confirms that a debug browser's DevTools endpoint answers before the launch
    /// reports success (roadmap M5.1); without it the launch is reported once the browser's port is known.</param>
    /// <param name="debugBrowserProfiles">Where a debug launch's new temporary profile goes, and deletes it again (roadmap
    /// M5.4); default the server's folders under %TEMP% and %LOCALAPPDATA%.</param>
    public ProcessService(
        ILaunchPlanner planner,
        IApplicationLauncher launcher,
        IProcessListing listing,
        IProcessTermination termination,
        INativeProcessWrapper processWrapper,
        IHostGuard hostGuard,
        DebugBrowserLocator debugBrowserLocator,
        IDebugBrowserReadiness? debugBrowserReadiness = null,
        DebugBrowserProfiles? debugBrowserProfiles = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _listing = listing ?? throw new ArgumentNullException(nameof(listing));
        _termination = termination ?? throw new ArgumentNullException(nameof(termination));
        _processWrapper = processWrapper ?? throw new ArgumentNullException(nameof(processWrapper));
        _hostGuard = hostGuard ?? throw new ArgumentNullException(nameof(hostGuard));
        _debugBrowserLocator = debugBrowserLocator ?? throw new ArgumentNullException(nameof(debugBrowserLocator));
        _debugBrowserReadiness = debugBrowserReadiness;
        _debugBrowserProfiles = debugBrowserProfiles ?? DebugBrowserProfiles.FromEnvironment();
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

    /// <summary>
    /// How long a debug browser started with port 0 may take, after its window wait, to write the DevToolsActivePort file
    /// that names its port (roadmap M5.1); tests shorten it.
    /// </summary>
    public TimeSpan DevToolsPortTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the <see cref="IDebugBrowserReadiness"/> check may wait for the DevTools endpoint to answer.</summary>
    public TimeSpan DebugReadinessTimeout { get; init; } = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan DevToolsPortPollInterval = TimeSpan.FromMilliseconds(100);

    /// <remarks>
    /// The steps (roadmap M5.1): find the .exe (<see cref="DebugBrowserLocator"/>); check an explicit port is free (port 0
    /// needs no check: the browser picks a free port); refuse a relative profile path, and a profile that a running browser
    /// holds (<see cref="ChromiumProfileLock"/>); delete a DevToolsActivePort file an earlier run of the profile left;
    /// start the browser with <see cref="BuildDebugBrowserArguments"/> and wait for a window of the started process only
    /// (<see cref="LaunchRequest.OnlyStartedProcess"/>); refuse a launch that handed off or did not start; for port 0, read
    /// the port from DevToolsActivePort; then let <see cref="IDebugBrowserReadiness"/>, when the server has one, confirm
    /// that the endpoint answers (<see cref="DebugBrowserReadinessProbe"/>), and report cdpReady, the browser version and
    /// the endpoints it found. A failure after the start keeps the process, window and profile in the result while the
    /// browser still runs, so that it can be closed.
    /// </remarks>
    public async Task<DebugBrowserLaunchResult> LaunchBrowserWithDebugAsync(
        DebugBrowserLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The DebugBrowser enum is the list (DebugBrowserLocator has each member's install): only a value outside it is refused
        if (DebugBrowserLocator.ExecutableName(request.Browser) == null)
        {
            return DebugLaunchFailure(
                $"Unsupported browser '{request.Browser}'. Supported browsers: {ToolEnumInfo.Get(typeof(DebugBrowser)).AllowedValues}. Nothing was launched.",
                BrowserErrorCode.UnsupportedBrowser);
        }

        var browser = ToolEnum<DebugBrowser>.Name(request.Browser);
        var location = _debugBrowserLocator.Locate(request.Browser, request.ExecutablePath);
        if (location.Path == null)
        {
            return DebugLaunchFailure(BrowserNotFoundMessage(request.Browser, request.ExecutablePath, location.Tried), BrowserErrorCode.BrowserNotFound);
        }

        var port = request.Port;
        if (port != DebugBrowserLaunchRequest.EphemeralPort && IsPortInUse(port))
        {
            return DebugLaunchFailure(
                $"Port {port} is already in use. Nothing was launched. Leave port out (0) to let the browser pick a free port, or choose another one.",
                BrowserErrorCode.PortInUse);
        }

        if (request.UserDataDir is { } given && !Path.IsPathFullyQualified(given))
        {
            // The server would read DevToolsActivePort relative to its own current directory, the browser would take the
            // folder relative to its install folder
            return DebugLaunchFailure(
                $"userDataDir must be a full path, but '{given}' is not. Nothing was launched.", ToolErrorReporting.InvalidArgumentsErrorCode);
        }

        var temporaryProfile = request.UserDataDir == null;
        var userDataDir = request.UserDataDir ?? _debugBrowserProfiles.NewTemporaryPath();

        // Before anything is deleted or started: a start would hand the url to the browser holding the profile, and the
        // port file deleted below would be that browser's
        if (ChromiumProfileLock.HeldReason(userDataDir) is { } held)
        {
            return DebugLaunchFailure(
                $"A browser is already running with this userDataDir: it holds the profile's {ChromiumProfileLock.FileName} ({held}). " +
                "Nothing was launched and nothing in the profile was changed; a start would only hand the url to that browser, " +
                $"which ignores the debug switches. {CloseOnlyYourOwnAdvice(userDataDir)} Otherwise leave userDataDir out to use a new temporary profile.",
                BrowserErrorCode.DebugLaunchFailed) with { UserDataDir = userDataDir };
        }

        if (DevToolsActivePortFile.DeleteStale(userDataDir) is { } notDeleted)
        {
            // Chromium writes the file and closes it, so a running browser does not stop the delete
            return DebugLaunchFailure(
                $"The {DevToolsActivePortFile.FileName} file an earlier run left in '{userDataDir}' could not be deleted ({notDeleted}), " +
                "so the port read from it could be that run's. Nothing was launched. Check the file's attributes and the folder's " +
                "permissions, or leave userDataDir out to use a new temporary profile.",
                BrowserErrorCode.DebugLaunchFailed);
        }

        if (temporaryProfile)
        {
            // The server's own folder (roadmap M5.4): its path proves to close_debug_browser that the browser is the server's
            try
            {
                Directory.CreateDirectory(userDataDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return DebugLaunchFailure(
                    $"The temporary profile folder '{userDataDir}' could not be created ({ex.Message}). Nothing was launched.",
                    BrowserErrorCode.DebugLaunchFailed);
            }
        }

        var arguments = BuildDebugBrowserArguments(port, userDataDir, request.Url);

        // The started browser, from the moment the start returns its PID (roadmap M5.4): a call cancelled after that closes it
        int? startedProcessId = null;
        DateTime? startedAt = null;

        // A start still running when the call was cancelled: its browser is ended when it comes
        Task<IProcessHandle?>? abandonedStart = null;

        try
        {
            var launch = await _launcher.LaunchApplicationAsync(
                new LaunchRequest(location.Path, arguments.ToArray())
                {
                    OnlyStartedProcess = true,
                    DebugBrowserLaunch = true,
                    OnStarted = pid =>
                    {
                        // Read at once, so that a failure or a cancellation below can tell the browser from a process
                        // that got its PID after it exited
                        startedAt = _processWrapper.GetProcessStartTime(pid);
                        startedProcessId = pid;
                    },
                    OnStartAbandoned = start => abandonedStart = start
                },
                waitForWindow: true,
                request.WaitTimeoutMs,
                cancellationToken).ConfigureAwait(false);

            if (!launch.Success)
            {
                return await WithoutLeftoverProfileAsync(
                    DebugLaunchFailure(launch.ErrorMessage ?? "Failed to launch browser", launch.ErrorCode ?? nameof(BrowserErrorCode.DebugLaunchFailed)) with
                    {
                        Warning = launch.Warning
                    },
                    userDataDir, temporaryProfile).ConfigureAwait(false);
            }

            if (NotStartedReason(browser, launch, temporaryProfile, CloseOnlyYourOwnAdvice(userDataDir)) is { } notStarted)
            {
                // Only the launch's own warning: launch_application's note for a launch without a window advises its
                // expectWindow, which this tool does not have. A consent prompt may still start the browser with the
                // profile, so the profile is kept then
                var notStartedResult = new DebugBrowserLaunchResult(false, null, null, null, userDataDir, notStarted,
                    nameof(BrowserErrorCode.DebugLaunchFailed), WithoutNoWindowNote(launch, request.WaitTimeoutMs), launch.Status);
                return launch.Status == LaunchStatus.AwaitingUserConsent
                    ? notStartedResult
                    : await WithoutLeftoverProfileAsync(notStartedResult, userDataDir, temporaryProfile).ConfigureAwait(false);
            }

            var processId = launch.ProcessId!.Value;
            var startTime = startedProcessId == processId ? startedAt : _processWrapper.GetProcessStartTime(processId);
            var started = new DebugBrowserLaunchResult(true, processId, launch.WindowHandle, port, userDataDir, null, null,
                launch.Warning, launch.Status) { StartTime = startTime };
            if (port == DebugBrowserLaunchRequest.EphemeralPort)
            {
                if (await WaitForDevToolsPortAsync(userDataDir, cancellationToken).ConfigureAwait(false) is not { } reportedPort)
                {
                    return await FailedAfterStartAsync(started with { CdpPort = null }, startTime, temporaryProfile,
                        $"The browser started (processId {processId}) but did not write its DevTools port to " +
                        $"'{DevToolsActivePortFile.PathIn(userDataDir)}' within {DevToolsPortTimeout.TotalSeconds:0.#} seconds, so " +
                        "the port is not known.").ConfigureAwait(false);
                }

                started = started with { CdpPort = reportedPort };
            }

            if (_debugBrowserReadiness != null)
            {
                DebugBrowserReadiness readiness;
                try
                {
                    readiness = await _debugBrowserReadiness.WaitUntilReadyAsync(
                        new DebugBrowserEndpoint(browser, processId, started.CdpPort!.Value, userDataDir, startTime),
                        DebugReadinessTimeout,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // The hook reports, it does not throw; one that does (its own timeout, a refused connection) still
                    // leaves a running browser that the result must name
                    readiness = new DebugBrowserReadiness(Ready: false, Error: $"The DevTools endpoint check failed: {ex.Message}");
                }

                if (!readiness.Ready)
                {
                    // A port another process holds, or one whose answer nothing ties to this browser, is not offered as
                    // its cdpPort: a CDP client pointed at it would control whatever listens there
                    var notTheBrowsers = readiness.Reason is DebugBrowserNotReadyReason.PortHeldByOtherProcess
                        or DebugBrowserNotReadyReason.PortOwnerUnknown;
                    return await FailedAfterStartAsync(notTheBrowsers ? started with { CdpPort = null } : started, startTime, temporaryProfile,
                        readiness.Error ?? "The DevTools endpoint did not answer.").ConfigureAwait(false);
                }

                started = started with
                {
                    CdpReady = true,
                    BrowserVersion = readiness.BrowserVersion,
                    CdpEndpoint = readiness.CdpEndpoint,
                    BrowserWsEndpoint = readiness.BrowserWebSocketUrl
                };
            }

            return started;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Nobody learns this browser's PID or port, and its debug port has no authentication: close it (roadmap M5.4)
            if (abandonedStart != null)
            {
                // The start still runs: its browser may still start with the profile, so neither is touched before it returns.
                // Not awaited: the call (and the input gate it holds) does not wait for a start that may take minutes
                _ = EndAbandonedStartAsync(abandonedStart, userDataDir, temporaryProfile);
            }
            else
            {
                await AbandonCancelledLaunchAsync(startedProcessId, startedAt, userDataDir, temporaryProfile).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            return await WithoutLeftoverProfileAsync(
                DebugLaunchFailure(ex.Message, BrowserErrorCode.DebugLaunchFailed), userDataDir, temporaryProfile).ConfigureAwait(false);
        }
    }

    /// <summary>How long a debug launch waits for a browser it ends to exit, and for its temporary profile to be deleted.</summary>
    private static readonly TimeSpan AbandonTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A launch_browser_with_debug call cancelled after the start (roadmap M5.4): the browser it started is ended, since no
    /// result names its PID or port and its debug port has no authentication, and its temporary profile is deleted. Only
    /// that browser: the PID must still have the start time read when it started (or, when that could not be read, the
    /// browser's own command line must name the launch's profile), checked while the opened process holds a handle that pins
    /// its PID until it is ended (review F4). And only what it provably started, as close_debug_browser ends it
    /// (<see cref="DebugBrowserProcesses"/>): the browser process alone, then what is left of its own child processes, never
    /// its process tree, which holds what the browser opened (a download's program, the user's own browser that a link
    /// started). Best effort; never throws.
    /// </summary>
    private async Task AbandonCancelledLaunchAsync(int? processId, DateTime? startTime, string userDataDir, bool temporaryProfile)
    {
        if (processId is int pid)
        {
            try
            {
                using var process = _processWrapper.OpenProcess(pid);
                if (process != null && !HasExitedSafe(process) && IsStartedDebugBrowser(pid, startTime, userDataDir))
                {
                    await new DebugBrowserProcesses(_processWrapper, _hostGuard)
                        .EndStartedBrowserAsync(process, pid, userDataDir, AbandonTimeout).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
            {
                // It exited meanwhile, or cannot be ended; nothing else to do
            }
        }

        if (temporaryProfile)
        {
            await _debugBrowserProfiles.DeleteAsync(userDataDir, AbandonTimeout).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A launch_browser_with_debug call cancelled while its start still ran (roadmap M5.4): once the start returns, the
    /// browser it brought is ended (the handle is the started process's own, so it cannot be another process that got the
    /// PID), as close_debug_browser ends it (<see cref="DebugBrowserProcesses"/>): the browser process alone, then what is
    /// left of its own child processes, never a program it opened. Only then is the temporary profile deleted. Best effort;
    /// never throws.
    /// </summary>
    private async Task EndAbandonedStartAsync(Task<IProcessHandle?> start, string userDataDir, bool temporaryProfile)
    {
        try
        {
            IProcessHandle? process;
            try
            {
                process = await start.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                process = null; // Nothing was started
            }

            if (process != null)
            {
                using (process)
                {
                    if (!HasExitedSafe(process))
                    {
                        await new DebugBrowserProcesses(_processWrapper, _hostGuard)
                            .EndStartedBrowserAsync(process, process.Id, userDataDir, AbandonTimeout).ConfigureAwait(false);
                    }
                }
            }

            if (temporaryProfile)
            {
                await _debugBrowserProfiles.DeleteAsync(userDataDir, AbandonTimeout).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException
                                       or IOException or UnauthorizedAccessException)
        {
            // It exited meanwhile, or cannot be ended; a temporary profile left is swept by a later launch
        }
    }

    /// <summary>
    /// Whether PID <paramref name="processId"/> is still the browser this launch started: the start time read at its start,
    /// or when that was not read, a command line that names <paramref name="userDataDir"/>.
    /// </summary>
    private bool IsStartedDebugBrowser(int processId, DateTime? startTime, string userDataDir)
    {
        if (startTime != null)
            return _processWrapper.GetProcessStartTime(processId) == startTime;

        var switches = ChromiumCommandLine.Parse(_processWrapper.GetProcessCommandLine(processId));
        return switches.TryGetValue("user-data-dir", out var profile) && DebugBrowserProfiles.SamePath(profile, userDataDir);
    }

    /// <summary>
    /// A failed debug launch whose browser is not running (no processId): its new temporary profile is deleted, since no
    /// browser will use it (roadmap M5.4), and the result names no profile folder. Any other result is returned as it is.
    /// </summary>
    private async Task<DebugBrowserLaunchResult> WithoutLeftoverProfileAsync(DebugBrowserLaunchResult result, string userDataDir, bool temporaryProfile)
    {
        if (!temporaryProfile || result.ProcessId != null)
            return result;

        return await _debugBrowserProfiles.DeleteAsync(userDataDir, AbandonTimeout).ConfigureAwait(false) == null
            ? result with { UserDataDir = null }
            : result;
    }

    private static DebugBrowserLaunchResult DebugLaunchFailure(string error, BrowserErrorCode errorCode) =>
        DebugLaunchFailure(error, errorCode.ToString());

    private static DebugBrowserLaunchResult DebugLaunchFailure(string error, string errorCode) =>
        new(Success: false, ProcessId: null, WindowHandle: null, CdpPort: null, UserDataDir: null, Error: error, ErrorCode: errorCode);

    private static string BrowserNotFoundMessage(DebugBrowser browser, string? executablePath, IReadOnlyList<string> tried) =>
        string.IsNullOrWhiteSpace(executablePath)
            ? $"Browser '{ToolEnum<DebugBrowser>.Name(browser)}' was not found. Tried: {string.Join("; ", tried)}. Nothing was launched. If it is installed " +
              $"elsewhere (another channel or a portable copy), pass the full path of its {DebugBrowserLocator.ExecutableName(browser)} as executablePath."
            : $"executablePath '{executablePath.Trim()}' does not exist. Nothing was launched.";

    /// <summary>
    /// What a failure says about closing a browser. A result that names no process gives no safe way to find "that
    /// browser": one found by name or title may be the user's own. close_debug_browser closes a browser on a userDataDir
    /// the agent passed only while this server run started it (a named profile is the server's own folder, so it always can).
    /// </summary>
    private string CloseOnlyYourOwnAdvice(string userDataDir) => _debugBrowserProfiles.KindOf(userDataDir) == DebugProfileKind.Custom
        ? "If it is a debug browser started with this userDataDir, launch again with reuseIfRunning=true to use it. " +
          "close_debug_browser closes it only if this server started it; otherwise ask the user to close it, and never close a " +
          "browser you did not start."
        : "If it is a debug browser started with this profile, launch again with reuseIfRunning=true to use it, or close it " +
          "with close_debug_browser; never close a browser you did not start.";

    /// <summary>
    /// Why a launch that Windows accepted did not start a debug browser of its own, or null when it did: the started
    /// process is running (with its window, or still without one when the wait ended). The window wait counted only the
    /// started process's windows, so a launch whose request went to a browser already running with the same profile ends
    /// with the launcher gone and no window (that browser ignores the debug switches of the request); so does a browser
    /// that could not start with the profile. The profile lock is checked before the start, so the first is a browser
    /// that took the profile meanwhile; with a new temporary profile it cannot be.
    /// </summary>
    private static string? NotStartedReason(string browser, LaunchResult launch, bool temporaryProfile, string closeAdvice) => launch.Status switch
    {
        LaunchStatus.ExitedWithoutWindow or LaunchStatus.PossibleHandoff when temporaryProfile =>
            $"The {browser} process that was started exited without a window of its own, so no debug port was opened. No " +
            "other browser can use its new temporary profile, so the browser could not start (for example a policy, or a " +
            "crash). Ask the user before trying again.",
        LaunchStatus.ExitedWithoutWindow or LaunchStatus.PossibleHandoff =>
            $"The {browser} process that was started exited without a window of its own, so no debug port was opened. Either " +
            $"a {browser} already running with this userDataDir took the request over (it ignores the debug switches, and has " +
            "most likely opened the url in a new window of its own), or the browser could not start with this profile (for " +
            $"example a folder it cannot write, a policy, or a crash). {closeAdvice} Otherwise leave userDataDir " +
            "out to use a new temporary profile.",
        LaunchStatus.AwaitingUserConsent =>
            "Windows is showing a consent or security prompt for the browser's start (see warning), so it is not known " +
            "whether it starts or which port it gets. Ask the user to answer the prompt; do not launch it again meanwhile.",
        _ when launch.ProcessId == null => "Windows started no browser process, so no debug port was opened.",
        _ => null
    };

    /// <summary><paramref name="launch"/>'s warning without the note <see cref="ApplicationLauncher.NoWindowNote"/> added to it.</summary>
    private static string? WithoutNoWindowNote(LaunchResult launch, int waitTimeoutMs)
    {
        if (launch.Status is not { } status || ApplicationLauncher.NoWindowNote(status, waitTimeoutMs) is not { } note || launch.Warning is not { } warning)
            return launch.Warning;

        if (warning == note)
            return null;

        return warning.EndsWith("; " + note, StringComparison.Ordinal) ? warning[..^(note.Length + 2)] : warning;
    }

    /// <summary>
    /// A failure after the browser started: it keeps the process, window and port (when <paramref name="started"/> has
    /// one) while that process still runs (its start time is the one read after the start), so that it can be closed;
    /// once it exited, its PID may be another process's, so the result names none. Either way the endpoint is not ready
    /// (cdpReady false).
    /// </summary>
    private async Task<DebugBrowserLaunchResult> FailedAfterStartAsync(
        DebugBrowserLaunchResult started, DateTime? startTime, bool temporaryProfile, string error)
    {
        var processId = started.ProcessId!.Value;
        var stillRunning = _processWrapper.GetProcessStartTime(processId) is { } now && (startTime == null || now == startTime);
        return stillRunning
            ? started with
            {
                Success = false,
                Error = $"{error} The browser (processId {processId}) is still running: close it with close_debug_browser and that processId.",
                ErrorCode = nameof(BrowserErrorCode.DebugLaunchFailed),
                CdpReady = false
            }
            : await WithoutLeftoverProfileAsync(started with
            {
                Success = false,
                ProcessId = null,
                WindowHandle = null,
                CdpPort = null,
                StartTime = null,
                Error = $"{error} The browser (processId {processId}) is no longer running, so there is nothing to close.",
                ErrorCode = nameof(BrowserErrorCode.DebugLaunchFailed),
                CdpReady = false
            }, started.UserDataDir!, temporaryProfile).ConfigureAwait(false);
    }

    /// <summary>
    /// The port a browser started with port 0 wrote to its profile's DevToolsActivePort, polled for at most
    /// <see cref="DevToolsPortTimeout"/>; null when it did not appear. The file of an earlier run was deleted before the
    /// start, so a file here is this launch's.
    /// </summary>
    private async Task<int?> WaitForDevToolsPortAsync(string userDataDir, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + DevToolsPortTimeout;
        while (true)
        {
            if (DevToolsActivePortFile.TryReadPort(userDataDir) is { } port)
            {
                return port;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return null;
            }

            await Task.Delay(left < DevToolsPortPollInterval ? left : DevToolsPortPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The switches every debug launch passes first (roadmap M5.1), so that a new profile opens the url rather than a
    /// screen that asks the user something; such a screen would also be the first tab, which the window title and the CDP
    /// tab match would land on. Chrome, Edge and Brave are all Chromium and read the same switches.
    /// </summary>
    public static readonly IReadOnlyList<string> FirstRunSwitches =
    [
        // No first-run experience: Chrome's welcome and "make Chrome your own" pages, Edge's welcome, sign-in and
        // personalization pages, Brave's welcome tour, and the offer to import another browser's data
        "--no-first-run",
        // No bar or dialog that offers to make this browser the user's default browser
        "--no-default-browser-check",
        // No search engine choice dialog (Chrome 120 and later, in some regions) over the first window of a new profile
        "--disable-search-engine-choice-screen",
        // No sync: a browser sign-in (by the user, or Edge's own with the Windows account) would otherwise copy the account's
        // passwords, history and other data into a profile that the unauthenticated debug port exposes. It does not stop
        // the sign-in itself: Edge may still sign in to Microsoft sites with the Windows account (not checked on a desktop yet)
        "--disable-sync"
    ];

    /// <summary>
    /// Builds the argument list for a CDP debug browser launch: <see cref="FirstRunSwitches"/>, the port (0: the browser
    /// picks one and writes it to DevToolsActivePort), the profile and the url. The profile is never the browser's default
    /// one (launch_browser_with_debug refuses a 'User Data' folder): Chrome 136 and later ignore --remote-debugging-port on
    /// the default profile. Values are left unquoted: <see cref="CommandLineBuilder"/> quotes each argument once when the
    /// command line is built, so user data directories containing spaces or a trailing backslash stay a single argument.
    /// </summary>
    public static IReadOnlyList<string> BuildDebugBrowserArguments(int port, string userDataDir, string? url)
    {
        var arguments = new List<string>(FirstRunSwitches)
        {
            $"--remote-debugging-port={port}",
            $"--user-data-dir={userDataDir}"
        };

        if (!string.IsNullOrWhiteSpace(url))
        {
            arguments.Add(url);
        }

        return arguments;
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return true;
        }
    }
}
