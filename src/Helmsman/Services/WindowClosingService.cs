using Helmsman.Models;
using Helmsman.Native;
using Helmsman.Services.Interfaces;
using static Helmsman.Services.WindowChangeFailures;
using static Helmsman.Services.WindowReads;

namespace Helmsman.Services;

/// <summary>
/// close_window's close (<see cref="IWindowClosing"/>): the checks before WM_CLOSE is posted (the target policy, the
/// desktop shell and the agent's host), the post itself, and the reads the verified close makes of the windows a close
/// leaves behind: the windows one owns, the prompt that blocks it, and the application windows on this and other
/// virtual desktops.
/// </summary>
public sealed class WindowClosingService : IWindowClosing
{
    private readonly INativeWindowWrapper _nativeWrapper;
    private readonly IHostGuard _hostGuard;
    private readonly INativeVirtualDesktopWrapper? _virtualDesktops;
    private readonly WindowReads _reads;
    private readonly WindowInfoReader _windowInfos;
    private readonly WindowTargetCheck _targetCheck;

    /// <param name="monitorService">Monitor lookups, for the windows it reports.</param>
    /// <param name="nativeWrapper">The window calls.</param>
    /// <param name="hostGuard">Recognizes the agent's host windows (close guard, WindowInfo.IsAgentHost).</param>
    /// <param name="targetPolicy">
    /// The server's target policy (roadmap M1.12). Closing a window refuses a window it denies, whatever force says.
    /// </param>
    /// <param name="virtualDesktops">
    /// The shell's virtual desktop manager, which tells a window on another desktop from a suspended or prelaunched frame
    /// that the shell cloaks the same way (<see cref="GetApplicationWindowsOnOtherDesktops"/>). The server always passes it;
    /// without it every window the shell cloaks counts as on another desktop.
    /// </param>
    public WindowClosingService(
        IMonitorService monitorService,
        INativeWindowWrapper nativeWrapper,
        IHostGuard hostGuard,
        ITargetPolicy targetPolicy,
        INativeVirtualDesktopWrapper? virtualDesktops = null)
    {
        _nativeWrapper = nativeWrapper;
        _hostGuard = hostGuard ?? throw new ArgumentNullException(nameof(hostGuard));
        _reads = new WindowReads(nativeWrapper);
        _windowInfos = new WindowInfoReader(nativeWrapper, monitorService, _hostGuard, _reads);
        _targetCheck = new WindowTargetCheck(nativeWrapper, _reads, targetPolicy ?? throw new ArgumentNullException(nameof(targetPolicy)));
        _virtualDesktops = virtualDesktops;
    }

    public WindowCloseResult CloseWindow(nint handle, bool force)
    {
        if (CheckClose(handle, force) is { } refused)
            return refused;

        // WM_CLOSE asks for a graceful close: the application can prompt to save, or cancel it
        var posted = _nativeWrapper.PostMessage(handle, NativeEnums.WM_CLOSE, nint.Zero, nint.Zero);
        return posted.Succeeded ? WindowCloseResult.Sent(handle) : CloseNotPosted(handle, posted.Win32Error);
    }

    public WindowCloseResult? CheckClose(nint handle, bool force)
    {
        if (!_reads.IsValidWindow(handle))
            return WindowCloseResult.Failed(handle, WindowErrorCode.InvalidHandle, InvalidHandleMessage);

        // The user's policy comes first: force is a tool argument and does not override it
        if (FindPolicyRefusal(handle) is { } refusal)
            return WindowCloseResult.Failed(handle, WindowErrorCode.TargetDeniedByPolicy, refusal);

        if (!force && FindCloseProtection(handle) is { } protection)
            return WindowCloseResult.Failed(handle, WindowErrorCode.ProtectedWindow, protection);

        return null;
    }

    /// <summary>
    /// The classes of the helper windows that close_application never closes, whatever their style: the text-input
    /// windows every GUI thread gets (Default IME, MSCTFIME UI) and the GDI+ hook window. They are hidden anyway; this
    /// keeps them out even when an app shows one.
    /// </summary>
    private static readonly HashSet<string> HelperWindowClasses =
        new(StringComparer.OrdinalIgnoreCase) { "IME", "MSCTFIME UI", "GDI+ Hook Window Class" };

