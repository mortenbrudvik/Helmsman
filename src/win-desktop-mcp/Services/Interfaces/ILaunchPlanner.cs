using WinDesktopMcp.Models;
using WinDesktopMcp.Models.Results;

namespace WinDesktopMcp.Services.Interfaces;

/// <summary>
/// Plans a launch without starting anything: every launch route (launch_application, launch_applications_batch,
/// reuseExisting, apply_layout's check, restore_workspace, the runAsAdmin preview and launch_browser_with_debug) plans
/// through it before anything starts. Only the file system and the installed apps are read.
/// </summary>
public interface ILaunchPlanner
{
    /// <summary>
    /// Plans a runAsAdmin request without starting anything (roadmap M6.5, owner decision D11): the program and command
    /// line it would start, and whether the user must approve them through the client because Windows would not ask (User
    /// Account Control off or not asking administrators, the built-in Administrator outside Admin Approval Mode, a server
    /// that already runs elevated, or a policy that cannot be read). launch_application shows the user that launch before
    /// the input gate and passes the approved one as <see cref="LaunchRequest.ApprovedElevation"/>; without it such a launch
    /// is ElevationNotConfirmed.
    /// </summary>
    ElevationPreview PreviewElevation(LaunchRequest request);

    /// <summary>
    /// Plans a request as <see cref="IProcessService.LaunchApplicationAsync(LaunchRequest, bool, int, CancellationToken)"/>
    /// would, without starting anything (roadmap M6.8, review #3): the file system and the installed apps are only read. Null
    /// when the launch would be planned; otherwise the error code and message the launch would fail with (ScriptFile,
    /// InterpreterWithArguments, UnsafeChromiumSwitch, ExecutableNotFound, ...). apply_layout's dry run reports it, and a
    /// layout's launch is refused with it before the user is asked about it.
    /// </summary>
    LaunchRefusal? CheckLaunch(LaunchRequest request);

    /// <summary>
    /// Plans a request (<see cref="PlanLaunch"/>) with its runAsAdmin, which reuseExisting contradicts: an open window is
    /// not the new elevated copy. With runAsAdmin, only the launch the user approved plans, and an elevation Windows would
    /// not ask about needs the user's approval (ElevationNotConfirmed).
    /// </summary>
    LaunchPlan PlanFor(LaunchRequest request);

    /// <summary>
    /// Validates and resolves a launch request and builds the start info, without starting anything (see
    /// <see cref="LaunchPlanner.PlanLaunch"/> for the rules).
    /// </summary>
    LaunchPlan PlanLaunch(
        string executable, string[]? arguments = null, string? workingDirectory = null, bool forceAccessibility = false,
        bool runAsAdmin = false, bool debugBrowserLaunch = false);
}
