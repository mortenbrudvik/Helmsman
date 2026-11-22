using Helmsman.Models;

namespace Helmsman.Services.Interfaces;

/// <summary>
/// The user's confirmations that a version of an agent-written preset may start its programs (owner decision D15), behind
/// <see cref="ILayoutService"/>: apply_layout checks them before it launches anything.
/// </summary>
public interface ILaunchConfirmationStore
{
    /// <inheritdoc cref="ILayoutService.AreLaunchesConfirmed"/>
    bool AreLaunchesConfirmed(LayoutPreset preset);

    /// <inheritdoc cref="ILayoutService.ConfirmLaunches"/>
    void ConfirmLaunches(LayoutPreset preset);
}
