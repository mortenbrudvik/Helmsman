using System.ComponentModel;
using WinDesktopMcp.Models;
using WinDesktopMcp.Models.Results;
using WinDesktopMcp.Native;
using WinDesktopMcp.Services.Interfaces;
using static WinDesktopMcp.Services.LaunchDocuments;
using static WinDesktopMcp.Services.ProcessHelpers;

namespace WinDesktopMcp.Services;

/// <summary>
/// Starts what <see cref="ILaunchPlanner"/> planned (<see cref="IApplicationLauncher"/>): launch_application,
/// launch_applications_batch, apply_layout's and restore_workspace's launches and launch_browser_with_debug's start. Every
/// request is planned first; then reuseExisting looks for an open window (<see cref="ReuseWindowFinder"/>), the program is
/// started or a packaged app activated, and the window it opens is waited for (<see cref="IWindowWaitService"/>).
/// </summary>
public sealed class ApplicationLauncher : IApplicationLauncher
{
    private readonly ILaunchPlanner _planner;
    private readonly IWindowService _windowService;
    private readonly INativeProcessWrapper _processWrapper;
    private readonly IWindowWaitService _windowWait;
    private readonly IProcessSecurityProbe _securityProbe;
    private readonly INativeAppsWrapper? _appsWrapper;
    private readonly ReuseWindowFinder _reuse;

    /// <param name="planner">Plans every request before anything starts.</param>
    /// <param name="windowService">Reads the windows: reuseExisting's open windows and a started process's main window.</param>
    /// <param name="securityProbe">Reads the server's own integrity (an elevated server cannot activate packaged apps).</param>
    /// <param name="processWrapper">Starts the programs and reads the processes; default the real ones.</param>
    /// <param name="windowWait">
    /// Finds the window a launch opens (roadmap M2.6). The server always passes it; without it a wait reads the real
    /// desktop through a <see cref="NativeWindowWrapper"/> created here (eagerly, so that the E2E reference walk sees it).
    /// </param>
    /// <param name="appsWrapper">Activates packaged apps and reads which package a process belongs to (roadmap M6.5). The
    /// server always passes it; without it a packaged app is started through the shell, as before, without a process ID.</param>
    public ApplicationLauncher(
        ILaunchPlanner planner,
        IWindowService windowService,
        IProcessSecurityProbe securityProbe,
        INativeProcessWrapper? processWrapper = null,
        IWindowWaitService? windowWait = null,
        INativeAppsWrapper? appsWrapper = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _windowService = windowService;
        _processWrapper = processWrapper ?? new NativeProcessWrapper();
        _windowWait = windowWait ?? new WindowWaitService(new NativeWindowWrapper(), windowService, _processWrapper);
        _securityProbe = securityProbe ?? throw new ArgumentNullException(nameof(securityProbe));
        _appsWrapper = appsWrapper;
        _reuse = new ReuseWindowFinder(windowService, _processWrapper, appsWrapper);
    }

    /// <summary>Whether the server itself runs elevated; false when that cannot be read.</summary>
    private bool ServerIsElevated() => ProcessHelpers.ServerIsElevated(_securityProbe);

    /// <summary>
    /// How long a start may take before the launch reports <see cref="LaunchStatus.AwaitingUserConsent"/>: ShellExecute does
    /// not return while Windows asks the user something (a UAC prompt for an elevated program, SmartScreen, a security
    /// warning, an Open with dialog), which can take any time.
    /// </summary>
    public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a start may take (default <see cref="DefaultStartTimeout"/>); tests shorten it.</summary>
    public TimeSpan StartTimeout { get; init; } = DefaultStartTimeout;

    /// <summary>ERROR_CANCELLED: the user declined or cancelled a prompt of the start (most likely UAC).</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// How long a packaged app's activation may take (roadmap M6.5): IApplicationActivationManager returns once the app is
    /// running, and a cold start with a splash screen takes seconds. Within the launch tools' 30-second cap.
    /// </summary>
    public static readonly TimeSpan DefaultActivationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long an activation may take (default <see cref="DefaultActivationTimeout"/>); tests shorten it.</summary>
    public TimeSpan ActivationTimeout { get; init; } = DefaultActivationTimeout;

