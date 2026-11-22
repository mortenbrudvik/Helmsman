using System.Runtime.CompilerServices;
using Helmsman.Models;
using Helmsman.Native;
using Helmsman.Services.Interfaces;
using static Helmsman.Services.WindowChangeFailures;
using static Helmsman.Services.WindowReads;

namespace Helmsman.Services;

/// <summary>
/// set_window_zorder (roadmap M6.7, <see cref="IWindowZOrder"/>): changes a window's place in the z-order without moving,
/// resizing or activating it, reads it back, and records the windows it made topmost in <see cref="TopmostPins"/>.
/// </summary>
/// <remarks>
/// SetWindowPos on another thread's window waits until that thread answers, so every such call goes through
/// <see cref="IWindowCallRunner"/>: a window that does not answer is given up on at its deadline with
/// <see cref="WindowNotRespondingException"/>.
/// </remarks>
public sealed class WindowZOrderService : IWindowZOrder
{
    private readonly INativeWindowWrapper _nativeWrapper;
    private readonly IWindowCallRunner _windowCalls;
    private readonly TopmostPins _topmostPins;
    private readonly WindowReads _reads;
    private readonly WindowTargetCheck _targetCheck;

    /// <param name="nativeWrapper">The window calls.</param>
    /// <param name="targetPolicy">
    /// The server's target policy (roadmap M1.12). A z-order change refuses a window it denies, so this tool never raises it.
    /// </param>
    /// <param name="windowCalls">
    /// Runs the calls that can block on another thread's window with a deadline; the one the window placements use, which
    /// counts the calls still stuck.
    /// </param>
    /// <param name="topmostPins">
    /// Where set_window_zorder records the windows it made topmost (roadmap M6.7), which get_server_info lists. The server
    /// shares one.
    /// </param>
    public WindowZOrderService(
        INativeWindowWrapper nativeWrapper,
        ITargetPolicy targetPolicy,
        IWindowCallRunner windowCalls,
        TopmostPins topmostPins)
    {
        _nativeWrapper = nativeWrapper;
        _windowCalls = windowCalls ?? throw new ArgumentNullException(nameof(windowCalls));
        _topmostPins = topmostPins ?? throw new ArgumentNullException(nameof(topmostPins));
        _reads = new WindowReads(nativeWrapper);
        _targetCheck = new WindowTargetCheck(nativeWrapper, _reads, targetPolicy ?? throw new ArgumentNullException(nameof(targetPolicy)));
    }

    /// <summary>The flags of every z-order change: it never moves, resizes or activates the window.</summary>
    private const uint ZOrderFlags = NativeEnums.SWP_NOMOVE | NativeEnums.SWP_NOSIZE | NativeEnums.SWP_NOACTIVATE;

    public WindowChangeResult SetWindowZOrder(nint handle, ZOrderMode mode, nint? above = null)
    {
        if (_targetCheck.ChangeRefusal(handle) is { } refused)
            return refused;

        if (mode == ZOrderMode.Above && (above is not { } named || !_reads.IsValidWindow(named)))
        {
            return WindowChangeResult.Failed(handle, WindowErrorCode.InvalidHandle,
                $"aboveHandle {above ?? 0} is no window (it closed). Nothing was changed. Find it again with find_windows.");
        }

        var window = TopLevel(handle);
        var wasTopmost = _reads.IsTopmost(window);
        try
        {
            if (mode == ZOrderMode.Above)
                return PlaceAbove(handle, window, TopLevel(above!.Value));

            var insertAfter = mode switch
            {
                ZOrderMode.Top => NativeEnums.HWND_TOP,
                ZOrderMode.Bottom => NativeEnums.HWND_BOTTOM,
                ZOrderMode.Topmost => NativeEnums.HWND_TOPMOST,
                ZOrderMode.NoTopmost => NativeEnums.HWND_NOTOPMOST,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a z-order mode.")
            };
            var result = SetZOrder(handle, window, insertAfter);
            return result.Success && TopmostRefusal(handle, window, mode) is { } notApplied ? notApplied : result;
        }
        finally
        {
            // From what the window is now, whatever the outcome (owner decision D14): a call that failed or did not return in
            // time can still have made it topmost, and such a window must be listed, not forgotten
            TrackPin(window, wasTopmost);
        }
    }

