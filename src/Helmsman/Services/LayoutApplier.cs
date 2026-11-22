using Helmsman.Models;
using Helmsman.Models.Results;
using Helmsman.Services.Interfaces;

namespace Helmsman.Services;

/// <summary>
/// apply_layout (<see cref="ILayoutApplier"/>): arranges the open windows of a preset placement by placement, and launches the
/// missing ones when asked, each after the launch planner's check and, for an agent-written preset, the user's confirmation.
/// </summary>
public class LayoutApplier : ILayoutApplier
{
    private readonly IWindowService _windowService;
    private readonly IMonitorService _monitorService;
    private readonly IProcessService _processService;
    private readonly IForegroundService _foregroundService;
    private readonly ILaunchPlacementService _launchPlacement;
    private readonly ILayoutPresetStore _store;
    private readonly ILaunchConfirmationStore _confirmations;

    /// <param name="foregroundService">Brings the windows of placements with focus set to the front, verified.</param>
    /// <param name="store">Finds the preset to apply, under the store's gate.</param>
    /// <param name="confirmations">The launches of agent-written presets the user confirmed (owner decision D15).</param>
    /// <param name="launchPlacement">
    /// Places the windows launchMissing launches, as the launch tools place theirs (roadmap M3.F27), and respects the user's
    /// pause.
    /// </param>
    public LayoutApplier(
        IWindowService windowService,
        IMonitorService monitorService,
        IProcessService processService,
        IForegroundService foregroundService,
        ILayoutPresetStore store,
        ILaunchConfirmationStore confirmations,
        ILaunchPlacementService launchPlacement)
    {
        _windowService = windowService;
        _monitorService = monitorService;
        _processService = processService;
        _foregroundService = foregroundService;
        _launchPlacement = launchPlacement ?? throw new ArgumentNullException(nameof(launchPlacement));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _confirmations = confirmations ?? throw new ArgumentNullException(nameof(confirmations));
    }

    /// <summary>How long a layout's launch waits for its window.</summary>
    private const int LaunchWaitTimeoutMs = 5000;

