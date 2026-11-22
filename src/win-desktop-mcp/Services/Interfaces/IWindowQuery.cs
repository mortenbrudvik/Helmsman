using WinDesktopMcp.Models;

namespace WinDesktopMcp.Services.Interfaces;

/// <summary>
/// The window reads: the listings and their user-facing filter, a window selector resolved to exactly one window (roadmap
/// M2.4), one window read by handle or in full, the foreground window, and the target policy's verdict on a window (roadmap
/// M1.12). Only reads.
/// </summary>
public interface IWindowQuery
{
    /// <inheritdoc cref="IWindowService.GetAllWindows"/>
    IReadOnlyList<WindowInfo> GetAllWindows(bool includeMinimized = true);

    /// <inheritdoc cref="IWindowService.FindWindows(string?, string?, int?, nint?, bool)"/>
    IReadOnlyList<WindowInfo> FindWindows(
        string? titleContains = null,
        string? processName = null,
        int? processId = null,
        nint? handle = null,
        bool visibleOnly = true);

    /// <inheritdoc cref="IWindowService.FindWindows(WindowQuery)"/>
    IReadOnlyList<WindowInfo> FindWindows(WindowQuery query);

    /// <inheritdoc cref="IWindowService.ResolveWindow"/>
    WindowResolution ResolveWindow(WindowSelector selector);

    /// <inheritdoc cref="IWindowService.GetWindowDetails"/>
    WindowDetails? GetWindowDetails(nint handle);

    /// <inheritdoc cref="IWindowService.GetForegroundWindow"/>
    WindowInfo? GetForegroundWindow();

    /// <inheritdoc cref="IWindowService.GetWindowInfo"/>
    WindowInfo? GetWindowInfo(nint handle);

    /// <inheritdoc cref="IWindowService.IsUserFacingAppWindow"/>
    bool IsUserFacingAppWindow(nint handle);

    /// <inheritdoc cref="IWindowService.IsValidWindow"/>
    bool IsValidWindow(nint handle);

    /// <inheritdoc cref="IWindowService.EvaluateTargetPolicy"/>
    TargetPolicyVerdict EvaluateTargetPolicy(nint handle);
}