    /// <summary>
    /// The HRESULTs with which Windows refuses to activate a packaged app from this server's process, though the shell
    /// can start it (roadmap M6.5), as winerror.h defines them (Windows SDK 10.0.22621): E_ELEVATED_ACTIVATION_NOT_SUPPORTED
    /// 0x80270251 ("This app can't be activated from an elevated context"), E_UAC_DISABLED 0x80270252 ("... when UAC is
    /// disabled") and E_FULL_ADMIN_NOT_SUPPORTED 0x80270253 ("... by the Built-in Administrator").
    /// </summary>
    private static readonly HashSet<int> ActivationRefusedForElevation =
        [unchecked((int)0x80270251), unchecked((int)0x80270252), unchecked((int)0x80270253)];

    /// <summary>
    /// E_APPLICATION_NOT_REGISTERED (winerror.h): "This app does not support the contract specified or is not installed".
    /// </summary>
    private const int ApplicationNotRegistered = unchecked((int)0x80270254);

    /// <summary>
    /// How many activations may be stuck at once (review M6.5 #5): each one that does not return keeps its start thread and
    /// the wrapper's STA thread until Windows answers, so at this many the next activation is refused at once.
    /// </summary>
    public const int MaxHungActivations = 3;

    /// <summary>Activations that missed <see cref="ActivationTimeout"/> and have not returned yet.</summary>
    private int _hungActivations;

    /// <summary>Activations that missed <see cref="ActivationTimeout"/> and have not returned yet.</summary>
    public int HungActivations => Volatile.Read(ref _hungActivations);

