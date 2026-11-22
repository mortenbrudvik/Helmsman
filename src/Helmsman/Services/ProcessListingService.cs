using System.Diagnostics;
using Helmsman.Infrastructure;
using Helmsman.Models;
using Helmsman.Native;
using Helmsman.Services.Interfaces;

namespace Helmsman.Services;

/// <summary>
/// list_processes' process list (roadmap M6.6) and the reads of one process's name and path: <see cref="IProcessListing"/>.
/// </summary>
public sealed class ProcessListingService : IProcessListing
{
    private readonly IWindowService _windowService;
    private readonly INativeProcessWrapper _processWrapper;
    private readonly ITargetPolicy _targetPolicy;
    private readonly IProcessSecurityProbe _securityProbe;

    /// <param name="windowService">Finds each process's windows.</param>
    /// <param name="processWrapper">The process list and the reads of a process.</param>
    /// <param name="targetPolicy">The server's target policy, which keeps list_processes from reading a denied process's
    /// command line (roadmap M6.6).</param>
    /// <param name="securityProbe">Reads the listed processes' integrity and the server's own (roadmap M6.6).</param>
    public ProcessListingService(
        IWindowService windowService,
        INativeProcessWrapper processWrapper,
        ITargetPolicy targetPolicy,
        IProcessSecurityProbe securityProbe)
    {
        _windowService = windowService;
        _processWrapper = processWrapper ?? throw new ArgumentNullException(nameof(processWrapper));
        _targetPolicy = targetPolicy ?? throw new ArgumentNullException(nameof(targetPolicy));
        _securityProbe = securityProbe ?? throw new ArgumentNullException(nameof(securityProbe));
    }

    /// <summary>
    /// Each process's visible, titled top-level windows, in the order FindWindows returns them. Unlike the user-facing window
    /// listings, this also takes cloaked windows (on another virtual desktop), tool windows and the desktop shell's
    /// windows: a process that has such a window is running with a window, and hiding it from list_processes would let
    /// "has it exited?" checks pass too early. A UWP window is listed for the app's process and for its
    /// ApplicationFrameHost frame host.
    /// </summary>
    private Dictionary<int, List<WindowInfo>> MapWindowsToProcesses()
    {
        var windowsByProcess = new Dictionary<int, List<WindowInfo>>();
        void Add(int processId, WindowInfo window)
        {
            if (!windowsByProcess.TryGetValue(processId, out var windows))
            {
                windowsByProcess[processId] = windows = [];
            }

            windows.Add(window);
        }

        foreach (var window in _windowService.FindWindows(new WindowQuery { IncludeShell = true }))
        {
            if (string.IsNullOrEmpty(window.Title))
                continue;

            Add(window.ProcessId, window);
            if (window.HostProcessId is { } hostProcessId && hostProcessId != window.ProcessId)
                Add(hostProcessId, window);
        }

        return windowsByProcess;
    }

    public IReadOnlyList<ProcessInfo> GetRunningProcesses(ProcessQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1)
            throw new ArgumentOutOfRangeException(nameof(query), query.Limit, "The limit must be at least 1.");

        // An empty list would read as "no such process is running" and let a "has it exited?" check pass when nothing
        // was read, so an unreadable list is an error, as it was before the process seam
        var processes = _processWrapper.ListRunningProcesses()
            ?? throw new InvalidOperationException("The process list could not be read.");

        // Built in both modes, so that windowCount is real with includeWindowless as well (D-PAL-20)
        var windowsByProcess = MapWindowsToProcesses();

