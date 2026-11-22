using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Helmsman.Services;

/// <summary>
/// Runs the <see cref="CaptureFileSweep"/> in the background while the server runs: <see cref="CaptureFileSweep.FirstSweepDelay"/>
/// after it starts, then every <see cref="CaptureFileSweep.Interval"/>, on a timer thread. A tool call never waits for a
/// sweep or sees its failure, and one sweep at a time runs in this server.
/// </summary>
/// <remarks>
/// Nothing is swept, listed or started when the service is created; only <see cref="StartAsync"/> starts the timer, so
/// resolving it (as the DI tests do) touches nothing.
/// </remarks>
public sealed class CaptureFileSweepStartup : IHostedService, IDisposable
{
    private readonly CaptureFileSweep _sweep;
    private readonly TimeProvider _clock;
    private readonly ILogger<CaptureFileSweepStartup>? _logger;
    private readonly object _lock = new();
    private ITimer? _timer;
    private int _sweeping;

    /// <param name="sweep">The sweep of the server's default captures folder.</param>
    /// <param name="logger">Gets a sweep that failed, at Debug.</param>
    /// <param name="clock">The clock whose timer runs the sweeps; default the system clock.</param>
    public CaptureFileSweepStartup(CaptureFileSweep sweep, ILogger<CaptureFileSweepStartup>? logger = null, TimeProvider? clock = null)
    {
        _sweep = sweep ?? throw new ArgumentNullException(nameof(sweep));
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _timer ??= _clock.CreateTimer(_ => SweepOnce(), null, CaptureFileSweep.FirstSweepDelay, CaptureFileSweep.Interval);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>One sweep, unless one still runs. Never throws: an exception on the timer's thread would end the server.</summary>
    private void SweepOnce()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1)
        {
            return;
        }

        try
        {
            _sweep.Sweep();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "The capture file sweep failed; the next one tries again");
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }
}