    public async Task<LayoutApplyResult> ApplyPresetAsync(
        string name,
        LayoutMatchBy matchBy = LayoutMatchBy.ProcessAndTitle,
        bool launchMissing = false,
        LayoutApplyOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LayoutApplyOptions();
        var dryRun = options.DryRun;
        var (storageError, preset, notFoundNote) = await _store.FindForApplyAsync(name, cancellationToken);

        if (storageError != null)
        {
            return new LayoutApplyResult(
                Success: false,
                PresetName: name,
                Errors: [storageError],
                ErrorCode: nameof(LayoutErrorCode.StorageUnavailable),
                DryRun: dryRun);
        }

        if (preset == null)
        {
            return new LayoutApplyResult(
                Success: false,
                PresetName: name,
                Errors: [$"Layout preset '{name}' not found" + (notFoundNote.Length > 0 ? "." + notFoundNote : string.Empty)],
                ErrorCode: nameof(LayoutErrorCode.PresetNotFound),
                DryRun: dryRun);
        }

        if (SubsetError(options.Placements, preset.Placements.Count) is { } subsetError)
        {
            return new LayoutApplyResult(
                Success: false,
                PresetName: name,
                Errors: [subsetError],
                ErrorCode: Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode,
                DryRun: dryRun);
        }

        var monitors = _monitorService.GetAllMonitors();

        // The placements in the order they are applied, each with its place in the preset (the index get_layout lists it at)
        var ordered = preset.Placements.Select((placement, index) => (Placement: placement, Index: index))
            .OrderBy(t => t.Placement.Order)
            .ToList();

        // One snapshot of the user-facing windows, as find_windows lists them; every placement is matched against it, so a window is
        // given to at most one placement. A subset is planned with the whole preset, so each of its placements gets the
        // window it would get in a full apply
        var snapshot = _windowService.GetAllWindows(includeMinimized: true);
        var exePaths = new Dictionary<int, string?>();
        var plans = LayoutPlanner.Plan(ordered.Select(t => t.Placement).ToList(), snapshot, monitors, matchBy,
            processId => LayoutCapturer.ExePathOf(_processService, processId, exePaths));
        var assignedTo = plans
            .Where(plan => plan.Window != null)
            .ToDictionary(plan => plan.Window!.Handle, plan => plan.Placement.Order);
        var chosen = Enumerable.Range(0, plans.Count)
            .Where(k => options.Placements == null || options.Placements.Contains(ordered[k].Index))
            .Select(k => plans[k])
            .ToList();

        // An agent wrote this preset: what it would launch needs the user's confirmation for this version of it (D15)
        var launchesConfirmed = _confirmations.AreLaunchesConfirmed(preset);

        var results = new List<LayoutPlacementResult>(chosen.Count);
        List<FocusResult>? focusResults = null;

        // What the placement being applied has done so far, so that a placement that throws still reports its launch
        string? launchCommand = null;
        var launchedWindow = false;
        WindowInfo? placedWindow = null;

        for (var index = 0; index < chosen.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var plan = chosen[index];
            LayoutPlacementResult result;
            string outcome;
            launchCommand = null;
            launchedWindow = false;
            placedWindow = plan.Window;
            try
            {
                (result, outcome) = await ApplyPlacementAsync(plan).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One placement's failure never aborts the others; a window it launched is still reported as launched
                result = new LayoutPlacementResult(
                    plan.Placement.Order,
                    LayoutPlacementStatus.Failed,
                    placedWindow is { } window ? LayoutWindowRef.From(window) : null,
                    Launched: launchedWindow ? true : null,
                    ErrorCode: nameof(LayoutErrorCode.ArrangeFailed),
                    Error: $"Could not arrange {plan.Label}: " + (ex is WindowNotRespondingException
                        ? $"{nameof(WindowErrorCode.NotResponding)}: {ex.Message}"
                        : $"{ex.GetType().Name}: {ex.Message}"),
                    LaunchCommand: launchCommand,
                    MonitorFallback: plan.MonitorFallback);
                outcome = "could not be arranged";
            }

            results.Add(result);

            // The client sees each placement's outcome as it happens: a layout that launches applications can take
            // several seconds per placement
            progress?.Report(new OperationProgress(index + 1, chosen.Count,
                $"{index + 1} of {chosen.Count}: {plan.Label} {outcome}"));
        }

        var errors = results.Where(r => r.Error != null).Select(r => r.Error!).ToList();
        if (dryRun)
        {
            return new LayoutApplyResult(
                Success: results.All(r => r.Status is LayoutPlacementStatus.WouldApply or LayoutPlacementStatus.WouldLaunch && r.ErrorCode == null),
                PresetName: name,
                WindowsNotFound: results.Count(r => r.Status == LayoutPlacementStatus.NotFound),
                Errors: errors.Count > 0 ? errors : null,
                ErrorCode: results.FirstOrDefault(r => r.ErrorCode != null)?.ErrorCode,
                Placements: results,
                DryRun: true);
        }

        return new LayoutApplyResult(
            Success: results.All(r => r.Status == LayoutPlacementStatus.Applied && r.ErrorCode == null),
            PresetName: name,
            WindowsArranged: results.Count(r => r.Status == LayoutPlacementStatus.Applied),
            WindowsNotFound: results.Count(r =>
                r.Status == LayoutPlacementStatus.NotFound || r.ErrorCode == nameof(LayoutErrorCode.LaunchFailed)),
            WindowsLaunched: results.Count(r => r.Launched == true),
            Errors: errors.Count > 0 ? errors : null,
            ErrorCode: results.FirstOrDefault(r => r.ErrorCode != null)?.ErrorCode,
            Focus: focusResults,
            Placements: results
        );

        // Arranges one placement and returns what happened to it, and the outcome in words for the progress report
        async Task<(LayoutPlacementResult Result, string Outcome)> ApplyPlacementAsync(PlannedPlacement plan)
        {
            var placement = plan.Placement;
            var label = plan.Label;

            LayoutPlacementResult Result(string status, WindowInfo? window, string? errorCode = null, string? error = null,
                bool launched = false, bool? focused = null, WindowRect? target = null) =>
                new(placement.Order, status, window == null ? null : LayoutWindowRef.From(window),
                    launched ? true : null, focused, errorCode, error, launchCommand,
                    target is { } t ? new BoundsDto(t.X, t.Y, t.Width, t.Height) : null, plan.MonitorFallback);

            if (plan.NotApplicable is { } notApplicable)
            {
                return (Result(LayoutPlacementStatus.NotApplicable, null, nameof(LayoutErrorCode.NoMatchCriterion), notApplicable),
                    "was skipped: it has no criterion matchBy can use");
            }

            if (plan.MonitorError is { } monitorError)
            {
                return (Result(LayoutPlacementStatus.Failed, null, plan.MonitorErrorCode ?? nameof(WindowErrorCode.InvalidMonitorIndex), monitorError),
                    "was not arranged: its monitor does not exist");
            }

            // A rectangle less than a pixel on its monitor is neither arranged nor launched (review finding F2)
            if (plan.PositionError is { } positionError)
            {
                return (Result(LayoutPlacementStatus.Failed, null, nameof(LayoutErrorCode.InvalidPlacement), positionError),
                    "was not arranged: its rectangle is less than a pixel");
            }

            var window = plan.Window;
            var launched = false;
            if (window == null)
            {
                var launchTarget = LayoutPresetSchema.LaunchTarget(placement);
                if (!launchMissing || launchTarget == null)
                {
                    var hint = launchTarget == null ? string.Empty : $"; set launchMissing=true to launch '{launchTarget}'";
                    return (Result(LayoutPlacementStatus.NotFound, null, nameof(LayoutErrorCode.WindowNotFound),
                        $"No open window matched {label}{hint}"), "was not found");
                }

                launchCommand = launchTarget;

                // Planned first, as the launch plans it, without starting anything: a dry run says what the apply would, and the
                // user is never asked about a launch that would be refused (review M6.8 #3)
                var request = new LaunchRequest(launchTarget, ExpectWindow: ExpectedWindow(placement));
                var refusal = LayoutPresetSchema.LaunchTargetError(launchTarget) is { } targetError
                    ? new LaunchRefusal(nameof(LayoutErrorCode.LaunchFailed), targetError)
                    : _processService.CheckLaunch(request);
                if (refusal != null)
                {
                    return (Result(LayoutPlacementStatus.Failed, null, nameof(LayoutErrorCode.LaunchFailed),
                        $"Failed to launch '{launchTarget}' for {label} ({refusal.ErrorCode}): {refusal.Error}"),
                        dryRun ? "would not be launched" : "could not be launched");
                }

                if (!launchesConfirmed)
                {
                    var why = options.LaunchRefusal ?? "apply_layout asks the user first when the client can ask (elicitation)";
                    var notConfirmed = $"Did not launch '{launchTarget}' for {label}: an AI agent wrote this layout, and the user has " +
                                       $"not confirmed that it may start programs ({why}). Ask the user to start it, or to confirm.";
                    return dryRun
                        ? (Result(LayoutPlacementStatus.WouldLaunch, null, nameof(LayoutErrorCode.LaunchNotConfirmed),
                            $"{notConfirmed} apply_layout asks them first when the client can ask."), "would need the user's confirmation to launch")
                        : (Result(LayoutPlacementStatus.Failed, null, nameof(LayoutErrorCode.LaunchNotConfirmed), notConfirmed),
                            "was not launched: the user has not confirmed it");
                }

                if (dryRun)
                    return (Result(LayoutPlacementStatus.WouldLaunch, null, target: DryRunTarget(plan)), "would be launched");

                // Launched as launch_application launches: the program is planned and checked (scripts and interpreters with
                // arguments are refused, a packaged app is activated), and its window is waited for, whichever process
                // owns it (roadmap M6.8, D-PAL-14). A cancelled call stops waiting for its window
                var launchResult = await _processService.LaunchApplicationAsync(
                    request,
                    waitForWindow: true,
                    waitTimeoutMs: LaunchWaitTimeoutMs,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!launchResult.Success)
                {
                    return (Result(LayoutPlacementStatus.Failed, null, nameof(LayoutErrorCode.LaunchFailed),
                        $"Failed to launch '{launchTarget}' for {label}: {launchResult.ErrorMessage ?? "unknown error"}"),
                        "could not be launched");
                }

                launched = true;
                launchedWindow = true;
                window = launchResult.WindowHandle is { } handle ? _windowService.GetWindowInfo(handle) : null;
                placedWindow = window;
                if (window == null)
                {
                    return (Result(LayoutPlacementStatus.Failed, null, nameof(LayoutErrorCode.LaunchFailed),
                        $"Launched '{launchTarget}' for {label}, but its window could not be found", launched: true),
                        "was launched, but its window was not found");
                }

                // A single-instance application can hand the launch to a window it already has open
                if (!assignedTo.TryAdd(window.Handle, placement.Order))
                {
                    return (Result(LayoutPlacementStatus.Failed, window, nameof(LayoutErrorCode.LaunchFailed),
                        $"Launched '{launchTarget}' for {label}, but the window it brought up, '{window.Title}', " +
                        $"was already given to placement {assignedTo[window.Handle]}", launched: true),
                        "was launched, but its window belongs to another placement");
                }

                // The launch tools' placement (roadmap M3.F27): the same checks before the window is touched (the agent's own
                // host, the user's policy, the pause), the watch for an app that moves its window back, and focus last. A
                // monitor saved by identity is found again by it when the window is placed (roadmap M6.8)
                var placed = (await _launchPlacement.PlaceAsync([(window.Handle, LaunchPlan(plan))], cancellationToken)
                    .ConfigureAwait(false)).Single();
                if (placed.Activation is { } activation)
                    (focusResults ??= []).Add(FocusResult.From(activation));

                return Launched(window, placed);
            }

            // The user's policy: a denied window is neither placed nor focused (restoring or raising it would put it
            // on screen, where a screenshot shows it)
            if (_windowService.EvaluateTargetPolicy(window.Handle) is { Allowed: false } verdict)
            {
                return (Result(LayoutPlacementStatus.Failed, window, nameof(WindowErrorCode.TargetDeniedByPolicy),
                    $"Did not arrange window '{window.Title}' for {label}: {WindowService.WindowChangeRefusal(window.Handle, verdict)}",
                    launched),
                    "was not arranged: the user's policy denies its window");
            }

            if (dryRun)
            {
                // A window its app hid while it is minimized or maximized is refused by the arrangement, which would show it
                // (review M6.8 #3): the dry run says so too
                return HiddenRefusal(window, plan) is { } hidden
                    ? (Result(LayoutPlacementStatus.Failed, window, nameof(LayoutErrorCode.ArrangeFailed),
                        $"Failed to arrange window '{window.Title}' for {label}: {nameof(WindowErrorCode.MoveFailed)}: {hidden}"),
                        "would not be arranged: its window is hidden")
                    : (Result(LayoutPlacementStatus.WouldApply, window, target: DryRunTarget(plan)), "would be arranged");
            }

            // A window that is minimized and stays minimized is given its restore rectangle without being shown (roadmap M6.8)
            var keptMinimized = placement.State == WindowState.Minimized && window.State == WindowState.Minimized && plan.Snap == null;
            string? notPlaced;
            try
            {
                var change = Arrange(window, plan, keptMinimized);
                notPlaced = change.Success ? null : $"{change.ErrorCode}: {change.Error}";
            }
            catch (WindowNotRespondingException ex)
            {
                // The application is hung or busy: this placement fails, and the others still run
                notPlaced = $"{nameof(WindowErrorCode.NotResponding)}: {ex.Message}";
            }

            if (notPlaced != null)
            {
                return (Result(LayoutPlacementStatus.Failed, window, nameof(LayoutErrorCode.ArrangeFailed),
                    $"Failed to arrange window '{window.Title}' for {label}: {notPlaced}", launched),
                    "could not be arranged");
            }

            var outcome = "was arranged";
            string? errorCode = null;
            string? error = null;
            bool? focused = null;

            if (placement.State == WindowState.Minimized && !keptMinimized)
            {
                WindowChangeResult minimized;
                try
                {
                    minimized = _windowService.SetWindowState(window.Handle, WindowStateRequest.Minimize);
                }
                catch (WindowNotRespondingException ex)
                {
                    // The window was arranged: the placement stays applied, with the minimize's failure
                    minimized = WindowChangeResult.Failed(window.Handle, WindowErrorCode.NotResponding, ex.Message);
                }

                if (!minimized.Success)
                {
                    outcome = "was arranged, but could not be minimized";
                    errorCode = nameof(LayoutErrorCode.ArrangeFailed);
                    error = $"Arranged window '{window.Title}' for {label}, but could not minimize it ({minimized.ErrorCode}): {minimized.Error}";
                }
            }

            if (Focuses(placement))
            {
                var focus = _foregroundService.EnsureForeground(window.Handle);
                (focusResults ??= []).Add(FocusResult.From(focus));
                focused = focus.IsForeground;
                if (focus.IsForeground)
                {
                    outcome = "was arranged and brought to the front";
                }
                else
                {
                    outcome = "was arranged, but could not be brought to the front";
                    errorCode ??= nameof(LayoutErrorCode.ArrangeFailed);
                    error ??= $"Arranged window '{window.Title}' for {label}, but could not bring it to the foreground " +
                              $"({focus.ErrorCode}): {focus.Error}";
                }
            }

            // Small delay between window operations
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            return (Result(LayoutPlacementStatus.Applied, window, errorCode, error, launched, focused), outcome);

            // What the placement service did with a launched window, as apply_layout reports an open window's placement: a
            // window that was not placed fails, one that was placed but not minimized, kept in place or focused is applied
            // with ArrangeFailed, and so is one the app kept moving back (apply_layout has no warnings)
            (LayoutPlacementResult Result, string Outcome) Launched(WindowInfo window, LaunchPlacementOutcome placed)
            {
                var code = placed.ErrorCode.ToString();
                bool? focused = Focuses(placement) ? placed.Activation?.IsForeground == true : null;
                return placed.FailedStep switch
                {
                    LaunchPlacementStep.None when placed.Warning is { } warning => (
                        Result(LayoutPlacementStatus.Applied, window, nameof(LayoutErrorCode.ArrangeFailed),
                            $"Arranged window '{window.Title}' for {label}, but its app kept moving it back: {warning}",
                            launched: true, focused: focused),
                        "was arranged, but its app kept moving it back"),
                    LaunchPlacementStep.None => (
                        Result(LayoutPlacementStatus.Applied, window, launched: true, focused: focused),
                        focused == true ? "was arranged and brought to the front" : "was arranged"),
                    LaunchPlacementStep.Check when placed.ErrorCode == WindowErrorCode.TargetDeniedByPolicy => (
                        Result(LayoutPlacementStatus.Failed, window, code, $"Did not arrange window '{window.Title}' for {label}: {placed.Error}",
                            launched: true),
                        "was not arranged: the user's policy denies its window"),
                    LaunchPlacementStep.Check => (
                        Result(LayoutPlacementStatus.Failed, window, nameof(LayoutErrorCode.ArrangeFailed),
                            $"Did not arrange window '{window.Title}' for {label}: {code}: {placed.Error}", launched: true),
                        "was not arranged"),
                    LaunchPlacementStep.Place => (
                        Result(LayoutPlacementStatus.Failed, window, nameof(LayoutErrorCode.ArrangeFailed),
                            $"Failed to arrange window '{window.Title}' for {label}: {code}: {placed.Error}", launched: true),
                        "could not be arranged"),
                    LaunchPlacementStep.Settle when placed.ErrorCode == WindowErrorCode.InvalidHandle => (
                        Result(LayoutPlacementStatus.Failed, window, nameof(LayoutErrorCode.ArrangeFailed),
                            $"Failed to arrange window '{window.Title}' for {label}: {code}: {placed.Error}", launched: true),
                        "was arranged, but its window closed"),
                    LaunchPlacementStep.Minimize => (
                        Result(LayoutPlacementStatus.Applied, window, nameof(LayoutErrorCode.ArrangeFailed),
                            $"Arranged window '{window.Title}' for {label}, but could not minimize it ({code}): {placed.Error}", launched: true),
                        "was arranged, but could not be minimized"),
                    LaunchPlacementStep.Focus => (
                        Result(LayoutPlacementStatus.Applied, window, nameof(LayoutErrorCode.ArrangeFailed),
                            $"Arranged window '{window.Title}' for {label}, but could not bring it to the foreground ({code}): {placed.Error}",
                            launched: true, focused: false),
                        "was arranged, but could not be brought to the front"),
                    // Settle: the app moved the window back, and it was not placed again (paused, or the placement failed)
                    _ => (
                        Result(LayoutPlacementStatus.Applied, window, nameof(LayoutErrorCode.ArrangeFailed),
                            $"Arranged window '{window.Title}' for {label}, but {code}: {placed.Error}", launched: true),
                        "was arranged, but its app moved it back")
                };
            }
        }
    }