    /// <summary>
    /// The classes of File Explorer's windows (CabinetWClass, and ExploreWClass of older versions): the only application
    /// windows of the shell's process, which draws the desktop, the taskbars and their flyouts too.
    /// </summary>
    private static readonly HashSet<string> FileExplorerWindowClasses =
        new(StringComparer.OrdinalIgnoreCase) { "CabinetWClass", "ExploreWClass" };

    /// <summary>The longest prompt text the close results carry; longer text is cut with an ellipsis.</summary>
    public const int MaxPromptTextLength = 1000;

    /// <summary>The most button captions a prompt reports.</summary>
    public const int MaxPromptButtons = 10;

    public IReadOnlyList<OwnedWindowInfo> GetOwnedWindows(nint handle, bool includeHidden = false)
    {
        if (!_reads.IsValidWindow(handle))
            return [];

        // The same dialog FindBlockingDialog reports as the prompt, so isBlocking agrees with the close tools
        var blocking = FindBlockingWindow(handle)?.Window;
        var session = new ReadSession(_nativeWrapper, topLevelWindows: null);
        var owned = new List<OwnedWindowInfo>();
        foreach (var window in OwnedTopLevelWindows(handle))
        {
            if (!includeHidden && !_nativeWrapper.IsWindowVisible(window))
                continue;

            var className = _nativeWrapper.GetClassName(window) ?? string.Empty;
            var title = _nativeWrapper.GetWindowText(window) ?? string.Empty;
            var process = _reads.ResolveProcess(window, className, title, _nativeWrapper.GetWindowThreadProcessId(window), session);
            if (_windowInfos.BuildWindowInfo(window, title, className, _nativeWrapper.IsCloaked(window), process, session) is { } info)
                owned.Add(new OwnedWindowInfo(info, WindowClassification.IsDialogClass(className), window == blocking));
        }

        return owned;
    }

    public ClosePrompt? FindBlockingDialog(nint handle)
    {
        if (!_reads.IsValidWindow(handle))
            return null;

        return FindBlockingWindow(handle) is { } blocking ? ReadDialog(blocking.Window, blocking.Kind) : null;
    }

    /// <summary>The window of the prompt that blocks <paramref name="handle"/>, and how it was found, or null.</summary>
    private (nint Window, ClosePromptKind Kind)? FindBlockingWindow(nint handle)
    {
        // A modal dialog disables its owner and is the owner's enabled popup
        if (EnabledPopupOfDisabled(handle) is { } popup && IsShownPopup(popup))
            return (popup, ClosePromptKind.ModalPopup);

        // A dialog of an owned window (the app's hidden owner or a tool window) disables that window instead
        var windowDisabled = IsDisabled(handle);
        foreach (var window in OwnedTopLevelWindows(handle))
        {
            if (WindowClassification.IsDialogClass(_nativeWrapper.GetClassName(window)) && IsShownPopup(window) &&
                (windowDisabled || IsDisabled(_nativeWrapper.GetWindow(window, NativeEnums.GW_OWNER))))
            {
                return (window, ClosePromptKind.OwnedDialog);
            }
        }

        return null;
    }

    public IReadOnlyList<WindowInfo> GetApplicationWindows() => ApplicationWindows(otherDesktops: false);

    public IReadOnlyList<WindowInfo> GetApplicationWindowsOnOtherDesktops()
    {
        var cloaked = ApplicationWindows(otherDesktops: true);
        if (_virtualDesktops == null || cloaked.Count == 0)
            return cloaked;

        // The shell also cloaks suspended and prelaunched frames: the virtual desktop manager puts those on no desktop. A
        // window it cannot place is kept, so a window with unsaved work on another desktop is never left out of the report
        var placements = _virtualDesktops.GetPlacements(cloaked.Select(w => w.Handle).ToList());
        return cloaked
            .Where((_, i) => i >= placements.Count ||
                             placements[i] is VirtualDesktopPlacement.OtherDesktop or VirtualDesktopPlacement.Unknown)
            .ToList();
    }

