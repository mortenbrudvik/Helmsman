using System.Diagnostics;
using Helmsman.Infrastructure;
using Helmsman.Models;
using Helmsman.Models.Results;
using Helmsman.Services.Interfaces;
using static Helmsman.Services.ProcessHelpers;

namespace Helmsman.Services;

/// <summary>
/// Plans a launch without starting anything (<see cref="ILaunchPlanner"/>): the validation, the program's resolution, the
/// interpreter and Chromium switch guards, packaged apps and installed apps' names, forceAccessibility and runAsAdmin
/// (<see cref="PlanLaunch"/>), and whether an elevated start needs the user's approval. Every launch route plans through
/// it before anything starts.
/// </summary>
public sealed class LaunchPlanner : ILaunchPlanner
{
    // Characters cmd.exe interprets in a command line, even inside quotes for % and !.
    // Control characters (line breaks in particular) are rejected as well.
    private static readonly char[] CmdMetacharacters = ['&', '|', '<', '>', '^', '%', '!', '"'];

    private const int MaxArgumentLengthInMessages = 80;

    private readonly IInputValidationService _validationService;
    private readonly IBrowserDetectionService _browserDetection;
    private readonly IExecutableResolver _executableResolver;
    private readonly ChromiumPrograms _chromiumPrograms;
    private readonly InterpreterGuard _interpreterGuard;
    private readonly ChromiumSwitchGuard _switchGuard;
    private readonly IProcessSecurityProbe _securityProbe;
    private readonly IInstalledAppsService? _installedApps;

    /// <param name="validationService">Validates the executable and the working directory.</param>
    /// <param name="browserDetection">Tells a browser opening a URL, which gets its new-window flag.</param>
    /// <param name="executableResolver">Resolves the program a launch starts: a bare name on PATH or in App Paths, a path, or
    /// a URI (roadmap M1.4).</param>
    /// <param name="chromiumPrograms">Tells which programs forceAccessibility may add --force-renderer-accessibility to
    /// (roadmap M3.3).</param>
    /// <param name="interpreterGuard">Refuses an interpreter with arguments and a script (roadmap M3.F8).</param>
    /// <param name="switchGuard">Refuses the Chromium and Electron switches that run a command, load code or open a program to
    /// remote control (review F2).</param>
    /// <param name="securityProbe">Reads the server's own integrity and whether Windows asks before it elevates (runAsAdmin,
    /// roadmap M6.5).</param>
    /// <param name="installedApps">The installed apps (roadmap M6.5): a bare name that is no program on disk is looked up by
    /// its exact display name, and an app's program is read before arguments are passed to it. The server always passes it;
    /// without it no name is looked up and no argument is passed to an app whose program the server cannot tell.</param>
    public LaunchPlanner(
        IInputValidationService validationService,
        IBrowserDetectionService browserDetection,
        IExecutableResolver executableResolver,
        ChromiumPrograms chromiumPrograms,
        InterpreterGuard interpreterGuard,
        ChromiumSwitchGuard switchGuard,
        IProcessSecurityProbe securityProbe,
        IInstalledAppsService? installedApps = null)
    {
        _validationService = validationService;
        _browserDetection = browserDetection;
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _chromiumPrograms = chromiumPrograms ?? throw new ArgumentNullException(nameof(chromiumPrograms));
        _interpreterGuard = interpreterGuard ?? throw new ArgumentNullException(nameof(interpreterGuard));
        _switchGuard = switchGuard ?? throw new ArgumentNullException(nameof(switchGuard));
        _securityProbe = securityProbe ?? throw new ArgumentNullException(nameof(securityProbe));
        _installedApps = installedApps;
    }

    public ElevationPreview PreviewElevation(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = request.ReuseExisting
            ? PlanFor(request with { RunAsAdmin = true })
            : PlanLaunch(request.Executable, request.Arguments, request.WorkingDirectory, request.ForceAccessibility, runAsAdmin: true);
        if (!plan.IsValid)
        {
            return new ElevationPreview(false, null, null, plan.ErrorCode, plan.ErrorMessage);
        }

        var reason = ElevationApprovalReason();
        return new ElevationPreview(reason != null, reason, ApprovalOf(plan));
    }

    public LaunchRefusal? CheckLaunch(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = PlanFor(request);
        return plan.IsValid
            ? null
            : new LaunchRefusal(plan.ErrorCode ?? ToolErrorReporting.InvalidArgumentsErrorCode, plan.ErrorMessage ?? "The launch could not be planned.");
    }

