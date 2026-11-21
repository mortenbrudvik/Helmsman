using WinDesktopMcp.Models;

namespace WinDesktopMcp.Services.Interfaces;

/// <summary>The running processes (list_processes, roadmap M6.6) and the reads of one process's name and path.</summary>
public interface IProcessListing
{
    /// <summary>
    /// Lists running processes (roadmap M6.6). The list comes from <see cref="INativeProcessWrapper.ListRunningProcesses"/>,
    /// filtered by name, process ID and (by default) a window, then by path, sorted (<see cref="ProcessQuery.Sort"/>: name,
    /// then process ID, by default) and cut to <see cref="ProcessQuery.Limit"/>. A process is opened only for what is asked:
    /// for its path and start time (<see cref="INativeProcessWrapper.ReadProcessDetails"/>) when the path filter or the
    /// start-time order needs them, else only once it is returned, and for its integrity and command line only once it is
    /// returned. Its parent is taken from one process snapshot with the PID-reuse rule of
    /// <see cref="Native.NativeProcessWrapper.FindParent"/> (named only when both start times are known). Never throws for a
    /// process that exits or cannot be opened: its unreadable fields are null.
    /// </summary>
    /// <remarks>
    /// Windows: only processes that have a visible, titled top-level window are listed unless
    /// <see cref="ProcessQuery.IncludeWindowless"/> is set, wherever the window is: windows on other virtual desktops, tool
    /// windows and the desktop shell's windows count, so a process is never hidden only because its window is not on the
    /// current desktop. WindowCount counts those windows in both modes (0 for a process without one), and Windows holds the
    /// first <see cref="ProcessInfo.MaxWindows"/> of them, front to back; a UWP app's window counts for the app's process and
    /// for its ApplicationFrameHost.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The process list cannot be read: an empty list would look like "no such process is running".
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ProcessQuery.Limit"/> is below 1.</exception>
    IReadOnlyList<ProcessInfo> GetRunningProcesses(ProcessQuery query);

    string? GetProcessName(int processId);

    string? GetProcessPath(int processId);
}
