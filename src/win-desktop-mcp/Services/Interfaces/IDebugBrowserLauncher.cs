using WinDesktopMcp.Models;
using WinDesktopMcp.Models.Results;

namespace WinDesktopMcp.Services.Interfaces;

/// <summary>launch_browser_with_debug's launch (roadmap M5.1); <see cref="IDebugBrowserLifecycle"/> calls it.</summary>
public interface IDebugBrowserLauncher
{
    /// <summary>
    /// Launches a browser with CDP (Chrome DevTools Protocol) debug port enabled, with a profile of its own and without
    /// first-run screens (roadmap M5.1). Success means the started browser is running and its port is known; a launch
    /// that handed off to a browser already running with the same profile fails with DebugLaunchFailed.
    /// </summary>
    /// <param name="request">The browser, url, port (0: the browser picks one), profile and .exe.</param>
    /// <param name="cancellationToken">Cancels the call, as for <see cref="IApplicationLauncher.LaunchApplicationAsync(LaunchRequest, bool, int, CancellationToken)"/>.</param>
    /// <returns>Result containing process ID, window handle, CDP port, user data directory and the launch status.</returns>
    Task<DebugBrowserLaunchResult> LaunchBrowserWithDebugAsync(
        DebugBrowserLaunchRequest request,
        CancellationToken cancellationToken = default);
}
