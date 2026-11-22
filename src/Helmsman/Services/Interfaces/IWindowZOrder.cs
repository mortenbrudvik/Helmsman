using Helmsman.Models;

namespace Helmsman.Services.Interfaces;

/// <summary>set_window_zorder (roadmap M6.7): a window's place in the z-order, and the windows it made topmost.</summary>
public interface IWindowZOrder
{
    /// <inheritdoc cref="IWindowService.SetWindowZOrder"/>
    WindowChangeResult SetWindowZOrder(nint handle, ZOrderMode mode, nint? above = null);
}
