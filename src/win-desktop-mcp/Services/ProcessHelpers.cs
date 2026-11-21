using WinDesktopMcp.Services.Interfaces;

namespace WinDesktopMcp.Services;

/// <summary>Small helpers that the launch, kill and debug-browser services share.</summary>
internal static class ProcessHelpers
{
    /// <summary>A process name without its .exe extension (any case); any other name as it is.</summary>
    public static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>Whether the process has exited; false when that cannot be read.</summary>
    public static bool HasExitedSafe(IProcessHandle process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
