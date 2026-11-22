namespace WinDesktopMcp.Native;

/// <summary>
/// Runs a blocking call (a COM call into the shell, a WinRT call into the graphics driver) on a new background thread of its
/// own and waits for it at most a deadline. A call that misses the deadline is abandoned, not cancelled: its thread cannot be
/// interrupted, so it goes on, and when it returns its result is dropped. While an abandoned call has not returned, every
/// further call returns the fallback at once without starting a thread, so a hung shell or driver holds one thread, not one
/// per call. A call that throws, or whose thread cannot be started, also returns the fallback.
/// </summary>
/// <remarks>
/// Each instance counts its own abandoned calls: the wrapper that owns it is refused while its own call is stuck, and others
/// are not. The call's own cleanup (releasing the COM objects it made) runs on its thread when it returns, abandoned or not.
/// </remarks>
public sealed class DeadlineWorker
{
    private const int Running = 0, Finished = 1, Abandoned = 2;

    private readonly string _threadName;
    private readonly ApartmentState _apartment;
    private readonly TimeSpan _deadline;

    /// <summary>Calls that missed the deadline and have not returned yet.</summary>
    private int _abandoned;

    /// <param name="threadName">The name of each call's thread, as diagnostics show it.</param>
    /// <param name="apartment">The COM apartment of each call's thread.</param>
    /// <param name="deadline">How long a caller waits for a call.</param>
    public DeadlineWorker(string threadName, ApartmentState apartment, TimeSpan deadline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadName);
        _threadName = threadName;
        _apartment = apartment;
        _deadline = deadline;
    }

    /// <summary>Calls that missed the deadline and have not returned yet; while there is one, no call starts.</summary>
    public int AbandonedCalls => Volatile.Read(ref _abandoned);

    /// <summary>
    /// <paramref name="work"/>'s result, or <paramref name="fallback"/> when an earlier call is still stuck, the thread could
    /// not be started, the call threw, or it did not return within the deadline.
    /// </summary>
    public T Run<T>(Func<T> work, T fallback)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (Volatile.Read(ref _abandoned) > 0)
        {
            return fallback;
        }

        var result = fallback;
        var state = Running;
        var finished = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception)
            {
                // The result stays the fallback
            }
            finally
            {
                if (Interlocked.Exchange(ref state, Finished) == Abandoned)
                {
                    Interlocked.Decrement(ref _abandoned);
                }

                finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = _threadName
        };

        try
        {
            worker.SetApartmentState(_apartment);
            worker.UnsafeStart();
        }
        catch (Exception)
        {
            // The thread could not be started
            return fallback;
        }

        // The event is not disposed: an abandoned worker still sets it when it returns
        if (!finished.Wait(_deadline) && Interlocked.CompareExchange(ref state, Abandoned, Running) == Running)
        {
            Interlocked.Increment(ref _abandoned);
            return fallback;
        }

        return result;
    }
}
