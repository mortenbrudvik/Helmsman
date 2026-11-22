using Helmsman.Models;
using Helmsman.Models.Results;

namespace Helmsman.Services.Interfaces;

/// <summary>
/// apply_layout, behind <see cref="ILayoutService"/>: arranges a stored preset's windows and launches the missing ones.
/// </summary>
public interface ILayoutApplier
{
    /// <inheritdoc cref="ILayoutService.ApplyPresetAsync"/>
    Task<LayoutApplyResult> ApplyPresetAsync(
        string name,
        LayoutMatchBy matchBy = LayoutMatchBy.ProcessAndTitle,
        bool launchMissing = false,
        LayoutApplyOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
