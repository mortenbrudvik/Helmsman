using Helmsman.Models;
using Helmsman.Models.Results;
using Helmsman.Services.Interfaces;
using static Helmsman.Services.LaunchDocuments;
using static Helmsman.Services.ProcessHelpers;

namespace Helmsman.Services;

/// <summary>
/// reuseExisting (roadmap M3.13): finds the open window of the app a launch would start, so that the launch uses it instead
/// of starting the program (<see cref="FindOpenWindow"/>). The launcher creates it from its own collaborators.
/// </summary>
internal sealed class ReuseWindowFinder
{
    private readonly IWindowService _windowService;
    private readonly INativeProcessWrapper _processWrapper;
    private readonly INativeAppsWrapper? _appsWrapper;

    /// <param name="windowService">The open application windows, and the target policy's verdict on each.</param>
    /// <param name="processWrapper">Reads a window's process's image path.</param>
    /// <param name="appsWrapper">Reads which package a window's process belongs to (roadmap M6.5); without it a packaged
    /// app's windows are not found by their package.</param>
    public ReuseWindowFinder(IWindowService windowService, INativeProcessWrapper processWrapper, INativeAppsWrapper? appsWrapper)
    {
        _windowService = windowService;
        _processWrapper = processWrapper;
        _appsWrapper = appsWrapper;
    }

