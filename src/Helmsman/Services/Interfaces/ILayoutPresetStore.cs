using Helmsman.Models;
using Helmsman.Models.Results;

namespace Helmsman.Services.Interfaces;

/// <summary>
/// The layout presets as JSON files in one folder: the storage behind <see cref="ILayoutService"/>. One gate serializes every
/// read and write of the folder and its cache, so the one instance is safe for parallel tool calls.
/// </summary>
public interface ILayoutPresetStore
{
    /// <inheritdoc cref="ILayoutService.LayoutsPath"/>
    string LayoutsPath { get; }

    /// <inheritdoc cref="ILayoutService.GetStorageError"/>
    string? GetStorageError();

    /// <inheritdoc cref="ILayoutService.GetAllPresets"/>
    IReadOnlyList<LayoutPreset> GetAllPresets();

    /// <inheritdoc cref="ILayoutService.GetPreset"/>
    LayoutPreset? GetPreset(string name);

    /// <inheritdoc cref="ILayoutService.GetInvalidFiles"/>
    IReadOnlyList<InvalidLayoutFile> GetInvalidFiles();

    /// <inheritdoc cref="ILayoutService.SavePresetAsync"/>
    Task<LayoutSaveResult> SavePresetAsync(LayoutPreset preset, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ILayoutService.DeletePresetAsync"/>
    Task<LayoutDeleteResult> DeletePresetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// apply_layout's lookup, under one hold of the gate: why the storage cannot be used, else the preset named
    /// <paramref name="name"/> (null when there is none), and then what a "not found" message adds about the files that are
    /// not valid presets (<see cref="LayoutService.NotFoundNote"/>; empty when the preset was found).
    /// </summary>
    Task<(string? StorageError, LayoutPreset? Preset, string NotFoundNote)> FindForApplyAsync(
        string name,
        CancellationToken cancellationToken = default);
}