    public Task<LaunchResult> LaunchApplicationAsync(
        string executable,
        string[]? arguments = null,
        string? workingDirectory = null,
        bool waitForWindow = false,
        int waitTimeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        LaunchApplicationAsync(new LaunchRequest(executable, arguments, workingDirectory), waitForWindow, waitTimeoutMs, cancellationToken);

    public async Task<LaunchResult> LaunchApplicationAsync(
        LaunchRequest request,
        bool waitForWindow,
        int waitTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = _planner.PlanFor(request);
        if (!plan.IsValid)
        {
            return Rejected(plan);
        }

        // A call cancelled before the start starts nothing
        cancellationToken.ThrowIfCancellationRequested();

        if (request.ReuseExisting)
        {
            var reuse = _reuse.FindOpenWindow(plan, request, taken: null, openBefore: null);
            if (reuse.Result is { } alreadyOpen)
            {
                return Finish(alreadyOpen, plan);
            }

            plan = plan with { Warning = CombineWarnings(plan.Warning, reuse.Note) };
        }

        plan = plan with { RunningInstance = RunningInstanceOf(plan) };
        var launch = await StartAsync(plan, waitForWindow, cancellationToken, request.OnStartAbandoned).ConfigureAwait(false);
        if (launch.Outcome is { } ended)
        {
            return Finish(ended, plan);
        }

        if (launch.Process != null)
        {
            request.OnStarted?.Invoke(launch.Process.Id);
        }

        using (launch.Process)
        {
            // Cancelled while waiting for the window: the program was started and keeps running, and the call throws
            // (launch_browser_with_debug closes its browser then, through LaunchRequest.OnStarted)
            if (!waitForWindow)
            {
                return Finish(StartedWithoutWait(launch), plan);
            }

            var owner = OwnerOf(launch, request);
            return Finish(
                await WaitForWindowAsync(launch, request, owner, owner.Matches, waitTimeoutMs, tryClaim: null, cancellationToken)
                    .ConfigureAwait(false),
                plan);
        }
    }

    /// <summary>What a launch reports besides its own outcome: whether forceAccessibility took effect, and an elevated start.</summary>
    private LaunchResult Finish(LaunchResult result, LaunchPlan plan) => WithElevationNote(WithForceAccessibility(result, plan), plan);

    /// <summary>
    /// runAsAdmin (roadmap M6.5): a program started as administrator runs above a server that is not elevated, so the input
    /// guard refuses input and element actions to its windows (TargetElevated), and Windows drops the server's close
    /// requests to them. Only when the start was handed to Windows.
    /// </summary>
    private LaunchResult WithElevationNote(LaunchResult result, LaunchPlan plan)
    {
        if (!result.Success || plan.StartInfo?.Verb != RunAsVerb || ServerIsElevated())
            return result;

        return result with
        {
            Warning = CombineWarnings(result.Warning,
                "runAsAdmin: once the user approves, the program runs as administrator, above this server, so input and element " +
                "actions to its windows are refused (TargetElevated) and close requests do not reach them. Ask the user to close it.")
        };
    }

    /// <summary>What to do so that --force-renderer-accessibility takes effect, when an instance of the app runs.</summary>
    private const string ForceAccessibilityAdvice =
        "To apply it, no process of the app may be left, not only no window: Edge's Startup boost and Chrome's background " +
        "apps keep one running without a window (list_processes with includeWindowless: true lists them; ask the user " +
        "before ending one). Then launch it again.";

    /// <summary>
    /// Says whether forceAccessibility took effect (roadmap M3.3), when it was asked for and the request was valid: false
    /// when nothing was started (reuseExisting found an open window) or the start failed. The flag only matters when the
    /// app's main process starts with it: when the launch went to an instance that was already running (a handoff, a
    /// reused window or a new tab, or a launcher that exited), that instance keeps the mode it started with, and the result
    /// warns about it. A launch that did not wait cannot tell yet, so it warns when a process of the program was running
    /// before the start (<see cref="LaunchPlan.RunningInstance"/>): the new process most likely hands off to it.
    /// </summary>
    private static LaunchResult WithForceAccessibility(LaunchResult result, LaunchPlan plan)
    {
        if (plan.ForceAccessibilityApplied is not { } applied)
        {
            return result;
        }

        if (!applied || !result.Success)
        {
            return result with { ForceAccessibilityApplied = false };
        }

        if (result.Status == LaunchStatus.AlreadyOpen)
        {
            return result with
            {
                ForceAccessibilityApplied = false,
                Warning = CombineWarnings(result.Warning,
                    "forceAccessibility was not applied: reuseExisting used an open window, so nothing was started.")
            };
        }

        var handedOff = result.Status is LaunchStatus.PossibleHandoff or LaunchStatus.ReusedExistingWindow or LaunchStatus.OpenedInTab ||
                        result.LauncherExited == true;
        var note = handedOff
            ? $"{ChromiumPrograms.ForceRendererAccessibility} was passed, but the launch went to an instance of the app that was " +
              $"already running, which keeps the accessibility mode it started with. {ForceAccessibilityAdvice}"
            : result.Status == LaunchStatus.Started && plan.RunningInstance is { } running
                ? $"{ChromiumPrograms.ForceRendererAccessibility} was passed, but the app was already running (process {running}, " +
                  "with or without a window), so the new process most likely hands its request to that instance and exits, and " +
                  $"the instance keeps the accessibility mode it started with. {ForceAccessibilityAdvice}"
                : null;
        return result with
        {
            ForceAccessibilityApplied = true,
            Warning = CombineWarnings(result.Warning, note)
        };
    }

    /// <summary>
    /// With forceAccessibility applied: a process of the program that is running before the start (its image path is the
    /// program's), from one process snapshot, or null when none is or the list cannot be read. Only reads.
    /// </summary>
    private int? RunningInstanceOf(LaunchPlan plan)
    {
        if (plan.ForceAccessibilityApplied != true || plan.Target is not { } target || !Path.IsPathFullyQualified(target.FileName))
            return null;

        var imageName = Path.GetFileName(target.FileName);
        foreach (var (processId, entry) in _processWrapper.GetProcessList() ?? new Dictionary<int, (int, string)>())
        {
            if (string.Equals(entry.ImageName, imageName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_processWrapper.GetProcessImagePath(processId), target.FileName, StringComparison.OrdinalIgnoreCase))
                return processId;
        }

        return null;
    }

    private static LaunchResult Rejected(LaunchPlan plan) =>
        new(false, ErrorMessage: plan.ErrorMessage, ErrorCode: plan.ErrorCode, Warning: plan.Warning);

    /// <summary>
    /// A launch whose start was requested: the started process (null when Windows started none), the windows before the
    /// start (with a window wait) and when it was requested; or, when it ended there, its <see cref="Outcome"/>.
    /// </summary>
    private sealed record StartedLaunch(
        LaunchPlan Plan, IProcessHandle? Process, WindowBaseline? Baseline, DateTime LaunchedAtUtc, LaunchResult? Outcome = null);

    /// <summary>
    /// Starts a planned launch. The start runs on a thread of its own and is waited for at most <see cref="StartTimeout"/>:
    /// ShellExecute blocks while Windows asks the user to approve an elevated program, so a longer start is reported as
    /// <see cref="LaunchStatus.AwaitingUserConsent"/> (the program starts later if the user approves; its handle is then
    /// released). A declined prompt is <see cref="LaunchErrorCode.ElevationCancelled"/>, any other failure
    /// <see cref="LaunchErrorCode.LaunchException"/>. A call cancelled while the start still runs hands the start to
    /// <paramref name="onAbandonedStart"/> when there is one (which then owns the process handle it brings), and throws.
    /// A packaged app (roadmap M6.5) is activated instead (<see cref="Activate"/>), for at most <see cref="ActivationTimeout"/>,
    /// and an activation that takes longer is <see cref="LaunchStatus.TimedOut"/>; from an elevated server, which Windows does
    /// not let activate an app, it is started through the shell, without a process ID.
    /// </summary>
    private async Task<StartedLaunch> StartAsync(
        LaunchPlan plan, bool takeBaseline, CancellationToken cancellationToken, Action<Task<IProcessHandle?>>? onAbandonedStart = null)
    {
        var activate = plan.Target is { Kind: LaunchTargetKind.PackagedApp, App: not null } && _appsWrapper != null;
        if (activate && HungActivations >= MaxHungActivations)
        {
            return new StartedLaunch(plan, null, null, DateTime.UtcNow, new LaunchResult(false,
                ErrorMessage: $"{MaxHungActivations} earlier activations of packaged apps are still waiting for Windows to answer, so " +
                              "no other is started now. Nothing was started. Look for the apps' windows with find_windows, and try " +
                              "again once they have opened (or ask the user to check them).",
                ErrorCode: nameof(LaunchErrorCode.ActivationFailed), Warning: plan.Warning));
        }

        string? startNote = null;
        if (activate && ServerIsElevated())
        {
            activate = false;
            startNote = "The server runs elevated, and Windows does not activate a packaged app from an elevated process, so the " +
                        "app was started through the shell: its processId is not known (the app runs at the user's normal level, " +
                        "and its window is found by its package).";
        }

        var baseline = takeBaseline ? _windowWait.TakeBaseline() : null;
        var launchedAt = DateTime.UtcNow;
        // On a thread of its own, not the thread pool: a start that waits for the user's answer to a UAC prompt can take
        // minutes, and a busy thread pool must not delay a start into looking like one
        Func<IProcessHandle?> startCall = activate
            ? () => Activate(plan, note => startNote = note)
            : () => _processWrapper.StartProcess(plan.StartInfo!);
        var start = Task.Factory.StartNew(startCall, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var timeout = activate ? ActivationTimeout : StartTimeout;
        Task finished;
        using (var stopTimer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            finished = await Task.WhenAny(start, Task.Delay(timeout, stopTimer.Token)).ConfigureAwait(false);
            stopTimer.Cancel();
        }

        if (finished != start)
        {
            if (activate)
            {
                // Counted until Windows answers: a stuck activation keeps two threads. Counted before a cancellation throws,
                // so a caller that cancels each launch against a hung shell still meets MaxHungActivations
                Interlocked.Increment(ref _hungActivations);
                _ = start.ContinueWith(
                    _ => Interlocked.Decrement(ref _hungActivations),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            if (cancellationToken.IsCancellationRequested && onAbandonedStart != null)
            {
                onAbandonedStart(start);
                cancellationToken.ThrowIfCancellationRequested();
            }

            ReleaseWhenStarted(start);
            cancellationToken.ThrowIfCancellationRequested();
            if (activate)
            {
                return new StartedLaunch(plan, null, null, launchedAt, new LaunchResult(
                    Success: true,
                    Warning: CombineWarnings(plan.Warning,
                        $"The activation of the app did not return within {timeout.TotalSeconds:0.#} seconds: it may still be " +
                        "starting (a cold start with a splash screen) or be stuck, and its process ID is not known. Look for its " +
                        "window with find_windows; do not launch it again."),
                    Status: LaunchStatus.TimedOut));
            }

            return new StartedLaunch(plan, null, null, launchedAt, new LaunchResult(
                Success: true,
                Warning: CombineWarnings(plan.Warning,
                    $"The start did not return within {StartTimeout.TotalSeconds:0.#} seconds: Windows is most likely showing a consent " +
                    "or security prompt (UAC, SmartScreen, a security warning for a downloaded or network file, or 'How do you want to " +
                    "open this file?'). Only the user may answer it: do not click or type into it. The program starts only if the user " +
                    "approves, its process ID is not known, and a later decline is not reported. Ask the user to answer the prompt, then " +
                    "look for its window with find_windows; do not launch it again."),
                Status: LaunchStatus.AwaitingUserConsent));
        }

        try
        {
            var process = await start.ConfigureAwait(false);
            return new StartedLaunch(plan with { Warning = CombineWarnings(plan.Warning, startNote) }, process, baseline, launchedAt);
        }
        catch (ActivationFailedException ex)
        {
            return new StartedLaunch(plan, null, null, launchedAt, new LaunchResult(false,
                ErrorMessage: ActivationFailureMessage(plan.Target!.App!, ex.Result),
                ErrorCode: nameof(LaunchErrorCode.ActivationFailed), Warning: plan.Warning));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new StartedLaunch(plan, null, null, launchedAt, new LaunchResult(false,
                ErrorMessage: "The user declined or cancelled a prompt Windows showed for the start (most likely a UAC prompt for a " +
                              "program that needs administrator rights). Nothing was started. Ask the user before trying again.",
                ErrorCode: nameof(LaunchErrorCode.ElevationCancelled), Warning: plan.Warning));
        }
        catch (Exception ex)
        {
            return new StartedLaunch(plan, null, null, launchedAt, new LaunchResult(false,
                ErrorMessage: ex.Message, ErrorCode: nameof(LaunchErrorCode.LaunchException), Warning: plan.Warning));
        }
    }

    /// <summary>
    /// Activates a packaged app (roadmap M6.5) on the start's own thread and opens the process the activation reports: null
    /// when it exited already (it handed the request to an instance that was running, whose window the package then tells).
    /// When Windows refuses the activation for this server's elevation, the app is started through the shell instead,
    /// without a process ID, and <paramref name="note"/> says so; any other failure throws <see cref="ActivationFailedException"/>.
    /// </summary>
    private IProcessHandle? Activate(LaunchPlan plan, Action<string> note)
    {
        var app = plan.Target!.App!;
        var arguments = string.IsNullOrEmpty(plan.StartInfo!.Arguments) ? null : plan.StartInfo.Arguments;
        var activation = _appsWrapper!.ActivateApplication(app.AppUserModelId, arguments);
        if (activation.Succeeded)
        {
            if (activation.ProcessId is not { } processId)
                return null;

            IProcessHandle? process;
            try
            {
                process = _processWrapper.OpenProcess(processId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ArgumentException or UnauthorizedAccessException)
            {
                return null;
            }

            // Review M6.5 #4: an app that exited at once may have left its PID to another process before it was opened, so the
            // process behind it must be of the app's package. Review F4: the opened process holds a handle to it, which pins
            // the PID from here to the end of the window wait (when no handle could be opened, the package is still checked,
            // but the PID is not pinned for the wait)
            if (process != null &&
                !string.Equals(_appsWrapper.GetPackageFamilyName(processId), app.PackageFamilyName, StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                return null;
            }

            return process;
        }

        if (!ActivationRefusedForElevation.Contains(activation.HResult))
            throw new ActivationFailedException(activation.HResult);

        note($"Windows does not activate a packaged app from this server (HRESULT 0x{activation.HResult:X8}: the server runs " +
             "elevated, or User Account Control is off), so the app was started through the shell: its processId is not known, " +
             "and its window is found by its package.");
        return _processWrapper.StartProcess(plan.StartInfo);
    }

    /// <summary>Why a packaged app's activation failed, with its HRESULT and what to do.</summary>
    private static string ActivationFailureMessage(PackagedAppId app, int hresult)
    {
        var reason = hresult == ApplicationNotRegistered
            ? "the app is not installed for the user, or cannot be launched this way: check the appId with list_installed_apps"
            : "the app may not be installed for this user (check the appId with list_installed_apps), its package may be " +
              "being updated, or it failed to start";
        return $"Windows could not activate the app '{app.AppUserModelId}' (HRESULT 0x{hresult:X8}): {reason}. Nothing was started.";
    }

    /// <summary>A packaged app's activation failed with the HRESULT in <see cref="Result"/> (roadmap M6.5).</summary>
    private sealed class ActivationFailedException(int result) : Exception($"The activation failed with HRESULT 0x{result:X8}.")
    {
        public int Result { get; } = result;
    }

    /// <summary>A start that was not waited for: its process handle is released when it comes, and a failure is observed.</summary>
    private static void ReleaseWhenStarted(Task<IProcessHandle?> start) =>
        start.ContinueWith(
            static t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                    t.Result?.Dispose();
                else
                    _ = t.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static LaunchResult StartedWithoutWait(StartedLaunch launch) => launch.Process == null
        ? new LaunchResult(
            Success: true,
            Warning: CombineWarnings(launch.Plan.Warning,
                "Windows started no new process: the request went to an app that was already running, or a packaged app was " +
                "activated, so processId is null. Find its window with find_windows, or launch with waitForWindow=true."),
            Status: LaunchStatus.PossibleHandoff)
        : new LaunchResult(Success: true, ProcessId: launch.Process.Id, Warning: launch.Plan.Warning, Status: LaunchStatus.Started);

    /// <summary>
    /// Waits for the window of a started launch (<see cref="IWindowWaitService"/>) and turns the outcome into the launch
    /// result. When no window was found, a visible main window of the started process (which may be untitled) still counts.
    /// </summary>
    /// <param name="isOwner">Whether a window belongs to the launch: <paramref name="owner"/>'s match, narrowed in a batch.</param>
    private async Task<LaunchResult> WaitForWindowAsync(
        StartedLaunch launch,
        LaunchRequest request,
        LaunchWindowOwner owner,
        Func<WindowCandidate, bool> isOwner,
        int waitTimeoutMs,
        Func<nint, bool>? tryClaim,
        CancellationToken cancellationToken)
    {
        var target = launch.Plan.Target!;
        var options = new WindowWaitOptions
        {
            TimeoutMs = waitTimeoutMs,
            DocumentHints = DocumentHints(target, request.Arguments, launch.Plan.StartInfo!.WorkingDirectory),
            TitleContains = string.IsNullOrWhiteSpace(request.ExpectWindow?.TitleContains) ? null : request.ExpectWindow.TitleContains.Trim(),
            TryClaim = tryClaim,
            // A packaged app's frame may be in the baseline, cloaked: Windows shows a suspended app's frame again (roadmap
            // M6.5). Only for a process the activation started: an app that ran before keeps its window (review M6.5 #9)
            WasCloakedOwner = target.Kind == LaunchTargetKind.PackagedApp && launch.Process is { } activated && StartedSince(activated.Id, launch.LaunchedAtUtc)
                ? candidate => owner.StartedProcess(candidate.ProcessId)
                : null
        };

        var wait = await _windowWait.WaitForNewWindowAsync(launch.Baseline!, isOwner, options, cancellationToken).ConfigureAwait(false);
        var window = wait.Window;
        var status = wait.Outcome switch
        {
            WindowWaitOutcome.NewWindow => LaunchStatus.WindowObserved,
            WindowWaitOutcome.ReusedExistingWindow => LaunchStatus.ReusedExistingWindow,
            WindowWaitOutcome.OpenedInTab => LaunchStatus.OpenedInTab,
            _ => LaunchStatus.TimedOut
        };

        if (status == LaunchStatus.TimedOut && launch.Process != null && MainWindowOf(launch.Process) is { } mainWindow)
        {
            window = mainWindow;
            status = LaunchStatus.WindowObserved;
        }

        bool? launcherExited = launch.Process == null ? null : HasExitedSafe(launch.Process);
        if (status == LaunchStatus.TimedOut)
        {
            status = launch.Process == null ? LaunchStatus.PossibleHandoff
                : launcherExited == true ? LaunchStatus.ExitedWithoutWindow
                : LaunchStatus.TimedOut;
        }

        return new LaunchResult(
            Success: true,
            ProcessId: launch.Process?.Id,
            WindowHandle: window?.Handle,
            Warning: CombineWarnings(launch.Plan.Warning, NoWindowNote(status, waitTimeoutMs)),
            Status: status,
            Window: window,
            LauncherExited: launcherExited);
    }

    /// <summary>Whether the process started at or after <paramref name="launchedAtUtc"/> (a second's tolerance); false when unknown.</summary>
    private bool StartedSince(int processId, DateTime launchedAtUtc) =>
        _processWrapper.GetProcessStartTime(processId) is { } started && started >= launchedAtUtc - TimeSpan.FromSeconds(1);

    private LaunchWindowOwner OwnerOf(StartedLaunch launch, LaunchRequest request) =>
        LaunchWindowOwner.For(_processWrapper, launch.Process?.Id, launch.LaunchedAtUtc, launch.Plan.Target!, request.ExpectWindow,
            request.OnlyStartedProcess, _appsWrapper == null ? null : _appsWrapper.GetPackageFamilyName);

    /// <summary>
    /// A batch launch's owner check: <paramref name="owner"/>'s match, except for a window whose process another launch of
    /// the batch started (or a child of it) and this one did not. Two launches of the same program both match each
    /// other's windows by name and path; the started process decides between them. Such a window is this launch's after
    /// all once that other launch has its window and this launch's launcher really handed off (<paramref name="handoff"/>):
    /// a single-instance app whose launcher handed the request to the instance an earlier item started (two Code or
    /// Terminal launches) opens its window in that instance. The shared claim still gives each window to one launch.
    /// </summary>
    private static Func<WindowCandidate, bool> BatchOwner(
        LaunchWindowOwner owner, IReadOnlyList<(LaunchWindowOwner Owner, Func<bool> HasWindow)> others, LauncherHandoff handoff) =>
        window =>
        {
            if (!owner.Matches(window))
                return false;

            if (owner.StartedProcess(window.ProcessId))
                return true;

            // Read for every candidate, so the handoff sees which windows were there while the launcher still ran
            var handedOff = handoff.HandedOff(window.Handle);
            return !others.Any(other => other.Owner.StartedProcess(window.ProcessId) && !(other.HasWindow() && handedOff));
        };

    /// <summary>
    /// Whether a batch launch's launcher handed its request to a running instance, so that a window of that instance can
    /// be the launch's (M2 follow-up F3). A launch that started no process has no launcher to judge (Windows handed it
    /// over). Otherwise the launcher must have exited: with exit code 0, which a single-instance launcher returns once it
    /// handed off; a non-zero code means it failed, and nothing it did opens a window. When the code cannot be read, only
    /// a window that was not there while the launcher still ran counts.
    /// </summary>
    private sealed class LauncherHandoff(IProcessHandle? launcher)
    {
        private readonly HashSet<nint> _seenWhileRunning = [];

        /// <summary>True when the launcher handed off and <paramref name="window"/> can be what it handed off.</summary>
        public bool HandedOff(nint window)
        {
            if (launcher == null)
                return true;

            lock (_seenWhileRunning)
            {
                if (!HasExitedSafe(launcher))
                {
                    _seenWhileRunning.Add(window);
                    return false;
                }

                return launcher.ExitCode switch
                {
                    0 => true,
                    null => !_seenWhileRunning.Contains(window),
                    _ => false
                };
            }
        }
    }

    /// <summary>The visible main window of the started process, or null (it has none, or it exited).</summary>
    private WindowInfo? MainWindowOf(IProcessHandle process)
    {
        try
        {
            var handle = process.RefreshMainWindowHandle();
            return handle != nint.Zero && _windowService.GetWindowInfo(handle) is { IsVisible: true } window ? window : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>What to do when the wait found no window; null when it found one.</summary>
    internal static string? NoWindowNote(LaunchStatus status, int waitTimeoutMs) => status switch
    {
        LaunchStatus.PossibleHandoff =>
            "Windows started no new process (the request went to an app that was already running, or a packaged app was activated), " +
            $"and no window of it was recognised within {waitTimeoutMs} ms. Look for it with find_windows, or launch with expectWindow " +
            "(processName or titleContains) so the wait can recognise it. Do not launch it again.",
        LaunchStatus.ExitedWithoutWindow =>
            "The started process exited without a window of its own, and no window of an app it may have handed off to was " +
            $"recognised within {waitTimeoutMs} ms. Look for it with find_windows, or launch with expectWindow (processName or " +
            "titleContains). Do not launch it again.",
        LaunchStatus.TimedOut =>
            $"The program is running, but no window of it appeared within {waitTimeoutMs} ms: it may still be starting, or run " +
            "without a window. Check with find_windows; do not launch it again.",
        _ => null
    };

    public async Task<IReadOnlyList<LaunchResult>> LaunchApplicationsBatchAsync(
        IReadOnlyList<LaunchRequest> applications,
        bool waitForWindows = false,
        int waitTimeoutMs = 5000,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applications);

        var results = new LaunchResult?[applications.Count];
        var plans = new LaunchPlan?[applications.Count];
        var waiting = new List<(int Index, StartedLaunch Launch)>();
        var reused = new HashSet<nint>();
        // reuseExisting reuses only windows that were open before the batch started anything, not one an earlier item started
        var openBefore = applications.Any(a => a.ReuseExisting)
            ? _windowService.GetApplicationWindows().Select(w => w.Handle).ToHashSet()
            : null;
        var finished = 0;
        var reportLock = new object();

        // "i of n" when an application is done: refused or started (without a wait), or its window wait ended. The waits
        // end concurrently: counting and reporting under one lock keeps the reported values increasing
        void Report(int index)
        {
            lock (reportLock)
            {
                finished++;
                progress?.Report(new OperationProgress(finished, applications.Count,
                    $"{finished} of {applications.Count}: {applications[index].Executable} {DescribeLaunch(results[index]!)}"));
            }
        }

        try
        {
            // Start every application first, in order; a cancelled call starts no more
            for (var i = 0; i < applications.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = applications[i];
                var plan = _planner.PlanFor(request);
                plans[i] = plan;
                if (!plan.IsValid)
                {
                    results[i] = Rejected(plan);
                    Report(i);
                    continue;
                }

                // reuseExisting: an open window of the app, each used by one item only
                if (request.ReuseExisting)
                {
                    var reuse = _reuse.FindOpenWindow(plan, request, reused, openBefore);
                    if (reuse.Result is { } alreadyOpen)
                    {
                        reused.Add(alreadyOpen.WindowHandle!.Value);
                        results[i] = alreadyOpen;
                        Report(i);
                        continue;
                    }

                    plan = plan with { Warning = CombineWarnings(plan.Warning, reuse.Note) };
                    plans[i] = plan;
                }

                // Read right before this item's start, so an earlier item's process counts as running
                plan = plan with { RunningInstance = RunningInstanceOf(plan) };
                plans[i] = plan;
                var launch = await StartAsync(plan, waitForWindows, cancellationToken).ConfigureAwait(false);
                if (launch.Outcome is { } ended)
                {
                    results[i] = ended;
                    Report(i);
                }
                else if (!waitForWindows)
                {
                    using (launch.Process)
                    {
                        results[i] = StartedWithoutWait(launch);
                    }

                    Report(i);
                }
                else
                {
                    waiting.Add((i, launch));
                }
            }

            // Then wait for all the windows at once, against one deadline; a new window goes to one launch only. A window
            // of a process that one launch started (or that process's child) is that launch's: the other launches' looser
            // matches (same program name or path, handoff names, hint) leave it alone, whichever wait sees it first
            var owners = waiting.Select(item => OwnerOf(item.Launch, applications[item.Index])).ToList();
            var claimed = new HashSet<nint>();
            var hasWindow = new bool[waiting.Count];
            bool Claim(int position, nint handle)
            {
                lock (claimed)
                {
                    if (!claimed.Add(handle))
                        return false;

                    hasWindow[position] = true;
                    return true;
                }
            }

            bool HasWindow(int position)
            {
                lock (claimed)
                {
                    return hasWindow[position];
                }
            }

            await Task.WhenAll(waiting.Select(async (item, position) =>
            {
                var owner = owners[position];
                var others = owners.Select((other, index) => (Owner: other, HasWindow: (Func<bool>)(() => HasWindow(index))))
                    .Where((_, index) => index != position).ToList();
                results[item.Index] = await WaitForWindowAsync(
                    item.Launch, applications[item.Index], owner, BatchOwner(owner, others, new LauncherHandoff(item.Launch.Process)), waitTimeoutMs,
                    handle => Claim(position, handle), cancellationToken).ConfigureAwait(false);
                Report(item.Index);
            })).ConfigureAwait(false);
        }
        finally
        {
            foreach (var (_, launch) in waiting)
            {
                launch.Process?.Dispose();
            }
        }

        return results.Select((result, i) => plans[i] is { } plan ? Finish(result!, plan) : result!).ToList();
    }

    private static string DescribeLaunch(LaunchResult result) => !result.Success
        ? $"was not started ({result.ErrorCode})"
        : result.Status switch
        {
            LaunchStatus.WindowObserved => "started, its window appeared",
            LaunchStatus.ReusedExistingWindow => "started, it reused a window that was already open",
            LaunchStatus.OpenedInTab => "started, it opened in a window that was already open",
            LaunchStatus.PossibleHandoff => "was handed to an app that was already running",
            LaunchStatus.ExitedWithoutWindow => "started, but it exited without a window",
            LaunchStatus.TimedOut => "started, no window appeared in time",
            LaunchStatus.AwaitingUserConsent => "is waiting for the user to answer a prompt (most likely UAC)",
            LaunchStatus.AlreadyOpen => "was already open, nothing was started",
            _ => "started"
        };
}