    /// <summary>
    /// The Alt-Tab set: the windows that are not cloaked, or with <paramref name="otherDesktops"/> those the shell cloaks
    /// (on another virtual desktop, or a suspended or prelaunched frame) and that would be in it otherwise.
    /// </summary>
    private List<WindowInfo> ApplicationWindows(bool otherDesktops)
    {
        var topLevelWindows = EnumerateTopLevelWindows(_nativeWrapper);
        var session = new ReadSession(_nativeWrapper, topLevelWindows);
        var windows = new List<WindowInfo>();
        uint? shellProcessId = null;
        foreach (var window in topLevelWindows)
        {
            if (!_nativeWrapper.IsWindowVisible(window))
                continue;

            var className = _nativeWrapper.GetClassName(window) ?? string.Empty;
            if (WindowClassification.IsShellWindowClass(className) || HelperWindowClasses.Contains(className) ||
                WindowClassification.IsCoreWindow(className))
                continue;

            // Alt-Tab: no tool window, and an owned or no-activate window only with WS_EX_APPWINDOW
            var extendedStyle = (long)_nativeWrapper.GetWindowLongPtr(window, NativeEnums.GWL_EXSTYLE);
            var appWindow = (extendedStyle & NativeEnums.WS_EX_APPWINDOW) != 0;
            if (WindowClassification.IsToolWindow((nint)extendedStyle) ||
                (!appWindow && (extendedStyle & NativeEnums.WS_EX_NOACTIVATE) != 0) ||
                (!appWindow && _nativeWrapper.GetWindow(window, NativeEnums.GW_OWNER) != nint.Zero))
                continue;

            if (otherDesktops)
            {
                // Cloaked by the shell: on another virtual desktop, or a suspended or prelaunched frame, which
                // GetApplicationWindowsOnOtherDesktops tells apart (an app's own cloak is a hidden window)
                if ((_nativeWrapper.GetCloakReason(window) & NativeEnums.DWM_CLOAKED_SHELL) == 0)
                    continue;
            }
            else if (_nativeWrapper.IsCloaked(window))
            {
                continue;
            }

            var windowProcessId = _nativeWrapper.GetWindowThreadProcessId(window);
            if (windowProcessId == session.CurrentProcessId)
                continue;

            // The shell's own process (explorer.exe) also draws the taskbar's flyouts and other shell surfaces: of its
            // windows only File Explorer's are application windows
            shellProcessId ??= _nativeWrapper.GetShellWindow() is var shell && shell != nint.Zero
                ? _nativeWrapper.GetWindowThreadProcessId(shell)
                : 0;
            if (windowProcessId == shellProcessId && !FileExplorerWindowClasses.Contains(className))
                continue;

            var title = _nativeWrapper.GetWindowText(window) ?? string.Empty;
            var process = _reads.ResolveProcess(window, className, title, windowProcessId, session);
            if (_windowInfos.BuildWindowInfo(window, title, className, isCloaked: otherDesktops, process, session) is { } info)
                windows.Add(info);
        }

        return windows;
    }

    /// <summary>
    /// The top-level windows that <paramref name="owner"/> owns, directly or through other owned windows, in z-order.
    /// The owner chain of each window is read up to <see cref="WindowOwnerChain.MaxOwners"/> owners.
    /// </summary>
    private List<nint> OwnedTopLevelWindows(nint owner)
    {
        var owned = new List<nint>();
        foreach (var window in EnumerateTopLevelWindows(_nativeWrapper))
        {
            if (window == owner)
                continue;

            var current = window;
            for (var i = 0; i < WindowOwnerChain.MaxOwners; i++)
            {
                current = _nativeWrapper.GetWindow(current, NativeEnums.GW_OWNER);
                if (current == nint.Zero || current == window)
                    break;

                if (current == owner)
                {
                    owned.Add(window);
                    break;
                }
            }
        }

        return owned;
    }

    /// <summary>The enabled popup (GW_ENABLEDPOPUP) of a disabled window, or null when the window is enabled or has none.</summary>
    private nint? EnabledPopupOfDisabled(nint window)
    {
        if (!IsDisabled(window))
            return null;

        var popup = _nativeWrapper.GetWindow(window, NativeEnums.GW_ENABLEDPOPUP);
        return popup == nint.Zero || popup == window ? null : popup;
    }

