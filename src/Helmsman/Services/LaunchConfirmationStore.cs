using Helmsman.Models;
using Helmsman.Services.Interfaces;
using static Helmsman.Services.LayoutPresetStore;

namespace Helmsman.Services;

/// <summary>
/// The launches of agent-written presets that the user confirmed (owner decision D15; <see cref="ILaunchConfirmationStore"/>),
/// kept in a file in the layouts folder that is only ever appended to.
/// </summary>
public class LaunchConfirmationStore : ILaunchConfirmationStore
{
    /// <summary>
    /// The file in the layouts folder that keeps the confirmed launches (<see cref="LayoutPresetSchema.LaunchFingerprint"/>),
    /// one per line, so a confirmation holds across restarts. Not a .json file, so it is never read as a preset.
    /// </summary>
    public const string LaunchConfirmationsFileName = ".launch-confirmations";

    private readonly object _confirmationsLock = new();

    // The confirmations as last read from the file, and what the file was then: it is read again when it changes or goes away
    // (review M6.8 #6), so deleting it revokes every confirmation at once
    private HashSet<string> _confirmedLaunches = new(StringComparer.Ordinal);
    private (bool Exists, DateTime LastWriteUtc, long Length)? _confirmationsStamp;

    // Confirmations the file could not keep (it could not be written): they hold until the server stops
    private readonly HashSet<string> _unsavedConfirmations = new(StringComparer.Ordinal);

    /// <param name="layoutsPath">The layouts folder (<see cref="ILayoutPresetStore.LayoutsPath"/>), which keeps the file.</param>
    public LaunchConfirmationStore(string layoutsPath)
    {
        LayoutsPath = layoutsPath;
    }

    /// <summary>The layouts folder, which keeps the file.</summary>
    private string LayoutsPath { get; }

    public bool AreLaunchesConfirmed(LayoutPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!LayoutPresetSchema.IsAgentAuthored(preset))
            return true;

        // A preset that starts more than the question shows is never confirmed (review M6.8 #1)
        if (LayoutPresetSchema.ConfirmationRefusal(preset) != null)
            return false;

        var key = LayoutPresetSchema.LaunchFingerprint(preset);
        lock (_confirmationsLock)
        {
            return Confirmations().Contains(key) || _unsavedConfirmations.Contains(key);
        }
    }

    public void ConfirmLaunches(LayoutPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var key = LayoutPresetSchema.LaunchFingerprint(preset);
        lock (_confirmationsLock)
        {
            if (Confirmations().Contains(key))
                return;

            try
            {
                // Appended in one write, on a line of its own whatever the file ends with; another server may append too
                using (var stream = new FileStream(ConfirmationsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    var line = System.Text.Encoding.UTF8.GetBytes("\n" + key + "\n");
                    stream.Write(line, 0, line.Length);
                }

                _confirmationsStamp = null;
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                _unsavedConfirmations.Add(key);
            }
        }
    }

    private string ConfirmationsPath => Path.Combine(LayoutsPath, LaunchConfirmationsFileName);

    /// <summary>
    /// The confirmed launches in the file, read again whenever it was written, replaced or deleted since the last read
    /// (callers hold <see cref="_confirmationsLock"/>). Only whole lines of 64 lowercase hex digits count: garbage, a cut-off
    /// line or a blank line confirms nothing.
    /// </summary>
    private HashSet<string> Confirmations()
    {
        (bool Exists, DateTime LastWriteUtc, long Length) stamp;
        try
        {
            var file = new FileInfo(ConfirmationsPath);
            stamp = file.Exists ? (true, file.LastWriteTimeUtc, file.Length) : (false, default, 0);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            stamp = (false, default, 0);
        }

        if (_confirmationsStamp == stamp)
            return _confirmedLaunches;

        var keys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (stamp.Exists)
            {
                foreach (var line in File.ReadLines(ConfirmationsPath))
                {
                    var key = line.Trim();
                    if (key.Length == 64 && key.All(char.IsAsciiHexDigitLower))
                        keys.Add(key);
                }
            }
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            // An unreadable file confirms nothing
        }

        _confirmedLaunches = keys;
        _confirmationsStamp = stamp;
        return keys;
    }
}