    /// <summary>
    /// Whether the placement's window is brought to the front: when it asks to be, unless its state is minimized. A layout an
    /// agent writes cannot have both (save_layout refuses them); a file edited by hand can, and then minimized wins, for an open
    /// window as for one apply_layout launches (<see cref="LaunchPlacementPlan.ForLayout"/>), so the window the layout
    /// minimizes is not brought back to the front.
    /// </summary>
    private static bool Focuses(WindowPlacement placement) => placement.Focus && placement.State != WindowState.Minimized;

    /// <summary>
    /// Why a subset of placements cannot be applied: an index that is not a placement of the preset, or one given twice. Null
    /// for no subset, or a valid one.
    /// </summary>
    private static string? SubsetError(IReadOnlyList<int>? subset, int count)
    {
        if (subset == null)
            return null;

        if (subset.Count == 0)
            return "placements is empty: leave it out to apply every placement, or name at least one. Nothing was arranged.";

        var outside = subset.Where(i => i < 0 || i >= count).ToList();
        if (outside.Count > 0)
        {
            var bad = outside[0];
            return $"placements names {bad}, but the preset has {count} placement{(count == 1 ? string.Empty : "s")} (0 to {count - 1}, as " +
                   "get_layout lists them). Nothing was arranged.";
        }

        return subset.Distinct().Count() == subset.Count
            ? null
            : "placements names a placement twice. Nothing was arranged.";
    }

