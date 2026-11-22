using Helmsman.Models;
using Helmsman.Models.Results;
using Helmsman.Services.Interfaces;
using static Helmsman.Services.LayoutPresetStore;

namespace Helmsman.Services;

/// <summary>
/// Makes the presets save_layout saves (<see cref="ILayoutCapturer"/>): from the open windows, or from the placements an
/// agent wrote, each checked before it is saved through the preset store.
/// </summary>
public class LayoutCapturer : ILayoutCapturer
{
    private readonly IWindowService _windowService;
    private readonly IMonitorService _monitorService;
    private readonly IProcessService _processService;
    private readonly ILayoutPresetStore _store;
    private readonly INativeAppsWrapper? _apps;
    private readonly IInstalledAppsService? _installedApps;

    /// <param name="store">Saves the presets.</param>
    /// <param name="apps">
    /// Tells a packaged app's window by its package and the AppUserModelID it carries, so save_layout records the app to
    /// launch rather than a program inside its package folder (roadmap M6.8). Without it every window records its program.
    /// </param>
    /// <param name="installedApps">Finds a packaged app's AppUserModelID among the installed apps when its window carries none.</param>
    public LayoutCapturer(
        IWindowService windowService,
        IMonitorService monitorService,
        IProcessService processService,
        ILayoutPresetStore store,
        INativeAppsWrapper? apps = null,
        IInstalledAppsService? installedApps = null)
    {
        _windowService = windowService;
        _monitorService = monitorService;
        _processService = processService;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _apps = apps;
        _installedApps = installedApps;
    }

    public async Task<LayoutSaveResult> CaptureCurrentLayoutAsync(
        string name,
        string? description = null,
        string[]? includeProcesses = null,
        string[]? excludeProcesses = null,
        bool includeMinimized = false,
        CancellationToken cancellationToken = default)
    {
        var captured = CaptureLayout(name, description, includeProcesses, excludeProcesses, includeMinimized);
        return captured is { Success: true, Preset: { } preset } ? await _store.SavePresetAsync(preset, cancellationToken) : captured;
    }

