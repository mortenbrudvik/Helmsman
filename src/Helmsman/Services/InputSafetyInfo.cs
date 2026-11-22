using Helmsman.Models;
using Helmsman.Services.Interfaces;

namespace Helmsman.Services;

/// <summary>Reports the safety settings of the host guard, the target policy and the input guard.</summary>
public sealed class InputSafetyInfo(IHostGuard hostGuard, ITargetPolicy policy, InputGuardOptions options) : IInputSafetyInfo
{
    public InputSafetySnapshot Read() => new(
        hostGuard.Host,
        policy.Settings,
        options.DestructiveChords.Select(chord => chord.Spec).ToList(),
        options.DestructiveChordSource);
}
