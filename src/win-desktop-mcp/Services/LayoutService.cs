using WinDesktopMcp.Models;
using WinDesktopMcp.Models.Results;
using WinDesktopMcp.Services.Interfaces;

namespace WinDesktopMcp.Services;

/// <summary>
/// The layout tools' one entry to the layout services (<see cref="ILayoutService"/>): it only hands each call to the part that
/// does it. <see cref="ILayoutPresetStore"/> stores the presets, <see cref="ILayoutCapturer"/> makes the ones save_layout
/// saves, <see cref="ILaunchConfirmationStore"/> keeps the user's confirmations of an agent-written preset's launches and
/// <see cref="ILayoutApplier"/> applies a preset. A consumer that needs only one of them takes that one.
/// </summary>
public class LayoutService : ILayoutService
{
    /// <summary>
    /// Maximum length of a preset name.
    /// </summary>
    public const int MaxNameLength = LayoutPresetStore.MaxNameLength;

    private readonly ILayoutPresetStore _store;
    private readonly ILaunchConfirmationStore _confirmations;
    private readonly ILayoutCapturer _capturer;
    private readonly ILayoutApplier _applier;

    /// <param name="store">Stores the presets: their reads, saves and deletes, under one gate.</param>
    /// <param name="confirmations">The launches of agent-written presets the user confirmed (owner decision D15).</param>
    /// <param name="capturer">Makes the presets save_layout saves, from the open windows or an agent's placements.</param>
    /// <param name="applier">apply_layout.</param>
    public LayoutService(
        ILayoutPresetStore store,
        ILaunchConfirmationStore confirmations,
        ILayoutCapturer capturer,
        ILayoutApplier applier)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _confirmations = confirmations ?? throw new ArgumentNullException(nameof(confirmations));
        _capturer = capturer ?? throw new ArgumentNullException(nameof(capturer));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
    }

    public string LayoutsPath => _store.LayoutsPath;

    public string? GetStorageError() => _store.GetStorageError();

    public IReadOnlyList<LayoutPreset> GetAllPresets() => _store.GetAllPresets();

    public LayoutPreset? GetPreset(string name) => _store.GetPreset(name);

    public IReadOnlyList<InvalidLayoutFile> GetInvalidFiles() => _store.GetInvalidFiles();

    /// <summary>
    /// What a "not found" message adds about the files that are not valid presets (review finding F1): the ones that may hold
    /// the preset <paramref name="name"/> (the name they give, or their file name as save_layout names files, matches), with
    /// why, and how many others there are. Empty when there are none, so the message stays as it was; it starts with a space
    /// otherwise.
    /// </summary>
    /// <param name="invalidFiles">The folder's invalid files (<see cref="ILayoutService.GetInvalidFiles"/>); null for none known.</param>
    public static string NotFoundNote(string? name, IReadOnlyList<InvalidLayoutFile>? invalidFiles) =>
        LayoutPresetStore.NotFoundNote(name, invalidFiles);

    public Task<LayoutSaveResult> SavePresetAsync(LayoutPreset preset, CancellationToken cancellationToken = default) =>
        _store.SavePresetAsync(preset, cancellationToken);

    public Task<LayoutDeleteResult> DeletePresetAsync(string name, CancellationToken cancellationToken = default) =>
        _store.DeletePresetAsync(name, cancellationToken);

    public Task<LayoutSaveResult> CaptureCurrentLayoutAsync(
        string name,
        string? description = null,
        string[]? includeProcesses = null,
        string[]? excludeProcesses = null,
        bool includeMinimized = false,
        CancellationToken cancellationToken = default) =>
        _capturer.CaptureCurrentLayoutAsync(name, description, includeProcesses, excludeProcesses, includeMinimized, cancellationToken);

    public LayoutSaveResult CaptureLayout(
        string name,
        string? description = null,
        string[]? includeProcesses = null,
        string[]? excludeProcesses = null,
        bool includeMinimized = false) =>
        _capturer.CaptureLayout(name, description, includeProcesses, excludeProcesses, includeMinimized);

    public Task<LayoutSaveResult> SaveAuthoredLayoutAsync(
        string name,
        string? description,
        IReadOnlyList<LayoutPlacementSpec> placements,
        CancellationToken cancellationToken = default) =>
        _capturer.SaveAuthoredLayoutAsync(name, description, placements, cancellationToken);

    /// <summary>
    /// The file in the layouts folder that keeps the confirmed launches (<see cref="LayoutPresetSchema.LaunchFingerprint"/>),
    /// one per line, so a confirmation holds across restarts. Not a .json file, so it is never read as a preset.
    /// </summary>
    public const string LaunchConfirmationsFileName = LaunchConfirmationStore.LaunchConfirmationsFileName;

    public bool AreLaunchesConfirmed(LayoutPreset preset) => _confirmations.AreLaunchesConfirmed(preset);

    public void ConfirmLaunches(LayoutPreset preset) => _confirmations.ConfirmLaunches(preset);

    public Task<LayoutApplyResult> ApplyPresetAsync(
        string name,
        LayoutMatchBy matchBy = LayoutMatchBy.ProcessAndTitle,
        bool launchMissing = false,
        LayoutApplyOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _applier.ApplyPresetAsync(name, matchBy, launchMissing, options, progress, cancellationToken);
}
