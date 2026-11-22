using Helmsman.Models;
using Helmsman.Services.Interfaces;

namespace Helmsman.Services;

/// <summary>
/// Small helpers that the launch, kill and debug-browser services share. A process name is read with
/// <see cref="ProcessNames"/>, the one rule for process names.
/// </summary>
internal static class ProcessHelpers
{
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

    /// <summary>The ShellExecute verb that starts a program as administrator.</summary>
    public const string RunAsVerb = "runas";

    /// <summary>Whether the server itself runs elevated; false when that cannot be read.</summary>
    public static bool ServerIsElevated(IProcessSecurityProbe securityProbe) =>
        securityProbe.ServerIntegrity is { } server && (server.IsElevated == true || server.IntegrityRid >= ProcessIntegrity.HighRid);

    /// <summary>Two warnings joined with "; ", either of which may be empty.</summary>
    public static string? CombineWarnings(string? warning1, string? warning2)
    {
        if (string.IsNullOrEmpty(warning1)) return warning2;
        if (string.IsNullOrEmpty(warning2)) return warning1;
        return $"{warning1}; {warning2}";
    }
}