    public LayoutSaveResult CaptureLayout(
        string name,
        string? description = null,
        string[]? includeProcesses = null,
        string[]? excludeProcesses = null,
        bool includeMinimized = false)
    {
        var nameError = ValidateName(name);
        if (nameError != null)
            return new LayoutSaveResult(false, null, ErrorMessage: nameError, ErrorCode: nameof(LayoutErrorCode.InvalidName));

        if (DescriptionError(description) is { } descriptionError)
            return new LayoutSaveResult(false, null, ErrorMessage: descriptionError, ErrorCode: Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode);

        // A filter that is a path would match no window, so an app it excludes would be saved anyway (ProcessNames)
        if ((ProcessFilterError("includeProcesses", includeProcesses) ?? ProcessFilterError("excludeProcesses", excludeProcesses)) is { } filterError)
            return new LayoutSaveResult(false, null, ErrorMessage: filterError, ErrorCode: Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode);

        var windows = _windowService.GetAllWindows(includeMinimized);
        if (_monitorService.GetAllMonitors().Count == 0)
        {
            // Every pass of the monitor enumeration failed or missed a monitor (a dock or undock under way), so there is no work
            // area to record a window's position in
            return MonitorsUnreadable(name, "No monitor could be read");
        }

        var placements = new List<WindowPlacement>();
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var exePaths = new Dictionary<int, string?>();
        var packages = new Dictionary<int, string?>();

        foreach (var window in windows)
        {
            // Filter by process name, the app's or (for UWP windows) the frame host's, as find_windows does
            if (includeProcesses?.Length > 0)
            {
                if (!includeProcesses.Any(p => ProcessNameContains(window, p)))
                    continue;
            }

            if (excludeProcesses?.Length > 0)
            {
                if (excludeProcesses.Any(p => ProcessNameContains(window, p)))
                    continue;
            }

            // The window's monitor, looked up again: a listing gives monitor index 0 also to a window whose monitor could not
            // be read while it was listed (the displays changing), and on monitor 0 the window would be recorded on another
            // monitor than its own, at a position measured against that monitor's work area. Its index, identity and work
            // area are then all of the one monitor read now
            if (_monitorService.GetMonitorForWindow(window.Handle) is not { } monitor)
            {
                // A window that has closed since it was listed is not part of the layout
                if (_windowService.GetWindowInfo(window.Handle) == null)
                    continue;

                return MonitorsUnreadable(name, $"The monitor of a window of {window.ProcessName} could not be read");
            }

            // Calculate relative position within monitor work area
            var workArea = monitor.WorkArea;

            // A maximized or minimized window also records where it returns to (roadmap M6.8). A normal window's restore
            // rectangle is not used: for a snapped window it is where the window was before the snap
            RelativePosition? normalPosition = null;
            if (window.State is WindowState.Maximized or WindowState.Minimized)
            {
                normalPosition = _windowService.GetNormalBounds(window.Handle) is { } normal ? RelativeTo(normal, workArea) : null;

                // A minimized window's rect is its icon, far off screen: without its restore rectangle there is nothing to save
                if (window.State == WindowState.Minimized && normalPosition == null)
                    continue;
            }

            // A minimized window's position is where it returns to, so an older server places it there before minimizing it
            var position = window.State == WindowState.Minimized ? normalPosition! : RelativeTo(window.Bounds, workArea);

            // Which of the process's windows this is, in z-order; apply uses it to break ties
            var ordinal = ordinals.GetValueOrDefault(window.ProcessName);
            ordinals[window.ProcessName] = ordinal + 1;

            // The title of a window the user's policy denies (a password manager's database name) is not written to disk, nor
            // what would start it
            var denied = _windowService.EvaluateTargetPolicy(window.Handle) is { Allowed: false };
            var exePath = ExePathOf(_processService, window.ProcessId, exePaths);

            var placement = new WindowPlacement(
                Matcher: new WindowMatcher(
                    TitleContains: null,
                    ProcessName: window.ProcessName,
                    ClassName: string.IsNullOrEmpty(window.ClassName) ? null : window.ClassName
                )
                {
                    ExactProcessName = true,
                    TitlePattern = denied || string.IsNullOrEmpty(window.Title) ? null : Truncate(window.Title, MaxTitlePatternLength),
                    ExePath = exePath,
                    Ordinal = ordinal
                },
                MonitorIndex: monitor.Index,
                Position: position,
                LaunchCommand: null,
                Focus: false,
                Order: placements.Count
            )
            {
                State = window.State,
                // The monitor by identity (roadmap M6.8): its id, which follows it on its port, or its device name without one
                Monitor = monitor.Id ?? monitor.DeviceName,
                MonitorName = monitor.FriendlyName,
                NormalPosition = normalPosition,
                Launch = denied ? null : LaunchOf(window, exePath, packages)
            };

            placements.Add(placement);
        }

        var preset = new LayoutPreset(
            Name: name,
            Description: description,
            Placements: placements
        )
        {
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        if (placements.Count == 0)
        {
            // Saving an empty layout would silently replace a good preset with one that does nothing.
            return new LayoutSaveResult(
                false,
                preset,
                ErrorMessage: "No visible windows matched the capture filters, so nothing was saved. Check includeProcesses/excludeProcesses.",
                ErrorCode: nameof(LayoutErrorCode.NoWindowsCaptured));
        }

        return new LayoutSaveResult(true, preset);
    }

    /// <summary>
    /// save_layout's failure when the monitors cannot be read: nothing is saved, so an existing preset of that name is kept.
    /// </summary>
    private static LayoutSaveResult MonitorsUnreadable(string name, string what) => new(false, null,
        ErrorMessage: $"{what} (Windows did not list the displays, as happens while they are changing), so nothing was saved " +
                      $"and any preset named '{name}' was kept. Try again in a moment; get_monitors lists the monitors once they " +
                      "can be read.",
        ErrorCode: nameof(WindowErrorCode.MonitorNotFound));

    /// <summary>
    /// A rectangle as fractions of a work area, clamped as save_layout has always clamped it (x and y 0 to 1, sizes 0.1 to 1),
    /// and moved back inside the work area when it reaches past it (<see cref="LayoutPresetSchema.InsideWorkArea"/>), so a
    /// captured preset goes back into save_layout's placements.
    /// </summary>
    private static RelativePosition RelativeTo(WindowRect bounds, MonitorRect workArea) => LayoutPresetSchema.InsideWorkArea(new(
        Math.Clamp((double)(bounds.X - workArea.X) / workArea.Width, 0, 1),
        Math.Clamp((double)(bounds.Y - workArea.Y) / workArea.Height, 0, 1),
        Math.Clamp((double)bounds.Width / workArea.Width, 0.1, 1),
        Math.Clamp((double)bounds.Height / workArea.Height, 0.1, 1)));

    public async Task<LayoutSaveResult> SaveAuthoredLayoutAsync(
        string name,
        string? description,
        IReadOnlyList<LayoutPlacementSpec> placements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placements);
        var nameError = ValidateName(name);
        if (nameError != null)
            return new LayoutSaveResult(false, null, ErrorMessage: nameError, ErrorCode: nameof(LayoutErrorCode.InvalidName));

        if (DescriptionError(description) is { } descriptionError)
            return new LayoutSaveResult(false, null, ErrorMessage: descriptionError, ErrorCode: Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode);

        if (placements.Count is 0 or > LayoutPresetSchema.MaxPlacements)
        {
            return new LayoutSaveResult(false, null,
                ErrorMessage: $"placements takes 1 to {LayoutPresetSchema.MaxPlacements} placements, but it has {placements.Count}. Nothing was saved.",
                ErrorCode: nameof(LayoutErrorCode.InvalidPlacement));
        }

        // Every placement is checked before anything is written (roadmap M6.8)
        var monitors = _monitorService.GetAllMonitors();
        var built = new List<WindowPlacement>(placements.Count);
        for (var i = 0; i < placements.Count; i++)
        {
            if (placements[i] is not { } spec)
            {
                return new LayoutSaveResult(false, null, ErrorMessage: $"placements[{i}] is empty. Nothing was saved.",
                    ErrorCode: nameof(LayoutErrorCode.InvalidPlacement));
            }

            if (!LayoutPresetSchema.TryBuildPlacement(spec, i, monitors, out var placement, out var error))
            {
                return new LayoutSaveResult(false, null, ErrorMessage: $"{error} Nothing was saved.",
                    ErrorCode: nameof(LayoutErrorCode.InvalidPlacement));
            }

            built.Add(placement!);
        }

        var now = DateTime.UtcNow;
        var preset = new LayoutPreset(name, description, built)
        {
            CreatedAt = now,
            UpdatedAt = now,
            AuthoredBy = LayoutPresetSchema.AgentAuthor
        };

        // The user confirms these launches once, and sees every one of them (review M6.8 #1)
        if (LayoutPresetSchema.LaunchTargets(preset) is { Count: > LayoutPresetSchema.MaxLaunchTargets } targets)
        {
            return new LayoutSaveResult(false, null,
                ErrorMessage: $"placements start {targets.Count} different programs or apps, and a layout an agent writes may start at " +
                              $"most {LayoutPresetSchema.MaxLaunchTargets}: the user is asked about them all at once. Nothing was saved.",
                ErrorCode: nameof(LayoutErrorCode.InvalidPlacement));
        }

        return await _store.SavePresetAsync(preset, cancellationToken);
    }

