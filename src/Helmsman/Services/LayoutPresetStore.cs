using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Helmsman.Models;
using Helmsman.Models.Results;
using Helmsman.Services.Interfaces;

namespace Helmsman.Services;

/// <summary>
/// Stores the layout presets as JSON files (<see cref="ILayoutPresetStore"/>): reads, saves and deletes them, and prepares the
/// folder on first use. Every other layout class reads and writes the presets only through it.
/// </summary>
/// <remarks>
/// The folder is read on every lookup (only changed files are re-parsed), so presets added, edited
/// or removed on disk are seen immediately. All file and cache access is serialized by one gate,
/// which makes the singleton safe for parallel tool calls.
/// </remarks>
public class LayoutPresetStore : ILayoutPresetStore
{
    /// <summary>
    /// Maximum length of a preset name.
    /// </summary>
    public const int MaxNameLength = 128;

    /// <summary>
    /// Written once the folder has been migrated and seeded, so that presets the user deletes
    /// are not migrated or seeded again on the next start.
    /// </summary>
    private const string InitializedMarkerFileName = ".initialized";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // A placement's state is written as "normal", "maximized" or "minimized"; any case is read back. A number is refused
        // (review finding F5): "state": 7 would bind a WindowState that does not exist, and no server ever wrote one
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly LayoutStorageOptions _options;
    private readonly ILayoutFolderAccess _folderAccess;
    private readonly ILogger<LayoutService>? _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Parsed preset files keyed by full path; Preset is null for files that are not valid presets, which keep why (Error)
    private readonly Dictionary<string, CachedPresetFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private bool _storageReady;

    // Whether the last listing of the folder failed, so its warning is logged once per run of failures
    private bool _listingFailed;

    /// <summary>
    /// A file as it was last read: its version (write time and length), and the preset it holds, or why it holds none (with
    /// the name it gives, when its JSON can be read that far). A file is read again only when its version changes, so an
    /// invalid one is logged once per version.
    /// </summary>
    private sealed record CachedPresetFile(DateTime LastWriteTimeUtc, long Length, LayoutPreset? Preset, string? Error = null,
        string? DeclaredName = null);

    /// <summary>
    /// Creates a store for the given storage options. The constructor does not touch the file system:
    /// the folder is prepared on first use, so an unusable location is reported as
    /// <see cref="LayoutErrorCode.StorageUnavailable"/> instead of failing service construction.
    /// </summary>
    /// <param name="folderAccess">Checks that the folder can be listed and written; defaults to the real file system.</param>
    /// <param name="logger">Logs a file that is not a valid preset (once per version of it), and a folder that cannot be listed.</param>
    public LayoutPresetStore(
        LayoutStorageOptions options,
        ILayoutFolderAccess? folderAccess = null,
        ILogger<LayoutService>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _folderAccess = folderAccess ?? new LayoutFolderAccess();
        _logger = logger;
    }

    public string LayoutsPath => _options.LayoutsPath;