    /// <summary>
    /// Why the arrangement would refuse the window, as the window service does: it is hidden (its app hid it, in the notification
    /// area, say) and minimized or maximized, so restoring it or setting its restore rectangle would show it. A window maximized
    /// on its monitor that stays so is left alone and not refused. Null when it is not refused.
    /// </summary>
    private static string? HiddenRefusal(WindowInfo window, PlannedPlacement plan)
    {
        if (window.IsVisible || window.State is not (WindowState.Minimized or WindowState.Maximized))
            return null;

        var leftAlone = plan.Snap == null && plan.Placement.State == WindowState.Maximized && window.State == WindowState.Maximized &&
                        window.MonitorIndex is { } monitor && monitor == plan.Monitor?.Index;
        return leftAlone
            ? null
            : $"The window is hidden and {window.State.ToString().ToLowerInvariant()}: its application hid it (for example in the " +
              "notification area), and arranging it would show it, so it would not be arranged. Show it through its application, or ask the user.";
    }

    /// <summary>
    /// Where a dry run says the window would go: the restore rectangle of a maximized placement (the work area when it saved
    /// none) or of a minimized one, else its rectangle.
    /// </summary>
    private static WindowRect? DryRunTarget(PlannedPlacement plan) => plan.Placement.State switch
    {
        WindowState.Maximized => plan.NormalTarget ?? (plan.WorkArea is { } area ? new WindowRect(area.X, area.Y, area.Width, area.Height) : null),
        WindowState.Minimized => plan.NormalTarget ?? plan.Target,
        _ => plan.Target
    };