    /// <summary>Whether the window has WS_DISABLED: a modal dialog took its input. False for zero.</summary>
    private bool IsDisabled(nint window) =>
        window != nint.Zero && ((long)_nativeWrapper.GetWindowLongPtr(window, NativeEnums.GWL_STYLE) & NativeEnums.WS_DISABLED) != 0;

    /// <summary>A popup the user can see: visible and not cloaked.</summary>
    private bool IsShownPopup(nint window) =>
        _nativeWrapper.IsWindowVisible(window) && !_nativeWrapper.IsCloaked(window);

    /// <summary>
    /// A dialog as a prompt: its title and class, the text of its Static controls and the captions of its buttons
    /// (Button controls, accelerator ampersands removed). GetWindowText reads a control of another process from the
    /// text Windows keeps for it, without sending it a message, so a hung dialog cannot block this.
    /// </summary>
    private ClosePrompt ReadDialog(nint dialog, ClosePromptKind kind)
    {
        var children = new List<nint>();
        _nativeWrapper.EnumChildWindows(dialog, child =>
        {
            children.Add(child);
            return true;
        });

        var text = new List<string>();
        var buttons = new List<string>();
        foreach (var child in children)
        {
            var className = _nativeWrapper.GetClassName(child) ?? string.Empty;
            var childText = _nativeWrapper.GetWindowText(child) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(childText))
                continue;

            if (string.Equals(className, "Static", StringComparison.OrdinalIgnoreCase))
                text.Add(childText.Trim());
            else if (string.Equals(className, "Button", StringComparison.OrdinalIgnoreCase) && buttons.Count < MaxPromptButtons)
                buttons.Add(WithoutAccelerators(childText).Trim());
        }

