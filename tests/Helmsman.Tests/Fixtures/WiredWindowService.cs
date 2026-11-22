namespace Helmsman.Tests.Fixtures;

/// <summary>
/// A <see cref="WindowService"/> wired from its parts as ServiceRegistration wires them, for the tests that drive the window
/// reads, changes, close and batches through one object. <see cref="Create"/> takes the collaborators WindowService took
/// before it was split into those parts, in the same order, and hands each part the ones it uses: one call runner and one
/// set of topmost pins, shared as the server shares them.
/// </summary>
public sealed class WiredWindowService : WindowService
{
    private WiredWindowService(
        WindowQueryService query,
        WindowGeometryService geometry,
        WindowZOrderService zOrder,
        WindowClosingService closing,
        WindowBatchService batch)
        : base(query, geometry, zOrder, closing, batch)
    {
    }

    /// <summary>
    /// The parts wired together. <paramref name="windowCalls"/> defaults to a <see cref="WindowCallRunner"/> over
    /// <paramref name="nativeWrapper"/> and <paramref name="topmostPins"/> to a new <see cref="TopmostPins"/>, as WindowService's
    /// own defaults did; <paramref name="stateChangeTimeout"/> left out is the production value.
    /// </summary>
    public static WiredWindowService Create(
        IMonitorService monitorService,
        INativeWindowWrapper nativeWrapper,
        IHostGuard hostGuard,
        ITargetPolicy targetPolicy,
        IWindowCallRunner? windowCalls = null,
        TimeSpan? stateChangeTimeout = null,
        INativeVirtualDesktopWrapper? virtualDesktops = null,
        TopmostPins? topmostPins = null)
    {
        var calls = windowCalls ?? new WindowCallRunner(nativeWrapper);
        var query = new WindowQueryService(monitorService, nativeWrapper, hostGuard, targetPolicy);
        var geometry = new WindowGeometryService(monitorService, nativeWrapper, targetPolicy, calls, stateChangeTimeout);
        var zOrder = new WindowZOrderService(nativeWrapper, targetPolicy, calls, topmostPins ?? new TopmostPins());
        var closing = new WindowClosingService(monitorService, nativeWrapper, hostGuard, targetPolicy, virtualDesktops);
        var batch = new WindowBatchService(geometry, closing);
        return new WiredWindowService(query, geometry, zOrder, closing, batch);
    }
}