    /// <summary>
    /// The window a layout's launch expects: one of the placement's process (part of its name), which tells the launch's window
    /// apart when the program hands the launch to another process or app (roadmap M6.8). Null without a process name, and for
    /// one that names none once normalized ('.exe', review finding F4): the launch would take a new window of any process. The
    /// planner refuses such a placement before it launches; this keeps the hint from ever being looser than the matcher.
    /// </summary>
    private static LaunchWindowHint? ExpectedWindow(WindowPlacement placement) =>
        LayoutPlanner.NamesAProcess(placement.Matcher.ProcessName) ? new LaunchWindowHint(ProcessName: placement.Matcher.ProcessName) : null;

    /// <summary>
    /// The launch placement plan of a placement whose window was launched (roadmap M3.F27), as <see cref="Arrange"/> places an
    /// open window: snapped on its monitor; a maximized placement given its restore rectangle and then maximized (maximized on
    /// its monitor when it has none); any other given the placement's rectangle as the window rect
    /// (<see cref="BoundsFrame.Outer"/>), the rect layouts save, and minimized afterwards for state minimized. Every plan keeps
    /// the monitor's id (or device name), and a rectangle also as fractions of the work area, so the window is placed on the
    /// monitor as it is when the window appears (roadmap M6.8, review #5). The plan is made by
    /// <see cref="LaunchPlacementPlan.ForLayout"/>, which states and enforces the layout's rules; a placement with focus and
    /// state minimized stays minimized and is not focused, as <see cref="Focuses"/> has it for an open window.
    /// </summary>
    private static LaunchPlacementPlan LaunchPlan(PlannedPlacement plan)
    {
        var placement = plan.Placement;
        var monitor = plan.Monitor!;
        var minimized = placement.State == WindowState.Minimized ? LaunchWindowState.Minimized : (LaunchWindowState?)null;
        var key = monitor.Id ?? monitor.DeviceName;
        if (plan.Snap is { } snap)
            return LaunchPlacementPlan.ForLayout(key, monitor.Index, snap: snap, state: minimized, focus: placement.Focus);

        // A maximized placement with a restore rectangle is given it before it is maximized, as an open window is
        if (placement.State == WindowState.Maximized && plan.NormalTarget is null)
            return LaunchPlacementPlan.ForLayout(key, monitor.Index, state: LaunchWindowState.Maximized, focus: placement.Focus);

        // The rectangle as fractions too, so it is placed on the monitor's work area as it is when the window appears
        var maximized = placement.State == WindowState.Maximized;
        return LaunchPlacementPlan.ForLayout(key, monitor.Index,
            bounds: maximized ? plan.NormalTarget : plan.Target,
            relativeBounds: maximized ? placement.NormalPosition : placement.Position,
            state: maximized ? LaunchWindowState.Maximized : minimized,
            focus: placement.Focus);
    }