        return new ClosePrompt(
            dialog,
            _nativeWrapper.GetWindowText(dialog) ?? string.Empty,
            _nativeWrapper.GetClassName(dialog) ?? string.Empty,
            PromptText(text),
            buttons,
            kind);
    }

    /// <summary>Prompt text lines as one text of at most <see cref="MaxPromptTextLength"/> characters, or null when there are none.</summary>
    public static string? PromptText(IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct());
        if (text.Length == 0)
            return null;

        return text.Length <= MaxPromptTextLength ? text : text[..(MaxPromptTextLength - 1)].TrimEnd() + "…";
    }

    /// <summary>A control caption without its accelerator marks: "&amp;Yes" is "Yes", and "&amp;&amp;" is one ampersand.</summary>
    public static string WithoutAccelerators(string caption)
    {
        var result = new System.Text.StringBuilder(caption.Length);
        for (var i = 0; i < caption.Length; i++)
        {
            if (caption[i] == '&')
            {
                if (i + 1 < caption.Length && caption[i + 1] == '&')
                {
                    result.Append('&');
                    i++;
                }

                continue;
            }

            result.Append(caption[i]);
        }

        return result.ToString();
    }

    /// <summary>
    /// Why closing the window could take down the desktop shell or end the agent's own session, or null
    /// when it cannot. The shell rule checks the window, the top-level window it is part of and every
    /// window that owns it (<see cref="WindowOwnerChain"/>: GW_OWNER as well as GA_ROOTOWNER, so an owned
    /// overlapped helper counts too): DefWindowProc destroys any window that gets WM_CLOSE, so a child or
    /// an owned helper popup of the taskbar (its task list, a tooltip) is as unsafe to close as the
    /// taskbar itself. A shell window is the shell's desktop window, or a window of a shell class that
    /// belongs to the shell's process (<see cref="WindowClassification.IsShellWindow"/>), so another app's
    /// WorkerW helper is not one. A dialog that the user can see is checked alone, without its owners:
    /// WM_CLOSE on a dialog works like its Cancel button (DefDlgProc turns it into IDCANCEL), and an owned
    /// window that closes leaves its owner alone, so a dialog of the shell such as Shut Down Windows can be
    /// dismissed. The host rule is <see cref="IHostGuard.IsHostWindow"/>, which follows the owner chain on
    /// its own, so a dialog of the agent's host stays protected.
    /// </summary>
    private string? FindCloseProtection(nint handle)
    {
        var shellWindow = _nativeWrapper.GetShellWindow();
        var shellProcessId = shellWindow == nint.Zero ? 0u : _nativeWrapper.GetWindowThreadProcessId(shellWindow);
        var root = _nativeWrapper.GetAncestor(handle, WindowAncestor.Root);
        IReadOnlyList<nint> windows = IsVisibleTopLevelDialog(handle, root)
            ? [handle]
            : WindowOwnerChain.SelfAndOwners(_nativeWrapper, handle);

        foreach (var window in windows.Where(w => w != nint.Zero).Distinct())
        {
            var className = _nativeWrapper.GetClassName(window);
            if (WindowClassification.IsShellWindow(
                    window, className, _nativeWrapper.GetWindowThreadProcessId(window), shellWindow, shellProcessId))
            {
                var part = window == handle ? "is" : $"belongs to {window}, which is";
                return $"Window {handle} {part} part of the desktop shell ({className}): the desktop or a taskbar. " +
                       "Closing it can open the Shut Down Windows dialog or take down part of the shell. " +
                       "Nothing was sent. Pass force=true only if the user explicitly asked to close this window.";
            }
        }

        if (_hostGuard.IsHostWindow(handle))
        {
            // A window that counts as the host's only because the ancestors are unknown (HostGuard): the error says so
            var unknown = HostGuard.AncestorsUnknownNoteFor(_hostGuard, handle);
            return $"Window {handle} belongs to the agent's host: the terminal, editor or chat app this server was " +
                   $"started from, or a window that one owns.{unknown} Closing it can end the agent's own session. " +
                   "Nothing was sent. Pass force=true only if the user explicitly asked to close this window.";
        }

        return null;
    }

    /// <summary>
    /// Why the target policy refuses the window, or null when it allows it (or there is no policy). The
    /// window is matched by its process, and a UWP frame also by the app it hosts, as the listings report it;
    /// one whose process cannot be named is refused (<see cref="IWindowService.EvaluateTargetPolicy"/>).
    /// </summary>
    private string? FindPolicyRefusal(nint handle) =>
        _targetCheck.EvaluateTargetPolicy(handle) is { Allowed: false } verdict
            ? $"Window {handle} is refused by the server's target policy: {verdict.Reason}. Nothing was sent. The policy is set by " +
              "the user in the server's environment; force does not override it. Ask the user to close this window."
            : null;

    /// <summary>
    /// A dialog box (#32770) that the user can see and dismiss: visible and top-level, not a child
    /// dialog (a property page) inside another window. A hidden one is a helper window, not a prompt.
    /// </summary>
    private bool IsVisibleTopLevelDialog(nint handle, nint root) =>
        root == handle &&
        WindowClassification.IsDialogClass(_nativeWrapper.GetClassName(handle)) &&
        _nativeWrapper.IsWindowVisible(handle);

    /// <summary>Maps the Win32 error of a PostMessage(WM_CLOSE) that Windows refused.</summary>
    private static WindowCloseResult CloseNotPosted(nint handle, int win32Error) => win32Error switch
    {
        NativeEnums.ERROR_ACCESS_DENIED => WindowCloseResult.Failed(handle, WindowErrorCode.AccessDenied,
            $"Windows blocked the close message (Win32 error {win32Error}, access denied). The window most likely " +
            "belongs to an app running with higher privileges, for example as administrator: User Interface " +
            "Privilege Isolation blocks messages to it from this server. Nothing was closed."),
        NativeEnums.ERROR_INVALID_WINDOW_HANDLE => WindowCloseResult.Failed(handle, WindowErrorCode.InvalidHandle,
            $"The window no longer exists (Win32 error {win32Error}): it closed before the close message was sent."),
        // A full message queue: the application has stopped reading its messages
        NativeEnums.ERROR_NOT_ENOUGH_QUOTA => WindowCloseResult.Failed(handle, WindowErrorCode.NotResponding,
            $"Windows did not accept the close message (Win32 error {win32Error}): the window's message queue is full, so " +
            "the application is not responding. Nothing was closed. Wait and try again, or ask the user; kill_process ends " +
            "an app that stays hung, losing its unsaved work."),
        _ => WindowCloseResult.Failed(handle, WindowErrorCode.CloseFailed,
            $"Windows did not accept the close message (Win32 error {win32Error}: {new System.ComponentModel.Win32Exception(win32Error).Message}). " +
            "Nothing was closed.")
    };
}
