using WinDesktopMcp.Models;
using WinDesktopMcp.Models.Results;

namespace WinDesktopMcp.Services.Interfaces;

/// <summary>
/// save_layout's presets, behind <see cref="ILayoutService"/>: captured from the open windows (a denied window's title and
/// launch left out), or made from the placements an agent wrote; each saved through <see cref="ILayoutPresetStore"/>.
/// </summary>
public interface ILayoutCapturer
{
    /// <inheritdoc cref="ILayoutService.CaptureCurrentLayoutAsync"/>
    Task<LayoutSaveResult> CaptureCurrentLayoutAsync(
        string name,
        string? description = null,
        string[]? includeProcesses = null,
        string[]? excludeProcesses = null,
        bool includeMinimized = false,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ILayoutService.CaptureLayout"/>
    LayoutSaveResult CaptureLayout(
        string name,
        string? description = null,
        string[]? includeProcesses = null,
        string[]? excludeProcesses = null,
        bool includeMinimized = false);

    /// <inheritdoc cref="ILayoutService.SaveAuthoredLayoutAsync"/>
    Task<LayoutSaveResult> SaveAuthoredLayoutAsync(
        string name,
        string? description,
        IReadOnlyList<LayoutPlacementSpec> placements,
        CancellationToken cancellationToken = default);
}
