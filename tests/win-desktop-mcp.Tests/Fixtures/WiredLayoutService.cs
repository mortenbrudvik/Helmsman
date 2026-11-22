using Microsoft.Extensions.Logging;

namespace WinDesktopMcp.Tests.Fixtures;

/// <summary>
/// A <see cref="LayoutService"/> wired from its parts as ServiceRegistration wires them, for the tests that drive the
/// layout storage, capture, launch confirmations and apply through one object. Each <c>Create</c> takes the collaborators of
/// the LayoutService constructor it stands for, in the same order, and hands each part the ones it uses.
/// </summary>
public sealed class WiredLayoutService : LayoutService
{
    private WiredLayoutService(
        IWindowService windowService,
        IMonitorService monitorService,
        IProcessService processService,
        IForegroundService foregroundService,
        LayoutPresetStore store,
        LaunchConfirmationStore confirmations,
        ILaunchPlacementService launchPlacement,
        INativeAppsWrapper? apps,
        IInstalledAppsService? installedApps)
        : base(windowService, monitorService, processService, foregroundService, store, confirmations, launchPlacement, apps,
            installedApps)
    {
        Store = store;
        Confirmations = confirmations;
    }

    /// <summary>Stores the presets.</summary>
    public LayoutPresetStore Store { get; }

    /// <summary>The launches the user confirmed.</summary>
    public LaunchConfirmationStore Confirmations { get; }

    /// <summary>
    /// The parts wired over the presets in <paramref name="layoutsPath"/>, without migrating or seeding anything; when it is
    /// null, the default location from <see cref="LayoutStorageOptions.FromEnvironment"/> is used.
    /// </summary>
    public static WiredLayoutService Create(
        IWindowService windowService,
        IMonitorService monitorService,
        IProcessService processService,
        IForegroundService foregroundService,
        ILaunchPlacementService launchPlacement,
        string? layoutsPath = null) =>
        Create(
            windowService,
            monitorService,
            processService,
            foregroundService,
            layoutsPath == null ? LayoutStorageOptions.FromEnvironment() : LayoutStorageOptions.ForPath(layoutsPath),
            launchPlacement);

    /// <summary>The parts wired over the given storage options.</summary>
    public static WiredLayoutService Create(
        IWindowService windowService,
        IMonitorService monitorService,
        IProcessService processService,
        IForegroundService foregroundService,
        LayoutStorageOptions options,
        ILaunchPlacementService launchPlacement,
        ILayoutFolderAccess? folderAccess = null,
        INativeAppsWrapper? apps = null,
        IInstalledAppsService? installedApps = null,
        ILogger<LayoutService>? logger = null)
    {
        var store = new LayoutPresetStore(options, folderAccess, logger);
        var confirmations = new LaunchConfirmationStore(store.LayoutsPath);
        return new WiredLayoutService(windowService, monitorService, processService, foregroundService, store, confirmations,
            launchPlacement, apps, installedApps);
    }
}