    /// <summary>The launch a plan would start, as the user approves it.</summary>
    private static ElevationApproval ApprovalOf(LaunchPlan plan) => new(plan.StartInfo!.FileName, plan.StartInfo.Arguments ?? string.Empty);

    /// <summary>
    /// Why the user must approve an elevated start through the client, or null when Windows asks them itself with a UAC
    /// prompt (owner decision D11; review M6.5 #1).
    /// </summary>
    private string? ElevationApprovalReason()
    {
        if (ServerIsElevated())
        {
            return "this server runs elevated, so a program it starts as administrator gets no UAC prompt";
        }

        return _securityProbe.GetElevationPrompt() switch
        {
            ElevationPrompt.Prompts => null,
            ElevationPrompt.Silent => "User Account Control does not ask on this computer (it is off, it does not ask " +
                                      "administrators, or this is the built-in Administrator account)",
            _ => "the User Account Control policy could not be read, so the server cannot tell whether Windows would ask"
        };
    }

    /// <summary>
    /// Plans a request (<see cref="PlanLaunch"/>) with its runAsAdmin, which reuseExisting contradicts: an open window is
    /// not the new elevated copy.
    /// </summary>
    public LaunchPlan PlanFor(LaunchRequest request)
    {
        // The window to expect is named by its process's name (part of it); a path would match no window, so the launch's
        // window would not be recognised and reuseExisting would start another copy
        if (ProcessNames.PathRefusal("expectWindow.processName", request.ExpectWindow?.ProcessName) is { } path)
        {
            return LaunchPlan.Rejected($"{path} Nothing was started.", ToolErrorReporting.InvalidArgumentsErrorCode);
        }

        if (request.RunAsAdmin && request.ReuseExisting)
        {
            return LaunchPlan.Rejected(
                "runAsAdmin starts a new copy of the program as administrator, and reuseExisting would use an open window instead: " +
                "leave one of them out. Nothing was started.",
                ToolErrorReporting.InvalidArgumentsErrorCode);
        }

        var plan = PlanLaunch(request.Executable, request.Arguments, request.WorkingDirectory, request.ForceAccessibility, request.RunAsAdmin,
            request.DebugBrowserLaunch);
        if (!plan.IsValid || !request.RunAsAdmin)
        {
            return plan;
        }

        // Review M6.5 #2: only the launch the user approved runs; a name can resolve to another program by now
        if (request.ApprovedElevation is { } approved)
        {
            var planned = ApprovalOf(plan);
            return planned == approved
                ? plan
                : LaunchPlan.Rejected(
                    $"The user approved starting '{approved.FileName}' {ShownArguments(approved.Arguments)} as administrator, but " +
                    $"the request now starts '{planned.FileName}' {ShownArguments(planned.Arguments)}. Nothing was started; launch " +
                    "again, so that the user is asked about what would start now.",
                    nameof(LaunchErrorCode.ElevationNotConfirmed),
                    plan.Warning);
        }

        return ElevationApprovalReason() is { } reason
            ? LaunchPlan.Rejected(ElevationNotConfirmedMessage(reason), nameof(LaunchErrorCode.ElevationNotConfirmed), plan.Warning)
            : plan;
    }

    private static string ShownArguments(string arguments) =>
        arguments.Length == 0 ? "with no arguments" : $"with the arguments '{AppText.Cut(arguments, 200)}'";