    /// <summary>
    /// reuseExisting (roadmap M3.13): the open window to use instead of starting the program, as an
    /// <see cref="LaunchStatus.AlreadyOpen"/> result, or, when there is none, a note for the launch's warning (null when the
    /// server looked and no window of the app was open). A window counts when it is:
    /// <list type="bullet">
    /// <item>an application window the user can switch to (<see cref="IWindowService.GetApplicationWindows"/>: visible,
    /// not cloaked, so on the current virtual desktop, unowned, not a tool or shell window, not the server's own), minimized
    /// or not, that is not the agent's own host, not refused by the user's target policy, open before the batch started
    /// anything (<paramref name="openBefore"/>, null for a single launch) and not already used by an earlier item of the
    /// batch (<paramref name="taken"/>);</item>
    /// <item>of the same app: its process has the started program's path or name, or the name of the same app it hands
    /// off to (<see cref="LaunchWindowOwner.SameAppProcessNames"/>; never a terminal for a console program or a browser for
    /// a web address), and, when expectWindow names a process, that process (part of the name, also the frame host's). A
    /// launch whose app is not known (a document, a desktop app's shell:AppsFolder entry, a web address) needs
    /// expectWindow.processName; a packaged app's windows are those of its package's processes (roadmap M6.5);</item>
    /// <item>showing what the launch opens: its title contains expectWindow.titleContains when that is set; otherwise it
    /// shows every file and folder the launch opens (<see cref="LaunchDocuments.ReuseTitleNames"/>) as <see cref="LaunchTitles.Shows"/>
    /// tells: as a whole name, so todo.txt is not shown by old-todo.txt or todo.txt.bak, and never after another path, so
    /// C:\notes\todo.txt is not shown by "C:\other\todo.txt - Notepad++". Starting an app (shell:AppsFolder\&lt;AUMID&gt;, a
    /// bare scheme such as ms-settings:) opens nothing; a launch that opens something whose name a title would not show (an
    /// ms-settings: page, a mailto: address) is never reused, nor is a web address: a page sets its own title, so a host
    /// name in it proves nothing.</item>
    /// </list>
    /// A window whose title shows the launched full paths goes before one that shows only their names; of several, the
    /// front-most in z-order (the one used last) is used, and the warning says how many matched. A window matched by the
    /// names alone, or by expectWindow.titleContains for a web address (a title the page sets), is used with a warning that
    /// says so and tells the agent to check it before acting.
    /// </summary>
    public (LaunchResult? Result, string? Note) FindOpenWindow(
        LaunchPlan plan, LaunchRequest request, IReadOnlySet<nint>? taken, IReadOnlySet<nint>? openBefore)
    {
        var target = plan.Target!;
        var hint = request.ExpectWindow is { IsEmpty: false } expected ? expected : null;
        var names = LaunchWindowOwner.SameAppProcessNames(target);
        var imagePath = target.Kind == LaunchTargetKind.NativeExecutable && Path.IsPathFullyQualified(target.FileName) ? target.FileName : null;
        var wantedProcess = string.IsNullOrWhiteSpace(hint?.ProcessName) ? null : StripExe(hint.ProcessName.Trim());
        var family = target.Kind == LaunchTargetKind.PackagedApp && _appsWrapper != null ? target.App?.PackageFamilyName : null;
        if (names.Count == 0 && imagePath == null && wantedProcess == null && family == null)
        {
            return (null, "reuseExisting: the server cannot tell which app opens this target, so it did not look for an open window " +
                          "and started it. Pass expectWindow.processName to reuse a window of that app.");
        }

        var titleContains = string.IsNullOrWhiteSpace(hint?.TitleContains) ? null : hint.TitleContains.Trim();
        var webAddress = WebAddressOf(target, request.Arguments);
        IReadOnlyList<DocumentHint> titleNames = [];
        if (titleContains == null)
        {
            if (webAddress != null)
            {
                return (null, "reuseExisting: the launch opens a web address, and a web page sets its own title, so a browser window " +
                              "is never reused because its title shows the host; it was started. Pass expectWindow.titleContains to " +
                              "name the window to reuse.");
            }

            if (ReuseTitleNames(target, request.Arguments, plan.StartInfo!.WorkingDirectory) is not { } shownNames)
            {
                return (null, "reuseExisting: the launch opens something whose name a window title would not show, so no open window " +
                              "was reused and it was started. Pass expectWindow.titleContains to name the window to reuse.");
            }

            titleNames = shownNames;
        }

        var nameSet = new HashSet<string>(names.Select(StripExe), StringComparer.OrdinalIgnoreCase);
        var appWindows = _windowService.GetApplicationWindows()
            .Where(w => !w.IsAgentHost && taken?.Contains(w.Handle) != true && openBefore?.Contains(w.Handle) != false)
            .Where(w => (names.Count == 0 && imagePath == null && family == null) || nameSet.Contains(StripExe(w.ProcessName)) ||
                        (imagePath != null && string.Equals(_processWrapper.GetProcessImagePath(w.ProcessId), imagePath, StringComparison.OrdinalIgnoreCase)) ||
                        (family != null && string.Equals(_appsWrapper!.GetPackageFamilyName(w.ProcessId), family, StringComparison.OrdinalIgnoreCase)))
            .Where(w => wantedProcess == null || w.ProcessName.Contains(wantedProcess, StringComparison.OrdinalIgnoreCase) ||
                        w.HostProcessName?.Contains(wantedProcess, StringComparison.OrdinalIgnoreCase) == true)
            .Where(w => _windowService.EvaluateTargetPolicy(w.Handle) is not { Allowed: false })
            .ToList();

        // Each window that shows what the launch opens, with the names its title shows without their paths
        var matches = new List<(WindowInfo Window, List<DocumentHint> NameOnly)>();
        foreach (var w in appWindows)
        {
            if (titleContains != null)
            {
                if (w.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                    matches.Add((w, []));
                continue;
            }

            var shows = titleNames.Select(name => (Name: name, Shows: LaunchTitles.Shows(w.Title, name))).ToList();
            if (shows.All(s => s.Shows is TitleShows.FullPath or TitleShows.NameOnly))
                matches.Add((w, shows.Where(s => s.Shows == TitleShows.NameOnly).Select(s => s.Name).ToList()));
        }

        if (matches.Count == 0)
        {
            var wanted = titleContains != null ? $"'{titleContains}'" : string.Join(" and ", titleNames.Select(n => $"'{n.Name}'"));
            return (null, appWindows.Count == 0 ? null :
                $"reuseExisting: {appWindows.Count} open window(s) of the app were found, but no title shows {wanted}, so none was " +
                "reused and it was started.");
        }

        // A window that shows the launched paths is that file or folder; one that shows only the names may be another
        var best = matches.Any(m => m.NameOnly.Count == 0) ? matches.Where(m => m.NameOnly.Count == 0).ToList() : matches;
        var (window, nameOnly) = best[0];
        var check = nameOnly.Count > 0
            ? $"reuseExisting: only the name was matched: the title '{window.Title}' shows " +
              $"{string.Join(" and ", nameOnly.Select(n => $"'{n.Name}'"))} but not the path, so the window may show another file " +
              $"or folder of the same name. Check that it shows {string.Join(" and ", nameOnly.Select(n => n.FullPath ?? n.Name))} " +
              "before acting on it."
            : webAddress != null
                ? $"reuseExisting: the window '{window.Title}' was matched by expectWindow.titleContains against a title the page " +
                  $"controls: check that it shows {webAddress} before acting on it."
                : null;
        return (new LaunchResult(
            Success: true,
            WindowHandle: window.Handle,
            Warning: CombineWarnings(CombineWarnings(plan.Warning, check), best.Count == 1 ? null :
                $"reuseExisting: {best.Count} open windows matched; the front-most one was used. Nothing was started."),
            Status: LaunchStatus.AlreadyOpen,
            Window: window), null);
    }
}
