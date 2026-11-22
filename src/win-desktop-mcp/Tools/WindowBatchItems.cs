using WinDesktopMcp.Infrastructure;
using WinDesktopMcp.Models;
using WinDesktopMcp.Services.Interfaces;

namespace WinDesktopMcp.Tools;

/// <summary>
/// The items of the window batches (set_windows_bounds_batch, snap_windows_batch, set_windows_state_batch and
/// close_windows_batch): each item's window resolved and checked again, and the batch's result in request order.
/// </summary>
internal static class WindowBatchItems
{
    /// <summary>
    /// The window of each item of a batch, resolved before the gate (roadmap M2.4): its handle, or its selector resolved.
    /// An item that is null, that names no window, or whose selector names no single window gets its refusal (InvalidArguments,
    /// WindowNotFound, AmbiguousWindow with candidates, HandleMismatch, InvalidHandle) and fails alone.
    /// </summary>
    internal static List<(nint Handle, WindowResultItem? Refusal)> ResolveItems<T>(
        IWindowService windows, T[] items, string array, string fields, Func<T, long?> handleOf, Func<T, WindowSelector?> windowOf,
        bool requireNamedWindow = false) where T : class
    {
        var resolved = new List<(nint, WindowResultItem?)>(items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            if (item == null)
            {
                resolved.Add((nint.Zero, NullItem(i, array, fields)));
                continue;
            }

            var target = WindowTargeting.Resolve(windows, handleOf(item), windowOf(item), requireNamedWindow: requireNamedWindow);
            if (target == null)
            {
                resolved.Add((nint.Zero, new WindowResultItem(0, false,
                    Error: $"Item {i} of {array} names no window; each item needs {fields}. Nothing was done for it.",
                    ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode)));
            }
            else if (target.Refusal is { } refusal)
            {
                resolved.Add((nint.Zero, new WindowResultItem(0, false, Error: $"Item {i}: {refusal.Error}", ErrorCode: refusal.ErrorCode,
                    Candidates: refusal.Candidates)));
            }
            else
            {
                resolved.Add((target.Handle, null));
            }
        }

        return resolved;
    }

    /// <summary>
    /// An item's resolved window checked again inside the input gate (<see cref="WindowTargeting.Recheck"/>): HandleMismatch
    /// when it no longer matches the item's selector, so that item fails alone; null otherwise.
    /// </summary>
    internal static WindowResultItem? ItemRecheck(IWindowService windows, int index, nint handle, WindowSelector? window) =>
        WindowTargeting.Recheck(windows, window, handle) is { } refusal
            ? new WindowResultItem(handle.ToInt64(), false, Error: $"Item {index}: {refusal.Error}", ErrorCode: refusal.ErrorCode)
            : null;

    /// <summary>
    /// The batch's array argument itself: an explicit null fails the call with InvalidArguments, as a missing one does.
    /// </summary>
    internal static void RequireItems<T>(T[]? items, string name)
    {
        if (items == null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"{name} must be an array of items, not null. Nothing was done.");
        }
    }

    /// <summary>The result of a batch item that is null: it fails alone with InvalidArguments (roadmap M2.1).</summary>
    private static WindowResultItem NullItem(int index, string array, string fields) =>
        new(0, false, Error: $"Item {index} of {array} is null; each item needs {fields}. Nothing was done for it.",
            ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);

    /// <summary>
    /// The batch's items in request order: the refusal where there is one, and the service's results, in order,
    /// for the other requests.
    /// </summary>
    internal static List<WindowResultItem> InOrder(IReadOnlyList<WindowResultItem?> refusals, IEnumerable<WindowResultItem> results)
    {
        using var done = results.GetEnumerator();
        var items = new List<WindowResultItem>(refusals.Count);
        foreach (var refusal in refusals)
        {
            if (refusal != null)
            {
                items.Add(refusal);
            }
            else if (done.MoveNext())
            {
                items.Add(done.Current);
            }
        }

        return items;
    }

    internal static BatchWindowResult Summary(int requested, IReadOnlyList<WindowResultItem> items) => new(
        TotalRequested: requested,
        Succeeded: items.Count(i => i.Success),
        Failed: items.Count(i => !i.Success),
        Results: items);
}
