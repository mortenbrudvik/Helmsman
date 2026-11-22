using Helmsman.Models;

namespace Helmsman.Services.Interfaces;

/// <summary>
/// The window placements and state changes (roadmap M2.2 and M2.3): move, resize, bounds and frame, snap, move to a monitor,
/// minimize, maximize and restore, and a minimized window's restore rectangle. Each refuses an invalid handle and a window
/// the target policy denies before anything is sent (see <see cref="IWindowService"/>).
/// </summary>
public interface IWindowGeometry
{
    /// <inheritdoc cref="IWindowService.MoveWindow"/>
    WindowChangeResult MoveWindow(nint handle, int x, int y, bool bringToFront = true);

    /// <inheritdoc cref="IWindowService.ResizeWindow(nint, int, int)"/>
    WindowChangeResult ResizeWindow(nint handle, int width, int height);

    /// <inheritdoc cref="IWindowService.ResizeWindow(nint, int, int, bool)"/>
    WindowChangeResult ResizeWindow(nint handle, int width, int height, bool bringToFront);

    /// <inheritdoc cref="IWindowService.SetWindowBounds"/>
    WindowChangeResult SetWindowBounds(nint handle, int x, int y, int width, int height, bool bringToFront = true);

    /// <inheritdoc cref="IWindowService.SetWindowFrame"/>
    WindowChangeResult SetWindowFrame(nint handle, int x, int y, int width, int height, bool bringToFront = true);

    /// <inheritdoc cref="IWindowService.WaitForSettledFrame"/>
    void WaitForSettledFrame(nint handle, TimeSpan timeout);

    /// <inheritdoc cref="IWindowService.SetWindowState"/>
    WindowChangeResult SetWindowState(nint handle, WindowStateRequest state);

    /// <inheritdoc cref="IWindowService.GetNormalBounds"/>
    WindowRect? GetNormalBounds(nint handle);

    /// <inheritdoc cref="IWindowService.SetMinimizedBounds"/>
    WindowChangeResult SetMinimizedBounds(nint handle, int x, int y, int width, int height);

    /// <inheritdoc cref="IWindowService.SnapWindow"/>
    WindowChangeResult SnapWindow(nint handle, SnapPosition position, int? monitorIndex = null, bool bringToFront = true);

    /// <inheritdoc cref="IWindowService.MoveWindowToMonitor"/>
    WindowChangeResult MoveWindowToMonitor(nint handle, int monitorIndex, MonitorPositioning positioning = MonitorPositioning.Center, bool bringToFront = true);
}
