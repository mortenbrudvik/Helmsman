using WinDesktopMcp.Models;
using WinDesktopMcp.Services.Interfaces;

namespace WinDesktopMcp.Services;

/// <summary>
/// The window batches (<see cref="IWindowBatch"/>): bounds, snap, state and close for several windows, one after another,
/// through the placements and the close, with a pause between two windows that the cancellation token ends.
/// </summary>
public sealed class WindowBatchService : IWindowBatch
{
    /// <summary>
    /// The longest delay the batches sleep between two windows, whatever delayBetweenMs asks for: they sleep inside
    /// the input gate, so an agent's value must not hold it for long.
    /// </summary>
    public const int MaxDelayBetweenMs = 5000;

    private readonly IWindowGeometry _geometry;
    private readonly IWindowClosing _closing;

    /// <param name="geometry">The placements and state changes each window of the bounds, snap and state batches gets.</param>
    /// <param name="closing">The close each window of the close batch gets.</param>
    public WindowBatchService(IWindowGeometry geometry, IWindowClosing closing)
    {
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _closing = closing ?? throw new ArgumentNullException(nameof(closing));
    }

    /// <summary>The delay a batch sleeps between two windows for <paramref name="delayBetweenMs"/>: 0 to <see cref="MaxDelayBetweenMs"/>.</summary>
    public static TimeSpan BatchDelay(int delayBetweenMs) =>
        TimeSpan.FromMilliseconds(Math.Clamp(delayBetweenMs, 0, MaxDelayBetweenMs));

    public Task<IReadOnlyList<WindowChangeResult>> SetWindowBoundsBatchAsync(
        IReadOnlyList<(nint Handle, int X, int Y, int Width, int Height, bool BringToFront)> placements,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default) =>
        RunBatchAsync(
            placements.Count,
            i => placements[i].Handle,
            i => _geometry.SetWindowFrame(placements[i].Handle, placements[i].X, placements[i].Y, placements[i].Width, placements[i].Height,
                placements[i].BringToFront),
            WindowErrorCode.MoveFailed,
            delayBetweenMs,
            cancellationToken);

    public Task<IReadOnlyList<WindowChangeResult>> SnapWindowsBatchAsync(
        IReadOnlyList<(nint Handle, SnapPosition Position, int? MonitorIndex, bool BringToFront)> placements,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default) =>
        RunBatchAsync(
            placements.Count,
            i => placements[i].Handle,
            i => _geometry.SnapWindow(placements[i].Handle, placements[i].Position, placements[i].MonitorIndex, placements[i].BringToFront),
            WindowErrorCode.MoveFailed,
            delayBetweenMs,
            cancellationToken);

    public Task<IReadOnlyList<WindowChangeResult>> SetWindowStateBatchAsync(
        IReadOnlyList<(nint Handle, WindowStateRequest State)> changes,
        int delayBetweenMs = 30,
        CancellationToken cancellationToken = default) =>
        RunBatchAsync(
            changes.Count,
            i => changes[i].Handle,
            i => _geometry.SetWindowState(changes[i].Handle, changes[i].State),
            WindowErrorCode.StateChangeFailed,
            delayBetweenMs,
            cancellationToken);

    /// <summary>
    /// Changes <paramref name="count"/> windows one after another. A window that fails does not stop the batch: its result
    /// says why, with NotResponding for a window that did not answer in time and <paramref name="failure"/> for an
    /// unexpected error. The batch pauses <paramref name="delayBetweenMs"/> after each window that was changed.
    /// </summary>
    private static async Task<IReadOnlyList<WindowChangeResult>> RunBatchAsync(
        int count,
        Func<int, nint> handleOf,
        Func<int, WindowChangeResult> change,
        WindowErrorCode failure,
        int delayBetweenMs,
        CancellationToken cancellationToken)
    {
        var results = new List<WindowChangeResult>(count);

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            WindowChangeResult result;
            try
            {
                result = change(i);
            }
            catch (WindowNotRespondingException ex)
            {
                result = WindowChangeResult.Failed(handleOf(i), WindowErrorCode.NotResponding, ex.Message);
            }
            catch (Exception ex)
            {
                result = WindowChangeResult.Failed(handleOf(i), failure, ex.Message);
            }

            results.Add(result);

            if (result.Success)
            {
                await PauseBetweenWindowsAsync(delayBetweenMs, i, count, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<WindowCloseResult>> CloseWindowsBatchAsync(
        IReadOnlyList<nint> handles,
        bool force,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default)
    {
        var results = new List<WindowCloseResult>();

        for (var i = 0; i < handles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var handle = handles[i];
            WindowCloseResult result;
            try
            {
                result = _closing.CloseWindow(handle, force);
            }
            catch (Exception ex)
            {
                result = WindowCloseResult.Failed(handle, WindowErrorCode.CloseFailed, ex.Message);
            }

            results.Add(result);

            // Pause only after a close message was actually sent
            if (result.Success)
            {
                await PauseBetweenWindowsAsync(delayBetweenMs, i, handles.Count, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    /// <summary>
    /// The batches' pause after window <paramref name="index"/> of <paramref name="count"/>: none after the last one,
    /// <see cref="BatchDelay"/> otherwise. A cancelled call ends the pause at once.
    /// </summary>
    private static Task PauseBetweenWindowsAsync(int delayBetweenMs, int index, int count, CancellationToken cancellationToken) =>
        delayBetweenMs > 0 && index < count - 1
            ? Task.Delay(BatchDelay(delayBetweenMs), cancellationToken)
            : Task.CompletedTask;
}
