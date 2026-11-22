namespace Helmsman.Models;

/// <summary>
/// The one way the server reads a process name, for the target policy and for every argument that names a process (a
/// window selector's processName, find_windows' and get_window_events' processName, close_application's processName,
/// kill_process' expectedProcessName, list_processes' nameFilter, launch_application's expectWindow.processName, a layout
/// placement's processName and save_layout's process filters): case does not matter, nor do surrounding spaces and quotes
/// or one trailing ".exe"; any other extension is part of the name (notepad.com is not notepad), and so is every dot before
/// it (Microsoft.Photos, python3.12).
/// </summary>
/// <remarks>
/// <para>The policy's own entries (HELMSMAN_DENY_PROCESSES, HELMSMAN_ALLOW_PROCESSES) may be paths: <see cref="Normalize"/>
/// drops the folder, so an entry denies or allows every process of that name, wherever it runs from.</para>
/// <para>An argument may not be a path (<see cref="IsPath"/>): a process is matched by its name alone, so the folder of
/// C:\Evil\notepad.exe would be ignored and the real Notepad matched. Such an argument is refused with
/// <see cref="PathRefusal"/>; a caller that matches with it anyway (<see cref="FromArgument"/>, <see cref="Matches"/>,
/// <see cref="Contains"/>) matches nothing, never the file name alone.</para>
/// </remarks>
public static class ProcessNames
{
    private const string Exe = ".exe";

    /// <summary>
    /// The image name <paramref name="name"/> stands for: no surrounding spaces or quotes, no folder, no ".exe", no spaces
    /// before the ".exe" either. Compare the results ignoring case. Used for the policy's entries and for the names Windows
    /// reports; an argument goes through <see cref="FromArgument"/>, which refuses a folder instead of dropping it.
    /// </summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var trimmed = name.Trim().Trim('"').Trim();
        var separator = trimmed.LastIndexOfAny(['\\', '/']);
        if (separator >= 0)
        {
            trimmed = trimmed[(separator + 1)..];
        }

        return trimmed.EndsWith(Exe, StringComparison.OrdinalIgnoreCase) ? trimmed[..^Exe.Length].Trim() : trimmed;
    }

    /// <summary>
    /// Whether an argument names a file in a folder or on a drive rather than a process: it holds '\', '/' or ':', none of
    /// which a process name can hold.
    /// </summary>
    public static bool IsPath(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.AsSpan().IndexOfAny('\\', '/', ':') >= 0;
    }

    /// <summary>
    /// The process name an argument stands for (<see cref="Normalize"/>), or null when it is a path (<see cref="IsPath"/>),
    /// which names no process here. "" when it is blank once normalized (only spaces, quotes or ".exe").
    /// </summary>
    public static string? FromArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return IsPath(value) ? null : Normalize(value);
    }

    /// <summary>
    /// Whether the argument <paramref name="wanted"/> names the process Windows calls <paramref name="actual"/>: the whole
    /// name, ignoring case. False when either is null, when the argument is a path, and when it is blank.
    /// </summary>
    public static bool Matches(string? actual, string? wanted) =>
        actual != null && wanted != null && FromArgument(wanted) is { Length: > 0 } name &&
        string.Equals(Normalize(actual), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the argument <paramref name="part"/> is part of the name of the process Windows calls
    /// <paramref name="actual"/>, ignoring case. False when either is null, when the argument is a path, and when it is
    /// blank.
    /// </summary>
    public static bool Contains(string? actual, string? part) =>
        actual != null && part != null && FromArgument(part) is { Length: > 0 } name &&
        Normalize(actual).Contains(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why the argument <paramref name="parameter"/> cannot be <paramref name="value"/>, a path, with the process name to
    /// pass instead; null when it is no path. The caller adds what was not done.
    /// </summary>
    public static string? PathRefusal(string parameter, string? value)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (value == null || !IsPath(value))
            return null;

        var name = Normalize(FileNameOf(value));
        var pass = name.Length > 0
            ? $"pass '{name}' (as find_windows and list_processes report it) instead of '{value}'"
            : $"pass the process's name as find_windows and list_processes report it, not '{value}'";
        return $"{parameter} takes a process name, not a path: {pass}. A folder is not compared with the process's own, so a " +
               "path is refused rather than matching every process of that name.";
    }

    /// <summary>The text after the last folder or drive separator, as the file name a path ends in.</summary>
    private static string FileNameOf(string path)
    {
        var trimmed = path.Trim().Trim('"').Trim();
        return trimmed[(trimmed.LastIndexOfAny(['\\', '/', ':']) + 1)..];
    }
}