    /// <summary>
    /// Records <paramref name="window"/> as pinned when it is topmost now and was not before the call, and forgets it when it is
    /// not topmost (or gone). A window that was topmost before keeps whatever it had: the server did not pin it.
    /// </summary>
    private void TrackPin(nint window, bool wasTopmost)
    {
        if (!_reads.IsTopmost(window))
            _topmostPins.Unpin(window);
        else if (!wasTopmost)
            _topmostPins.Pin(window, _nativeWrapper.GetWindowThreadId(window));
    }

    /// <summary>The top-level window of <paramref name="handle"/>, which has the place in the z-order; itself when it has none.</summary>
    private nint TopLevel(nint handle) =>
        _nativeWrapper.GetAncestor(handle, WindowAncestor.Root) is var root && root != nint.Zero ? root : handle;

    /// <summary>ZOrderNotApplied when the window's topmost state is not what <paramref name="mode"/> asked for; otherwise null.</summary>
    private WindowChangeResult? TopmostRefusal(nint handle, nint window, ZOrderMode mode)
    {
        var expected = mode switch
        {
            ZOrderMode.Topmost => true,
            ZOrderMode.NoTopmost or ZOrderMode.Bottom => false,
            _ => (bool?)null
        };
        if (expected is not { } topmost || _reads.IsTopmost(window) == topmost)
            return null;

        return WindowChangeResult.Failed(handle, WindowErrorCode.ZOrderNotApplied, topmost
            ? "Windows took the change, but the window is not topmost when read back: its app sets its own z-order (some apps and " +
              "games do). Check it with get_window_info."
            : "Windows took the change, but the window is still topmost when read back: its app keeps itself topmost. Check it " +
              "with get_window_info.");
    }

    /// <summary>
    /// Places <paramref name="window"/> directly above <paramref name="other"/>: behind the window in front of it, in its band.
    /// A window that is topmost is above every window that is not, so nothing is sent for it; one that is not becomes topmost
    /// first when the other is (it can only be above a topmost window as one), and is made not topmost again when the rest of
    /// the placement fails, so a failed call leaves no new topmost window. A normal window that would go behind a topmost one
    /// goes to the top of the normal windows instead (HWND_TOP), which is the same place, so it never becomes topmost by the way
    /// it is inserted. Read back: ZOrderNotApplied unless it is in front of the other afterwards, InvalidHandle when either
    /// window closed meanwhile.
    /// </summary>
    private WindowChangeResult PlaceAbove(nint handle, nint window, nint other)
    {
        if (other == window)
        {
            return WindowChangeResult.Failed(handle, WindowErrorCode.InvalidHandle,
                "aboveHandle is the window itself. Nothing was changed. Pass another window to place it above.");
        }

        var windowTopmost = _reads.IsTopmost(window);
        var otherTopmost = _reads.IsTopmost(other);
        var zOrder = EnumerateTopLevelWindows(_nativeWrapper);
        var otherIndex = zOrder.IndexOf(other);
        var inFront = zOrder.Take(Math.Max(otherIndex, 0)).Where(w => w != window).LastOrDefault();
        var already = windowTopmost && !otherTopmost || otherIndex > 0 && zOrder[otherIndex - 1] == window;
        if (!already && otherTopmost && !windowTopmost)
        {
            // HWND_TOPMOST takes it to the front of the topmost windows; then behind the one in front of the other, if any
            var sent = new StrongBox<bool>();
            WindowChangeResult madeTopmost;
            try
            {
                madeTopmost = SetZOrder(handle, window, NativeEnums.HWND_TOPMOST, sent);
            }
            catch (WindowNotRespondingException ex) when (Volatile.Read(ref sent.Value))
            {
                // Sent and given up on: the call can still make the window topmost once its app answers, after any undo. One
                // that already did is undone; one refused before it was sent changed nothing, and its message says so
                if (!_reads.IsTopmost(window))
                    throw new WindowNotRespondingException(ex.Window, ex.Message + MayBecomeTopmost);
                if (!UndoTopmost(handle, window))
                    throw new WindowNotRespondingException(ex.Window, LeftTopmost(ex.Message));
                throw;
            }

            if (!madeTopmost.Success)
                return madeTopmost;

            WindowChangeResult placed;
            try
            {
                placed = inFront != nint.Zero ? SetZOrder(handle, window, inFront) : WindowChangeResult.Done(handle);
                if (placed.Success)
                    placed = ReadBackAbove(handle, window, other);
            }
            catch (WindowNotRespondingException ex)
            {
                if (!UndoTopmost(handle, window))
                    throw new WindowNotRespondingException(ex.Window, LeftTopmost(ex.Message));
                throw;
            }

            if (!placed.Success && !UndoTopmost(handle, window))
                placed = placed with { Error = LeftTopmost(placed.Error) };
            return placed;
        }

        if (!already)
        {
            var insertAfter = inFront == nint.Zero || _reads.IsTopmost(inFront) && !otherTopmost
                ? otherTopmost ? NativeEnums.HWND_TOPMOST : NativeEnums.HWND_TOP
                : inFront;
            if (SetZOrder(handle, window, insertAfter) is { Success: false } notPlaced)
                return notPlaced;
        }

        return ReadBackAbove(handle, window, other);
    }