    public string? GetStorageError()
    {
        _gate.Wait();
        try
        {
            return EnsureStorage();
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<LayoutPreset> GetAllPresets()
    {
        _gate.Wait();
        try
        {
            if (EnsureStorage() != null)
                return [];

            RefreshCache();
            return BuildNameIndex().Values
                .Select(entry => entry.Preset)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public LayoutPreset? GetPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        _gate.Wait();
        try
        {
            return FindPreset(name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<InvalidLayoutFile> GetInvalidFiles()
    {
        _gate.Wait();
        try
        {
            if (EnsureStorage() != null)
                return [];

            RefreshCache();
            return InvalidFiles();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// What a "not found" message adds about the files that are not valid presets (review finding F1): the ones that may hold
    /// the preset <paramref name="name"/> (the name they give, or their file name as save_layout names files, matches), with
    /// why, and how many others there are. Empty when there are none, so the message stays as it was; it starts with a space
    /// otherwise.
    /// </summary>
    /// <param name="invalidFiles">The folder's invalid files (<see cref="ILayoutService.GetInvalidFiles"/>); null for none known.</param>
    public static string NotFoundNote(string? name, IReadOnlyList<InvalidLayoutFile>? invalidFiles)
    {
        if (invalidFiles is not { Count: > 0 })
            return string.Empty;

        var wanted = name?.Trim() ?? string.Empty;
        var matching = invalidFiles.Where(f => wanted.Length > 0 && StandsFor(f, wanted)).Take(3).ToList();
        var note = string.Concat(matching.Select(f =>
            $" The file '{f.FileName}' in the layouts folder may hold it, but it is not a valid preset: {f.Error} Fix or delete the file."));

        var others = invalidFiles.Count - matching.Count;
        if (others > 0)
        {
            note += others == 1
                ? $" {(matching.Count > 0 ? "1 other file" : "1 file")} in the layouts folder is not a valid preset; list_layouts lists it."
                : $" {others} {(matching.Count > 0 ? "other files" : "files")} in the layouts folder are not valid presets; list_layouts lists them.";
        }

        return note;

        static bool StandsFor(InvalidLayoutFile file, string name)
        {
            var stem = Path.GetFileNameWithoutExtension(file.FileName);
            return string.Equals(file.DeclaredName?.Trim(), name, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(stem, name, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(stem, SanitizeFileName(name), StringComparison.OrdinalIgnoreCase);
        }
    }

    public async Task<LayoutSaveResult> SavePresetAsync(LayoutPreset preset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preset);

        var nameError = ValidateName(preset.Name);
        if (nameError != null)
            return new LayoutSaveResult(false, preset, ErrorMessage: nameError, ErrorCode: nameof(LayoutErrorCode.InvalidName));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var storageError = EnsureStorage();
            if (storageError != null)
                return new LayoutSaveResult(false, preset, ErrorMessage: storageError, ErrorCode: nameof(LayoutErrorCode.StorageUnavailable));

            RefreshCache();
            var replaced = BuildNameIndex().TryGetValue(preset.Name, out var existing);

            // Overwrite the file an existing preset came from (which may be hand-named); otherwise pick
            // a file name that no other file uses, so names that sanitize alike never clobber each other.
            var filePath = replaced ? existing.Path : GetAvailableFilePath(preset.Name);
            var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";

            try
            {
                Directory.CreateDirectory(LayoutsPath);
                // Version 2, with every version 1 field filled for older servers (roadmap M6.8)
                await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(LayoutPresetSchema.ForWriting(preset), JsonOptions),
                    cancellationToken);
                File.Move(tempPath, filePath, overwrite: true);
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(tempPath);
                throw;
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                TryDeleteFile(tempPath);
                return new LayoutSaveResult(
                    false,
                    preset,
                    replaced,
                    $"Failed to save layout preset '{preset.Name}' to '{filePath}': {ex.Message}",
                    nameof(LayoutErrorCode.SaveFailed));
            }

            // Re-read from disk on the next lookup.
            _files.Remove(filePath);
            return new LayoutSaveResult(true, preset, replaced);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LayoutDeleteResult> DeletePresetAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new LayoutDeleteResult(false, name ?? string.Empty, "Layout preset name must not be empty",
                nameof(LayoutErrorCode.InvalidName));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var storageError = EnsureStorage();
            if (storageError != null)
                return new LayoutDeleteResult(false, name, storageError, nameof(LayoutErrorCode.StorageUnavailable));

            RefreshCache();

            // Delete every file that defines this preset, so it cannot reappear from a duplicate.
            var paths = _files
                .Where(entry => entry.Value.Preset is { } p && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Key)
                .ToList();

            if (paths.Count == 0)
            {
                // A file that is no valid preset is named, never deleted: it is the user's to fix or delete
                var note = NotFoundNote(name, InvalidFiles());
                return new LayoutDeleteResult(false, name, $"Layout preset '{name}' was not found" + (note.Length > 0 ? "." + note : string.Empty),
                    nameof(LayoutErrorCode.PresetNotFound));
            }

            foreach (var path in paths)
            {
                try
                {
                    File.Delete(path);
                    _files.Remove(path);
                }
                catch (Exception ex) when (IsFileSystemError(ex))
                {
                    return new LayoutDeleteResult(false, name, $"Failed to delete layout preset file '{path}': {ex.Message}",
                        nameof(LayoutErrorCode.DeleteFailed));
                }
            }

            return new LayoutDeleteResult(true, name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(string? StorageError, LayoutPreset? Preset, string NotFoundNote)> FindForApplyAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        string? storageError;
        LayoutPreset? preset = null;
        var notFoundNote = string.Empty;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            storageError = EnsureStorage();
            if (storageError == null && !string.IsNullOrWhiteSpace(name))
                preset = FindPreset(name);
            if (storageError == null && preset == null)
                notFoundNote = NotFoundNote(name, InvalidFiles());
        }
        finally
        {
            _gate.Release();
        }

        return (storageError, preset, notFoundNote);
    }

    #region Storage (callers must hold _gate)

    /// <summary>
    /// Creates the folder, checks that it can be listed and written, and the first time, migrates
    /// legacy presets and seeds the samples. Returns null when storage is usable, otherwise why it
    /// is not. Retries on every call until it succeeds.
    /// </summary>
    private string? EnsureStorage()
    {
        if (_storageReady)
            return null;

        if (!Path.IsPathFullyQualified(LayoutsPath))
        {
            return $"Layout storage folder '{LayoutsPath}' is not an absolute path. " +
                   $"Set the {LayoutStorageOptions.LayoutsPathEnvironmentVariable} environment variable to an absolute folder path.";
        }

        try
        {
            Directory.CreateDirectory(LayoutsPath);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return $"Layout storage folder '{LayoutsPath}' could not be created: {ex.Message} " +
                   $"Set the {LayoutStorageOptions.LayoutsPathEnvironmentVariable} environment variable to a writable folder.";
        }

        // CreateDirectory also succeeds for an existing folder this user cannot use, which would
        // otherwise look empty to list_layouts and only fail later in save_layout.
        try
        {
            _folderAccess.CheckCanList(LayoutsPath);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return $"Layout storage folder '{LayoutsPath}' exists but cannot be read: {ex.Message} " +
                   $"Set the {LayoutStorageOptions.LayoutsPathEnvironmentVariable} environment variable to a folder this user can read and write.";
        }

        try
        {
            _folderAccess.CheckCanWrite(LayoutsPath);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return $"Layout storage folder '{LayoutsPath}' exists but cannot be written: {ex.Message} " +
                   $"Set the {LayoutStorageOptions.LayoutsPathEnvironmentVariable} environment variable to a writable folder.";
        }

        InitializeFolderContents();
        _storageReady = true;
        return null;
    }

    /// <summary>
    /// One-time, best-effort migration and seeding. Never overwrites or deletes anything.
    /// </summary>
    private void InitializeFolderContents()
    {
        if (_options.MigrationSources.Count == 0 && _options.Samples.Count == 0)
            return;

        var markerPath = Path.Combine(LayoutsPath, InitializedMarkerFileName);
        if (File.Exists(markerPath))
            return;

        var knownNames = ReadPresetNames(LayoutsPath);
        MigrateLegacyPresets(knownNames);
        SeedSamplePresets(knownNames);

        try
        {
            File.WriteAllText(markerPath,
                "Helmsman migrated older layout presets and added the sample presets to this folder." + Environment.NewLine +
                "Delete this file to run that first-time setup again (existing presets are never overwritten)." + Environment.NewLine);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            // Best effort: without the marker the setup simply runs again on the next start.
        }
    }

    private void MigrateLegacyPresets(HashSet<string> knownNames)
    {
        var candidates = new List<(FileInfo File, LayoutPreset Preset)>();

        foreach (var source in _options.MigrationSources)
        {
            if (IsSameFolder(source, LayoutsPath))
                continue;

            try
            {
                var folder = new DirectoryInfo(source);
                if (!folder.Exists)
                    continue;

                foreach (var file in folder.EnumerateFiles("*.json"))
                {
                    if (TryReadFile(file.FullName, out var json) && TryParsePreset(json) is { } preset)
                        candidates.Add((file, preset));
                }
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                // Best effort: skip folders that cannot be read.
            }
        }

        // When several old versions have the same preset, the most recently written one wins.
        foreach (var group in candidates.GroupBy(c => c.Preset.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (knownNames.Contains(group.Key))
                continue;

            var newest = group.OrderByDescending(c => c.File.LastWriteTimeUtc).First();
            try
            {
                File.Copy(newest.File.FullName, GetAvailableFilePath(Path.GetFileNameWithoutExtension(newest.File.Name)), overwrite: false);
                knownNames.Add(group.Key);
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                // Best effort: the original file stays where it was.
            }
        }
    }

    private void SeedSamplePresets(HashSet<string> knownNames)
    {
        foreach (var sample in _options.Samples)
        {
            if (TryParsePreset(sample.Json) is not { } preset || knownNames.Contains(preset.Name))
                continue;

            var fileName = Path.GetFileName(sample.FileName);
            if (string.IsNullOrEmpty(fileName))
                continue;

            try
            {
                using var stream = new FileStream(Path.Combine(LayoutsPath, fileName), FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream);
                writer.Write(sample.Json);
                knownNames.Add(preset.Name);
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                // Best effort: an existing file with that name is never overwritten.
            }
        }
    }

    private static HashSet<string> ReadPresetNames(string folder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                if (TryReadFile(file, out var json) && TryParsePreset(json) is { } preset)
                    names.Add(preset.Name);
            }
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            // Treat an unreadable folder as empty.
        }

        return names;
    }

    /// <summary>
    /// Brings the cache in line with the folder, re-parsing only new or changed files. A file that is not a valid preset is
    /// kept with why, and logged as a warning once per version of it (its write time and length).
    /// </summary>
    private void RefreshCache()
    {
        IReadOnlyList<FileInfo> files;
        try
        {
            files = _folderAccess.ListPresetFiles(LayoutsPath);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            // Keep what we have rather than forgetting every preset on a transient error; it may be stale meanwhile, which is
            // logged when the listing starts to fail, not on every call while it does
            if (!_listingFailed)
            {
                _logger?.LogWarning(ex, "The layouts folder '{Folder}' could not be listed; the presets read from it before are used " +
                                        "until it can be, and may be stale", LayoutsPath);
            }

            _listingFailed = true;
            return;
        }

        if (_listingFailed)
            _logger?.LogInformation("The layouts folder '{Folder}' can be listed again", LayoutsPath);
        _listingFailed = false;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            seen.Add(file.FullName);

            if (_files.TryGetValue(file.FullName, out var cached) &&
                cached.LastWriteTimeUtc == file.LastWriteTimeUtc &&
                cached.Length == file.Length)
            {
                continue;
            }

            // If the file cannot be read right now (e.g. it is being written), keep the old entry and retry later.
            if (TryReadFile(file.FullName, out var json))
            {
                var (preset, error) = ParsePreset(json);
                _files[file.FullName] = new CachedPresetFile(file.LastWriteTimeUtc, file.Length, preset, error,
                    preset == null ? DeclaredNameOf(json) : null);
                if (error != null)
                {
                    _logger?.LogWarning("Layout file '{File}' is not a valid preset, so it is left out of the presets: {Error}",
                        file.FullName, error);
                }
            }
        }

        foreach (var removed in _files.Keys.Where(path => !seen.Contains(path)).ToList())
        {
            _files.Remove(removed);
        }
    }

    /// <summary>
    /// Maps preset names to the file that defines them. If two files define the same name, the most recently written wins.
    /// </summary>
    private Dictionary<string, (string Path, LayoutPreset Preset)> BuildNameIndex()
    {
        var index = new Dictionary<string, (string Path, LayoutPreset Preset)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, cached) in _files
                     .OrderBy(entry => entry.Value.LastWriteTimeUtc)
                     .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (cached.Preset is { } preset)
                index[preset.Name] = (path, preset);
        }

        return index;
    }

    private LayoutPreset? FindPreset(string name)
    {
        if (EnsureStorage() != null)
            return null;

        RefreshCache();
        return BuildNameIndex().TryGetValue(name, out var entry) ? entry.Preset : null;
    }

    /// <summary>The cached files that are not valid presets, with why, by file name (callers hold the gate).</summary>
    private List<InvalidLayoutFile> InvalidFiles() => _files
        .Where(entry => entry.Value.Preset == null)
        .Select(entry => new InvalidLayoutFile(Path.GetFileName(entry.Key), entry.Value.Error ?? "It is not a valid preset.",
            entry.Value.LastWriteTimeUtc, entry.Value.DeclaredName))
        .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// A file path for a new preset that no existing file or folder uses.
    /// </summary>
    private string GetAvailableFilePath(string name)
    {
        var baseName = SanitizeFileName(name);
        var candidate = Path.Combine(LayoutsPath, $"{baseName}.json");
        for (var suffix = 2; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(LayoutsPath, $"{baseName}-{suffix}.json");
        }

        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()))
            .Trim()
            .TrimEnd('.'); // Windows drops trailing dots and spaces from file names

        if (safeName.Length == 0)
            safeName = "layout";

        // CON, NUL, COM1 and friends are devices, even with an extension.
        if (ReservedDeviceNames.Contains(safeName.Split('.')[0]))
            safeName = "_" + safeName;

        return safeName;
    }

    internal static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Layout preset name must not be empty";

        if (name.Length > MaxNameLength)
            return $"Layout preset name must be at most {MaxNameLength} characters";

        return null;
    }

    private static LayoutPreset? TryParsePreset(string json) => ParsePreset(json).Preset;

    /// <summary>
    /// The preset a file holds, upgraded to schema version 2 in memory (the file is never rewritten, roadmap M6.8), or why it
    /// holds none, as a sentence: it is not JSON (or has a value of the wrong kind, such as a state given as a number), lacks
    /// a name or placements, has a placement without a matcher or position, or a position that is no rectangle
    /// (<see cref="LayoutPresetSchema.LoadedError"/>).
    /// </summary>
    private static (LayoutPreset? Preset, string? Error) ParsePreset(string json)
    {
        LayoutPreset? preset;
        try
        {
            preset = JsonSerializer.Deserialize<LayoutPreset>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (null, Sentence($"It is not valid layout JSON: {ex.Message}"));
        }

        // Records do not enforce non-nullable members during deserialization, so check the shape here.
        if (preset == null)
            return (null, "It holds no preset (its JSON is null).");
        if (string.IsNullOrWhiteSpace(preset.Name))
            return (null, "It has no name.");
        if (preset.Placements == null)
            return (null, "It has no placements.");

        for (var i = 0; i < preset.Placements.Count; i++)
        {
            var placement = preset.Placements[i];
            if (placement == null)
                return (null, $"placements[{i}] is empty.");
            if (placement.Matcher == null)
                return (null, $"placements[{i}] has no matcher.");
            if (placement.Position == null)
                return (null, $"placements[{i}] has no position.");
        }

        // A position that is no rectangle a window can have is never applied (review finding F2)
        if (LayoutPresetSchema.LoadedError(preset) is { } positionError)
            return (null, Sentence(positionError));

        return (LayoutPresetSchema.Upgrade(preset), null);
    }

    /// <summary>The name an invalid file gives (its "name" when its JSON can be read that far), trimmed; null otherwise.</summary>
    private static string? DeclaredNameOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(name.GetString())
                ? name.GetString()!.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The text with a full stop at its end, so a message can go on after it.</summary>
    private static string Sentence(string text) => text.TrimEnd().EndsWith('.') ? text.TrimEnd() : text.TrimEnd() + ".";

    private static bool TryReadFile(string path, out string content)
    {
        try
        {
            content = File.ReadAllText(path);
            return true;
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            content = string.Empty;
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            // Best effort cleanup of a temp file.
        }
    }

    private static bool IsSameFolder(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return false;
        }
    }

    internal static bool IsFileSystemError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException;

    #endregion
}
