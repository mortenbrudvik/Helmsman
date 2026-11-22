using Helmsman.Models;

namespace Helmsman.Services.Interfaces;

/// <summary>
/// close_window's close (roadmap M2.5): the checks before WM_CLOSE is posted and the post, and the reads of the windows a
/// close leaves behind (the windows one owns, the prompt that blocks it, the application windows).
/// </summary>
public interface IWindowClosing
{
    /// <inheritdoc cref="IWindowService.CloseWindow"/>
    WindowCloseResult CloseWindow(nint handle, bool force);

    /// <inheritdoc cref="IWindowService.CheckClose"/>
    WindowCloseResult? CheckClose(nint handle, bool force);

    /// <inheritdoc cref="IWindowService.GetOwnedWindows"/>
    IReadOnlyList<OwnedWindowInfo> GetOwnedWindows(nint handle, bool includeHidden = false);

    /// <inheritdoc cref="IWindowService.FindBlockingDialog"/>
    ClosePrompt? FindBlockingDialog(nint handle);

    /// <inheritdoc cref="IWindowService.GetApplicationWindows"/>
    IReadOnlyList<WindowInfo> GetApplicationWindows();

    /// <inheritdoc cref="IWindowService.GetApplicationWindowsOnOtherDesktops"/>
    IReadOnlyList<WindowInfo> GetApplicationWindowsOnOtherDesktops();
}