    /// <summary>Done when <paramref name="window"/> is in front of <paramref name="other"/> now; InvalidHandle when either closed.</summary>
    private WindowChangeResult ReadBackAbove(nint handle, nint window, nint other)
    {
        var after = EnumerateTopLevelWindows(_nativeWrapper);
        var windowAfter = after.IndexOf(window);
        var otherAfter = after.IndexOf(other);
        if (windowAfter < 0)
            return WindowGone(handle);

        if (otherAfter < 0)
        {
            return WindowChangeResult.Failed(handle, WindowErrorCode.InvalidHandle,
                "aboveHandle closed during the call, so the window is above nothing. Check its place with get_window_info.");
        }

        return windowAfter < otherAfter
            ? WindowChangeResult.Done(handle)
            : WindowChangeResult.Failed(handle, WindowErrorCode.ZOrderNotApplied,
                "Windows took the change, but the window is not above the other window when read back: an app changed the z-order " +
                "again. Check it with get_window_info.");
    }

    /// <summary>
    /// Makes a window the failed 'above' made topmost not topmost again, and says whether it is not topmost afterwards (read
    /// back, whatever the call returned). A refusal or a window that does not answer is left as it is: the pin then follows
    /// what the window is (TrackPin), so get_server_info still lists it, and the caller puts <see cref="LeftTopmost"/> before
    /// its failure, since the contract that a failed call leaves no new topmost window does not hold for it.
    /// </summary>
    private bool UndoTopmost(nint handle, nint window)
    {
        try
        {
            SetZOrder(handle, window, NativeEnums.HWND_NOTOPMOST);
        }
        catch (WindowNotRespondingException)
        {
            // Read back below: a change Windows made before the deadline still counts
        }

        return !_reads.IsTopmost(window);
    }

    /// <summary>
    /// A failed 'above' whose undo failed too, in words: the state first (the window is topmost now) and how to undo it, then
    /// <paramref name="failure"/>, the step that failed, whose own "nothing was changed" is about that step alone.
    /// </summary>
    private static string LeftTopmost(string? failure) =>
        "The window was left topmost: it was made topmost to go above a topmost window, and making it not topmost again failed " +
        "too. Call set_window_zorder with mode 'notopmost' for it (get_server_info lists it among the windows the server made " +
        $"topmost), or ask the user. Placing it above the other window failed: {failure}";

    /// <summary>
    /// What a failed 'above' adds when the call that makes the window topmost first was sent and given up on: it may still
    /// happen once the app answers, and then the window stays topmost.
    /// </summary>
    private const string MayBecomeTopmost =
        " Going above a topmost window makes the window topmost first, and that change may still happen once the app answers, so " +
        "the window may be left topmost: check it with get_window_info (isTopmost), and if it is, call set_window_zorder with mode " +
        "'notopmost' for it, or ask the user.";

    /// <summary>SetWindowZOrder, with a deadline (<see cref="IWindowCallRunner"/>); a refusal as a placement's (AccessDenied).</summary>
    /// <param name="sent">
    /// Set when the call is made, before it can block: a <see cref="WindowNotRespondingException"/> with it set was given up on
    /// and may still happen; without it, the call was refused before it was sent (Windows reports the app hung, or an earlier
    /// call to it is still stuck) and changed nothing.
    /// </param>
    /// <exception cref="WindowNotRespondingException">The window did not answer in time.</exception>
    private WindowChangeResult SetZOrder(nint handle, nint window, nint insertAfter, StrongBox<bool>? sent = null)
    {
        var placed = _windowCalls.Run(handle, () =>
        {
            if (sent != null)
                Volatile.Write(ref sent.Value, true);
            return _nativeWrapper.SetWindowZOrder(window, insertAfter, ZOrderFlags);
        });
        return placed.Succeeded ? WindowChangeResult.Done(handle) : NotPlaced(handle, placed.Win32Error, WindowErrorCode.ZOrderNotApplied);
    }
}