    /// <summary>Why a preset's description cannot be saved: longer than <see cref="LayoutPresetSchema.MaxDescriptionLength"/>.</summary>
    private static string? DescriptionError(string? description) =>
        description?.Length > LayoutPresetSchema.MaxDescriptionLength
            ? $"description is longer than {LayoutPresetSchema.MaxDescriptionLength} characters. Nothing was saved."
            : null;

    /// <summary>
    /// Why a capture filter's entry cannot be used, or null when every entry can: it is a path
    /// (<see cref="ProcessNames.PathRefusal"/>), or it is blank once read (spaces, quotes, '.exe'), which would match no
    /// window, so an exclude filter would exclude nothing and an include filter include nothing (in 4.0.0 such an entry was
    /// part of every name and matched every window).
    /// </summary>
    private static string? ProcessFilterError(string parameter, string[]? names)
    {
        foreach (var name in names ?? [])
        {
            if (ProcessNames.PathRefusal(parameter, name) is { } path)
                return $"{path} Nothing was saved.";

            if (name == null || ProcessNames.Normalize(name).Length == 0)
            {
                return $"{parameter} has the entry '{name}', which is blank once spaces, quotes and '.exe' are taken off, so it " +
                       "names no process; give (part of) a process name, or leave the entry out. Nothing was saved.";
            }
        }

        return null;
    }

