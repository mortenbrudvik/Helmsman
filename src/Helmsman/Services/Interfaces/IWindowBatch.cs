using Helmsman.Models;

namespace Helmsman.Services.Interfaces;

/// <summary>
/// The window batches: each changes one window after another, with delayBetweenMs (clamped to 0-5000) between two windows;
/// the cancellation token ends the batch between windows and during that delay: the windows already changed stay changed,
/// and the call throws OperationCanceledException. Each window gets its own result, in order: a window that fails, or does
/// not answer (NotResponding), does not stop the batch. The bounds batch places the visible frame, as set_window_bounds does.
/// </summary>
public interface IWindowBatch
{
    /// <inheritdoc cref="IWindowService.SetWindowBoundsBatchAsync"/>
    Task<IReadOnlyList<WindowChangeResult>> SetWindowBoundsBatchAsync(
        IReadOnlyList<(nint Handle, int X, int Y, int Width, int Height, bool BringToFront)> placements,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IWindowService.SnapWindowsBatchAsync"/>
    Task<IReadOnlyList<WindowChangeResult>> SnapWindowsBatchAsync(
        IReadOnlyList<(nint Handle, SnapPosition Position, int? MonitorIndex, bool BringToFront)> placements,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IWindowService.SetWindowStateBatchAsync"/>
    Task<IReadOnlyList<WindowChangeResult>> SetWindowStateBatchAsync(
        IReadOnlyList<(nint Handle, WindowStateRequest State)> changes,
        int delayBetweenMs = 30,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IWindowService.CloseWindowsBatchAsync"/>
    Task<IReadOnlyList<WindowCloseResult>> CloseWindowsBatchAsync(
        IReadOnlyList<nint> handles,
        bool force,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default);
}
