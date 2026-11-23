namespace Helmsman.FixtureApp;

/// <summary>
/// The GL frame counter window's last way out, independent of the window's own thread: every other way (its lifetime, Esc,
/// the close block, WM_CLOSE, the fixture exiting) is handled by that thread's render loop, so a driver that never returns
/// from SwapBuffers or from setting up the context would leave a full-monitor window over the user's monitor. A one-shot
/// <see cref="System.Threading.Timer"/> (a thread-pool thread), armed when the window is requested, checks at
/// <see cref="DeadlineFor"/> whether the window's thread is still alive and, if it is, calls the expiry action (the fixture
/// logs and ends its own process).
/// The thread disarms it when it ends, so in the normal path it never fires. The fixture uses it; the E2E project compiles
/// this file too (a linked source file) to test it without a desktop.
/// </summary>
public sealed class GlWindowWatchdog : IDisposable
{
    /// <summary>
    /// How long after the window's lifetime the watchdog waits: the lifetime runs from when the window is shown, the
    /// watchdog from when it is requested, so this covers setting up the window and the context, and releasing them.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private const int Armed = 0;
    private const int Disarmed = 1;
    private const int Expired = 2;

    private readonly Func<bool> _threadAlive;
    private readonly Action _expire;
    private readonly System.Threading.Timer _timer;
    private int _state = Armed;

    /// <summary>
    /// Arms a watchdog that calls <paramref name="expire"/> after <paramref name="deadline"/> if <paramref name="threadAlive"/>
    /// is then true.
    /// </summary>
    public GlWindowWatchdog(TimeSpan deadline, Func<bool> threadAlive, Action expire)
    {
        ArgumentNullException.ThrowIfNull(threadAlive);
        ArgumentNullException.ThrowIfNull(expire);
        if (deadline < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline), deadline, "The deadline cannot be negative.");
        }

        _threadAlive = threadAlive;
        _expire = expire;
        _timer = new System.Threading.Timer(_ => Check(), null, deadline, Timeout.InfiniteTimeSpan);
    }

    /// <summary>When the watchdog checks the window's thread, counted from when the window is requested.</summary>
    public static TimeSpan DeadlineFor(TimeSpan lifetime) => lifetime + Grace;

    /// <summary>Whether the watchdog ends the fixture: it is still armed and the window's thread is still alive.</summary>
    public static bool ShouldExpire(bool armed, bool threadAlive) => armed && threadAlive;

    /// <summary>Whether the expiry action was called.</summary>
    public bool HasExpired => Volatile.Read(ref _state) == Expired;

    /// <summary>The window's thread ended: the watchdog never fires.</summary>
    public void Disarm()
    {
        Interlocked.CompareExchange(ref _state, Disarmed, Armed);
        _timer.Dispose();
    }

    public void Dispose() => Disarm();

    private void Check()
    {
        if (!ShouldExpire(Volatile.Read(ref _state) == Armed, _threadAlive()) ||
            Interlocked.CompareExchange(ref _state, Expired, Armed) != Armed)
        {
            return;
        }

        _expire();
    }
}