    #region Capture helpers

    /// <summary>The longest title save_layout records.</summary>
    private const int MaxTitlePatternLength = 260;

    /// <summary>
    /// True when <paramref name="name"/> is part of the window's process name or, for a UWP window, of
    /// its frame host's (ApplicationFrameHost). Presets saved before UWP windows were attributed to the app
    /// store ApplicationFrameHost as their process name.
    /// </summary>
    private static bool ProcessNameContains(WindowInfo window, string name) =>
        WindowMatching.ProcessNameMatches(window.ProcessName, window.HostProcessName, name, contains: true);

    /// <summary>The process's executable path, read once per process; null when it cannot be read.</summary>
    internal static string? ExePathOf(IProcessService processService, int processId, Dictionary<int, string?> cache)
    {
        if (cache.TryGetValue(processId, out var cached))
            return cached;

        string? path;
        try
        {
            path = processService.GetProcessPath(processId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            path = null;
        }

        cache[processId] = string.IsNullOrEmpty(path) ? null : path;
        return cache[processId];
    }

    /// <summary>
    /// What launchMissing would start for a captured window (roadmap M6.8, owner decision D15; never arguments): a packaged
    /// app's AppUserModelID, the one its window carries or its package's only app among the installed apps, since a program
    /// in a package's folder cannot be started directly; any other window's program. Null when it cannot be told: a packaged
    /// app with several apps and no AppUserModelID on its window, or a program whose path cannot be read.
    /// </summary>
    private LayoutLaunch? LaunchOf(WindowInfo window, string? exePath, Dictionary<int, string?> packages)
    {
        if (!packages.TryGetValue(window.ProcessId, out var family))
            packages[window.ProcessId] = family = _apps?.GetPackageFamilyName(window.ProcessId);

        if (family == null)
            return exePath == null ? null : new LayoutLaunch(Executable: exePath);

        if (PackagedAppId.TryParse(_apps!.GetWindowAppUserModelId(window.Handle), out var app) &&
            string.Equals(app.PackageFamilyName, family, StringComparison.OrdinalIgnoreCase))
        {
            return new LayoutLaunch(AppId: app.AppUserModelId);
        }

        var prefix = family + "!";
        var ofPackage = _installedApps?.List(prefix, MaxPackageApps).Apps
            .Where(a => a.Packaged && a.AppId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return ofPackage is { Count: 1 } ? new LayoutLaunch(AppId: ofPackage[0].AppId) : null;
    }

    /// <summary>How many installed apps of one package are read to find the one a window belongs to.</summary>
    private const int MaxPackageApps = 10;

    private static string Truncate(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength];

    #endregion
}