        var listed = new List<(RunningProcess Process, List<WindowInfo>? Windows)>();
        foreach (var process in processes)
        {
            if (query.ProcessId is { } processId && process.ProcessId != processId)
                continue;
            if (!string.IsNullOrEmpty(query.NameFilter) && !process.Name.Contains(query.NameFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var windows = windowsByProcess.GetValueOrDefault(process.ProcessId);
            if (!query.IncludeWindowless && windows == null)
                continue;

            listed.Add((process, windows));
        }

        if (listed.Count == 0)
            return [];

        // Each process is opened once for its path and start time: here only when the path filter or the start-time
        // order needs them, else once it is returned (or is the parent of one that is)
        var details = new Dictionary<int, ProcessDetails?>();
        ProcessDetails? DetailsOf(int processId)
        {
            if (!details.TryGetValue(processId, out var read))
            {
                details[processId] = read = _processWrapper.ReadProcessDetails(processId);
            }

            return read;
        }

        IEnumerable<(RunningProcess Process, List<WindowInfo>? Windows)> rows = listed;
        if (!string.IsNullOrEmpty(query.PathContains))
        {
            rows = rows.Where(r => DetailsOf(r.Process.ProcessId)?.ImagePath?.Contains(query.PathContains, StringComparison.OrdinalIgnoreCase) == true);
        }

        DateTime? StartOf((RunningProcess Process, List<WindowInfo>? Windows) row) => DetailsOf(row.Process.ProcessId)?.StartTime;
        var ordered = query.Sort switch
        {
            // Largest or newest first; what cannot be read last
            ProcessSort.Memory => rows.OrderBy(r => r.Process.WorkingSetBytes == null).ThenByDescending(r => r.Process.WorkingSetBytes),
            ProcessSort.StartTime => rows.OrderBy(r => StartOf(r) == null).ThenByDescending(StartOf),
            _ => rows.OrderBy(r => r.Process.Name, StringComparer.OrdinalIgnoreCase)
        };
        rows = ordered.ThenBy(r => r.Process.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Process.ProcessId);
        if (query.Limit is { } limit)
        {
            rows = rows.Take(limit);
        }

        var returned = rows.ToList();

        // The parents come from a second snapshot (Toolhelp), so a process can be in one list and not the other: a
        // parent that is not in it is not running, and is not reported
        var parentByProcessId = _processWrapper.GetProcessList()?.ToDictionary(p => p.Key, p => p.Value.ParentProcessId);
        var serverIntegrity = _securityProbe.ServerIntegrity;

        var result = new List<ProcessInfo>(returned.Count);
        foreach (var (process, windows) in returned)
        {
            var processDetails = DetailsOf(process.ProcessId);
            var parent = parentByProcessId == null
                ? null
                : NativeProcessWrapper.FindParent(process.ProcessId, parentByProcessId, id => DetailsOf(id)?.StartTime);
            var integrity = _securityProbe.GetIntegrity(process.ProcessId);
            var commandLine = query.IncludeCommandLine ? ReadCommandLine(process, processDetails) : null;

            result.Add(new ProcessInfo(
                ProcessId: process.ProcessId,
                Name: process.Name,
                ExecutablePath: processDetails?.ImagePath,
                WindowCount: windows?.Count ?? 0,
                MemoryUsageMB: process.WorkingSetBytes / (1024.0 * 1024.0),
                ParentProcessId: parent,
                StartTime: processDetails?.StartTime,
                Windows: windows?.Take(ProcessInfo.MaxWindows).ToList() ?? [],
                Integrity: integrity,
                AboveServer: integrity != null && serverIntegrity != null && integrity.IsMorePrivilegedThan(serverIntegrity),
                CommandLine: commandLine?.Text,
                CommandLineRedacted: commandLine?.Redacted == true));
        }

        return result;
    }

    /// <summary>
    /// A listed process's command line, redacted and cut to <see cref="ProcessInfo.MaxCommandLineLength"/>; null when the
    /// user's target policy denies the process (it is then never read) and when it cannot be read. The policy decides by the
    /// listed name, so the command line is read only when the process <paramref name="details"/> describes still bears it
    /// (its image file is that name) and is still the one at the PID once the command line is read (same start time): a
    /// PID reused at any point after the listing gives no command line, rather than one of a process the policy check did
    /// not cover. Without a start time or a path the PID cannot be pinned, so none is read either.
    /// </summary>
    private RedactedCommandLine? ReadCommandLine(RunningProcess process, ProcessDetails? details)
    {
        if (details is not { StartTime: { } startTime, ImagePath: { } imagePath })
            return null;

        var imageName = Path.GetFileNameWithoutExtension(imagePath);
        if (!string.Equals(imageName, process.Name, StringComparison.OrdinalIgnoreCase) ||
            _targetPolicy.Evaluate([process.Name, imageName]) is { Allowed: false })
            return null;

        var commandLine = _processWrapper.GetProcessCommandLine(process.ProcessId);
        if (commandLine == null || _processWrapper.ReadProcessDetails(process.ProcessId)?.StartTime != startTime)
            return null;

        var redacted = CommandLineRedactor.Redact(commandLine);
        return redacted with { Text = AppText.Cut(redacted.Text, ProcessInfo.MaxCommandLineLength) };
    }

    public string? GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public string? GetProcessPath(int processId) => _processWrapper.GetProcessImagePath(processId);
}