    /// <summary>
    /// Puts the window where the placement says, through the window service's verified geometry (roadmap M2.3, M6.8): snapped
    /// on its monitor; a maximized placement first given its restore rectangle, then maximized, so it restores to that
    /// rectangle (one already maximized on its monitor is left alone; one without a restore rectangle is maximized on its
    /// monitor); a window that is minimized and stays minimized given its restore rectangle without being shown
    /// (<paramref name="keptMinimized"/>); any other window given the placement's rectangle. SetWindowBounds restores a
    /// minimized or maximized window to normal first, without activating it (SW_SHOWNOACTIVATE); maximizing activates the
    /// window (Windows has no maximize that does not).
    /// </summary>
    private WindowChangeResult Arrange(WindowInfo window, PlannedPlacement plan, bool keptMinimized)
    {
        var handle = window.Handle;
        var monitor = plan.Monitor!;
        if (plan.Snap is { } snap)
            return _windowService.SnapWindow(handle, snap, monitor.Index);

        if (keptMinimized)
        {
            var restore = plan.NormalTarget ?? plan.Target!;
            return _windowService.SetMinimizedBounds(handle, restore.X, restore.Y, restore.Width, restore.Height);
        }

        if (plan.Placement.State == WindowState.Maximized)
        {
            if (window.State == WindowState.Maximized && window.MonitorIndex == monitor.Index)
                return WindowChangeResult.Done(handle);

            if (plan.NormalTarget is not { } normal)
                return _windowService.MoveWindowToMonitor(handle, monitor.Index, MonitorPositioning.Maximize);

            var placed = _windowService.SetWindowBounds(handle, normal.X, normal.Y, normal.Width, normal.Height);
            return placed.Success ? _windowService.SetWindowState(handle, WindowStateRequest.Maximize) : placed;
        }

        var target = plan.Target!;
        return _windowService.SetWindowBounds(handle, target.X, target.Y, target.Width, target.Height);
    }
}