    /// <summary>
    /// Validates and resolves a launch request and builds the start info, without starting anything.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Bare names are resolved to an absolute path by <see cref="IExecutableResolver"/>, never from
    /// the working directory, and the resolved path is what gets started.</item>
    /// <item>Arguments are quoted with the Windows command-line rules (<see cref="CommandLineBuilder"/>).</item>
    /// <item>The server does not run commands or scripts for the agent (roadmap M3.F8): an interpreter or system tool with
    /// arguments, and a script, installer or shortcut file, are refused by <see cref="InterpreterGuard"/>. One of Windows'
    /// own consoles, which the guard allows, starts in its own folder (System32) whatever working directory was asked for,
    /// and the plan's warning says when one was not used.</item>
    /// <item>When the target cannot be verified to be a native executable (a URI, document or unresolved name, which
    /// may be handed to cmd.exe), arguments containing cmd.exe metacharacters are rejected, because cmd.exe
    /// ignores \" escaping and expands %VAR% even inside quotes.</item>
    /// <item><paramref name="forceAccessibility"/> (roadmap M3.3) puts --force-renderer-accessibility in front of the
    /// arguments, only for a program <see cref="ChromiumPrograms"/> recognizes (a Chromium browser, an Electron or a CEF
    /// app, started by its .exe) and only once; for any other target nothing is added and the plan's warning says why. In
    /// front, because Chromium reads no switch after a "--" argument.</item>
    /// <item>Roadmap M6.5: a packaged app's AppUserModelID, bare or as shell:AppsFolder\&lt;AUMID&gt;, is planned as an
    /// activation (<see cref="LaunchTargetKind.PackagedApp"/>). Arguments go to an app or a shell:AppsFolder entry only when
    /// its program is known and is no interpreter (<see cref="InterpreterGuard.CheckApp"/>). A bare name that is no program
    /// on disk is looked up among the installed apps by its exact display name (<see cref="IInstalledAppsService.FindByName"/>):
    /// one packaged app is planned as it, one desktop app as its program (through every check here), several are
    /// AmbiguousApp.</item>
    /// <item><paramref name="runAsAdmin"/> (roadmap M6.5) starts a program's .exe with the runas verb; any other target is
    /// refused. Whether the user must approve the elevation is decided by the launch, not here (PreviewElevation).</item>
    /// <item>Review F2: no target gets a switch with which Chromium, an Electron or a CEF app runs a command line, loads code
    /// or opens itself to remote control, nor a --user-data-dir in a browser's real profile (<see cref="ChromiumSwitchGuard"/>,
    /// UnsafeChromiumSwitch): every program's arguments are checked, since a launcher hands them on to the browser.
    /// <paramref name="debugBrowserLaunch"/> is launch_browser_with_debug's own start, whose --remote-debugging-port
    /// passes.</item>
    /// </list>
    /// </remarks>
    public LaunchPlan PlanLaunch(
        string executable, string[]? arguments = null, string? workingDirectory = null, bool forceAccessibility = false,
        bool runAsAdmin = false, bool debugBrowserLaunch = false)
    {
        // Validate executable path
        var executableValidation = _validationService.ValidateExecutable(executable);
        if (!executableValidation.IsValid)
        {
            return LaunchPlan.Rejected(executableValidation.Error, executableValidation.ErrorCode);
        }

        // Validate working directory
        var workingDirValidation = _validationService.ValidateWorkingDirectory(workingDirectory);
        if (!workingDirValidation.IsValid)
        {
            return LaunchPlan.Rejected(workingDirValidation.Error, workingDirValidation.ErrorCode);
        }

        // Combine warnings
        var warning = CombineWarnings(executableValidation.Warning, workingDirValidation.Warning);

        if (arguments != null && arguments.Any(a => a is null || a.Contains('\0')))
        {
            return LaunchPlan.Rejected(
                "Arguments must not be null or contain NUL characters.",
                nameof(LaunchErrorCode.InvalidArgumentValue),
                warning);
        }

        // Auto-inject --new-window for browsers opening URLs
        if (_browserDetection.IsBrowser(executable) && _browserDetection.ContainsUrl(arguments))
        {
            var newWindowFlag = _browserDetection.GetNewWindowFlag(executable);
            if (newWindowFlag != null && (arguments?.All(a => a != newWindowFlag) ?? true))
            {
                arguments = new[] { newWindowFlag }.Concat(arguments ?? []).ToArray();
            }
        }

        // The resolver checks the server's current directory itself; the working directory is checked when one was given
        var requestedWorkingDirectory = workingDirValidation.SanitizedPath ?? (string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory);

        // A packaged app is activated by its AppUserModelID; the resolver has nothing to find (roadmap M6.5)
        if (PackagedAppId.TryParse(executableValidation.SanitizedExecutable ?? executable, out var packagedApp))
        {
            return PlanPackagedApp(packagedApp, executable, arguments, requestedWorkingDirectory, forceAccessibility, runAsAdmin, warning);
        }

        var target = _executableResolver.Resolve(executableValidation.SanitizedExecutable ?? executable, requestedWorkingDirectory);

        if (target.FoundOnlyInWorkingDirectory != null)
        {
            return LaunchPlan.Rejected(
                $"'{executable}' was not found in the Windows directory, System32, PATH or App Paths, but " +
                $"'{target.FoundOnlyInWorkingDirectory}' exists in the current or working directory. Bare names are " +
                "never resolved from the working directory, to avoid running a planted program. Pass the full path " +
                "if you intend to run that file.",
                nameof(LaunchErrorCode.ExecutableInWorkingDirectory),
                warning);
        }

        if (target.NotFound)
        {
            if (PlanInstalledApp(executable, arguments, workingDirectory, requestedWorkingDirectory, forceAccessibility, runAsAdmin,
                    warning) is { } byName)
            {
                return byName;
            }

            return LaunchPlan.Rejected(
                $"'{executable}' was not found in the Windows directory, System32, the PATH entries or App Paths, and is no " +
                "installed app's exact name. A bare name is never passed to Windows unresolved, because Windows would also search " +
                "the server's current directory. Pass the program's full path, or an app's appId or path from list_installed_apps " +
                "(names are localized), or a URI such as ms-settings:.",
                nameof(LaunchErrorCode.ExecutableNotFound),
                warning);
        }

        if (_interpreterGuard.Check(executable, target.FileName, arguments, out var systemConsole) is { } refusal)
        {
            return LaunchPlan.Rejected(refusal.Message, refusal.ErrorCode, warning);
        }

        // A Windows console starts in its own folder, System32, whatever working directory was asked for: MMC may run elevated
        // for it, and must not load anything from a folder the agent chose
        var startDirectory = requestedWorkingDirectory ?? DefaultWorkingDirectory(target);
        if (systemConsole)
        {
            var ownFolder = DefaultWorkingDirectory(target);
            if (requestedWorkingDirectory != null &&
                !string.Equals(Path.TrimEndingDirectorySeparator(requestedWorkingDirectory), ownFolder, StringComparison.OrdinalIgnoreCase))
            {
                warning = CombineWarnings(warning,
                    $"workingDirectory '{requestedWorkingDirectory}' was not used: a Windows console always starts in its own " +
                    $"folder ({ownFolder}).");
            }

            startDirectory = ownFolder;
        }

        // A desktop app's shell:AppsFolder entry: the shell starts its program, which the guard above cannot see (roadmap
        // M6.5): a script is refused with or without arguments (review M6.5 #3), and arguments need a program that is known
        if (target.Kind == LaunchTargetKind.Other &&
            target.FileName.StartsWith(PackagedAppId.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var appId = target.FileName[PackagedAppId.AppsFolderPrefix.Length..];
            if (InterpreterGuard.CheckApp(appId, _installedApps?.FindByAppId(appId)?.Path, null, arguments) is { } appRefusal)
            {
                return LaunchPlan.Rejected(appRefusal.Message, appRefusal.ErrorCode, warning);
            }
        }

        // Review F2: whatever the target, since a launcher or an app's entry hands its arguments on to the browser
        if (_switchGuard.Check(executable, arguments, requestedWorkingDirectory ?? DefaultWorkingDirectory(target), debugBrowserLaunch) is { } switchRefusal)
        {
            return LaunchPlan.Rejected(switchRefusal.Message, switchRefusal.ErrorCode, warning);
        }

        var isNativeExecutable = target.Kind == LaunchTargetKind.NativeExecutable;
        if (!isNativeExecutable && arguments?.Length > 0)
        {
            var unsafeArgument = arguments.FirstOrDefault(ContainsCmdMetacharacters);
            if (unsafeArgument != null)
            {
                return LaunchPlan.Rejected(
                    BuildUnsafeBatchArgumentMessage(executable, unsafeArgument),
                    nameof(LaunchErrorCode.UnsafeBatchArguments),
                    warning);
            }
        }

        if (runAsAdmin)
        {
            if (!isNativeExecutable)
            {
                return LaunchPlan.Rejected(RunAsAdminOnlyExe(executable), ToolErrorReporting.InvalidArgumentsErrorCode, warning);
            }
        }

        bool? forceAccessibilityApplied = null;
        if (forceAccessibility)
        {
            var whyNot = isNativeExecutable
                ? _chromiumPrograms.WhyNot(target.FileName)
                : "it is not a program's .exe (a URI, document, shortcut or Store app)";
            forceAccessibilityApplied = whyNot == null;
            if (whyNot != null)
            {
                warning = CombineWarnings(warning, ForceAccessibilityNotApplied(executable, whyNot));
            }
            else if (arguments?.Any(ChromiumPrograms.IsForceAccessibilityArgument) != true)
            {
                arguments = [ChromiumPrograms.ForceRendererAccessibility, .. arguments ?? []];
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = target.FileName,
            UseShellExecute = true,
            WorkingDirectory = startDirectory
        };

        if (arguments?.Length > 0)
        {
            startInfo.Arguments = CommandLineBuilder.JoinArguments(arguments, quoteCmdDelimiters: !isNativeExecutable);
        }

        if (runAsAdmin)
        {
            startInfo.Verb = RunAsVerb;
        }

        return new LaunchPlan(startInfo, target.Kind, warning, Target: target, ForceAccessibilityApplied: forceAccessibilityApplied);
    }

    private static string ForceAccessibilityNotApplied(string executable, string whyNot) =>
        $"forceAccessibility was not applied to '{executable}': {whyNot}. {ChromiumPrograms.ForceRendererAccessibility} " +
        "is added only to a Chromium browser, an Electron or a CEF app started by its .exe (for VS Code, Code.exe, " +
        "not code.cmd).";

    private static string RunAsAdminOnlyExe(string executable) =>
        $"runAsAdmin starts only a program's .exe as administrator, and '{executable}' is not one (a URI, document, shortcut " +
        "or packaged app). Nothing was started.";

    private static string ElevationNotConfirmedMessage(string reason) =>
        $"Windows will not ask the user before it elevates this program: {reason}. So runAsAdmin needs the user's approval " +
        "through the client: launch_application asks for it when the client can ask (elicitation), and " +
        "launch_applications_batch never asks. Nothing was started. Ask the user to start the program as administrator.";

    /// <summary>
    /// Plans a packaged app's activation (roadmap M6.5): its shell:AppsFolder target (what the shell fallback starts), the
    /// arguments quoted for the app alone (activation runs no cmd.exe), and refused when the app is an interpreter or its
    /// program cannot be told (<see cref="InterpreterGuard.CheckApp"/>). Neither runAsAdmin nor forceAccessibility applies.
    /// </summary>
    private LaunchPlan PlanPackagedApp(
        PackagedAppId app, string executable, string[]? arguments, string? workingDirectory, bool forceAccessibility, bool runAsAdmin,
        string? warning)
    {
        if (runAsAdmin)
        {
            return LaunchPlan.Rejected(RunAsAdminOnlyExe(executable), ToolErrorReporting.InvalidArgumentsErrorCode, warning);
        }

        var program = arguments?.Length > 0 ? _installedApps?.ProgramOf(app) : null;
        if (InterpreterGuard.CheckApp(app.AppUserModelId, program, app.PackageFamilyName, arguments) is { } refusal)
        {
            return LaunchPlan.Rejected(refusal.Message, refusal.ErrorCode, warning);
        }

        // Review F2: a Store app built on Electron or CEF reads the switches from its activation's arguments too
        var target = new ResolvedLaunchTarget(app.AppsFolderTarget, LaunchTargetKind.PackagedApp) { App = app };
        if (_switchGuard.Check(executable, arguments, workingDirectory ?? DefaultWorkingDirectory(target)) is { } switchRefusal)
        {
            return LaunchPlan.Rejected(switchRefusal.Message, switchRefusal.ErrorCode, warning);
        }

        bool? forceAccessibilityApplied = null;
        if (forceAccessibility)
        {
            forceAccessibilityApplied = false;
            warning = CombineWarnings(warning, ForceAccessibilityNotApplied(executable, "it is a packaged app, not a program's .exe"));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = app.AppsFolderTarget,
            UseShellExecute = true,
            WorkingDirectory = workingDirectory ?? DefaultWorkingDirectory(target)
        };

        if (arguments?.Length > 0)
        {
            startInfo.Arguments = CommandLineBuilder.JoinArguments(arguments, quoteCmdDelimiters: false);
        }

        return new LaunchPlan(startInfo, LaunchTargetKind.PackagedApp, warning, Target: target, ForceAccessibilityApplied: forceAccessibilityApplied);
    }

    /// <summary>The most installed apps an AmbiguousApp error lists.</summary>
    private const int MaxAmbiguousApps = 5;

    /// <summary>
    /// A bare name that is no program on disk, looked up among the installed apps by its exact display name (roadmap M6.5,
    /// owner decision D11: never a part of a name, since any program can add a Start menu shortcut). Null when no installed
    /// app has the name (or the apps cannot be read), so the launch is ExecutableNotFound. One packaged app is planned as
    /// its activation; one desktop app as its program's path, through every check of <see cref="PlanLaunch"/> (a shortcut's
    /// own arguments are never used); several are AmbiguousApp.
    /// </summary>
    private LaunchPlan? PlanInstalledApp(
        string name, string[]? arguments, string? workingDirectory, string? requestedWorkingDirectory, bool forceAccessibility,
        bool runAsAdmin, string? warning)
    {
        if (_installedApps == null || name.IndexOfAny(['\\', '/', ':']) >= 0)
            return null;

        var apps = _installedApps.FindByName(name)?
            .Where(a => a.Packaged ? PackagedAppId.TryParse(a.AppId, out _) : a.Path != null && Path.IsPathFullyQualified(a.Path))
            .ToList();
        if (apps is not { Count: > 0 })
            return null;

        if (apps.Count > 1)
        {
            var listed = string.Join("; ", apps.Take(MaxAmbiguousApps).Select(a => a.Packaged
                ? $"'{a.Name}' appId {a.AppId}"
                : $"'{a.Name}' appId {a.AppId}, path {a.Path}"));
            var more = apps.Count > MaxAmbiguousApps ? $" and {apps.Count - MaxAmbiguousApps} more" : string.Empty;
            return LaunchPlan.Rejected(
                $"'{name}' is no program on disk, and {apps.Count} installed apps have that name: {listed}{more}. Launch one by its " +
                "appId (a packaged app) or path. Nothing was started.",
                nameof(LaunchErrorCode.AmbiguousApp),
                warning);
        }

        var app = apps[0];
        var note = $"'{name}' is no program on disk; it was resolved to the installed app '{app.Name}' " +
                   (app.Packaged ? $"(appId {app.AppId})." : $"({app.Path}).");
        if (app.Packaged && PackagedAppId.TryParse(app.AppId, out var packaged))
        {
            return PlanPackagedApp(packaged, name, arguments, requestedWorkingDirectory, forceAccessibility, runAsAdmin, CombineWarnings(warning, note));
        }

        // Planned again from its path, so the program gets every check a path gets; that plan validates the working directory
        // again and repeats its warning, so only the name's note is added
        var plan = PlanLaunch(app.Path!, arguments, workingDirectory, forceAccessibility, runAsAdmin);
        return plan with { Warning = CombineWarnings(note, plan.Warning) };
    }

    /// <summary>
    /// The working directory of a launch that names none: the folder of the program or document, never the server's
    /// current directory (a repository the agent works in, from which the program could load planted files). A URI gets
    /// the user's profile folder, as the Run dialog would.
    /// </summary>
    private static string DefaultWorkingDirectory(ResolvedLaunchTarget target)
    {
        try
        {
            if (Path.IsPathFullyQualified(target.FileName) && Path.GetDirectoryName(target.FileName) is { Length: > 0 } folder)
            {
                return folder;
            }
        }
        catch (ArgumentException)
        {
            // Not a path: a URI
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? Environment.SystemDirectory : profile;
    }

    private static bool ContainsCmdMetacharacters(string argument)
    {
        return argument.IndexOfAny(CmdMetacharacters) >= 0 || argument.Any(char.IsControl);
    }

    private static string BuildUnsafeBatchArgumentMessage(string executable, string argument)
    {
        var shownArgument = argument.Length > MaxArgumentLengthInMessages
            ? argument[..MaxArgumentLengthInMessages] + "..."
            : argument;

        // A batch file never gets here: InterpreterGuard refuses it as a script
        var reason = $"'{executable}' could not be verified to be a native .exe (it is a URI, document, shortcut or an " +
                     "unresolved name), so it may be run through cmd.exe";

        return $"{reason}, which would interpret characters in the argument '{shownArgument}' " +
               "(& | < > ^ % ! \" or control characters) as commands or variables. Remove those characters, " +
               "or launch the program's .exe directly by its full path.";
    }

    /// <summary>Whether the server itself runs elevated; false when that cannot be read.</summary>
    private bool ServerIsElevated() => ProcessHelpers.ServerIsElevated(_securityProbe);
}
