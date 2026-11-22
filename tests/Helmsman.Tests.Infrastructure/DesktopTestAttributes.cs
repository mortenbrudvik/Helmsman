using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Helmsman.Tests.Infrastructure;

/// <summary>
/// The two desktop test tiers. Each has its own opt-in, and both are denied by default.
/// </summary>
public enum DesktopTier
{
    /// <summary>
    /// <see cref="DesktopNoInputFactAttribute"/>: tests that start the suite's own fixture app and only
    /// read it (find its windows, read its UI Automation tree, capture it). They send no keyboard or
    /// mouse input, act on no window and kill only the fixture process they started.
    /// Runs when <see cref="DesktopTestGate.NoInputEnvironmentVariable"/> or
    /// <see cref="DesktopTestGate.EnvironmentVariable"/> is <c>1</c>.
    /// </summary>
    NoInput,

    /// <summary>
    /// <see cref="DesktopFactAttribute"/>: tests with real desktop side effects, such as launching or
    /// killing processes, sending real input or focusing, moving or closing windows.
    /// Runs only when <see cref="DesktopTestGate.EnvironmentVariable"/> is <c>1</c>.
    /// </summary>
    Input
}

/// <summary>
/// Deny-by-default gate for desktop tests: tests that launch or kill real processes,
/// send real mouse or keyboard input, or move, focus or close real windows.
/// Such tests are skipped unless the environment variable
/// <see cref="EnvironmentVariable"/> is set to exactly <c>1</c>.
/// A narrower tier, <see cref="DesktopTier.NoInput"/>, covers tests that only start the suite's
/// own fixture app and read or capture it; <see cref="NoInputEnvironmentVariable"/> opts in to that
/// tier alone.
/// </summary>
/// <remarks>
/// The gate lives in the attributes, not in a runner filter, so it holds for every way the
/// suite can be started: plain <c>dotnet test</c>, an explicit <c>--filter</c>, IDE test
/// explorers and coverage tools. The <c>Category=Desktop</c> trait is reported as well so CI
/// can leave these tests out of the run entirely. This project is shared by the unit test project
/// and the end-to-end test project, so both use the same gate and the same variables.
/// <para>Each opt-in had a <c>WAM_</c> name before release 4.0.0 (<see cref="OldEnvironmentVariable"/>,
/// <see cref="OldNoInputEnvironmentVariable"/>), as the server's settings did. The old name is still read when the new
/// one is not present in the environment; when the new one is present, whatever its value, the old one is ignored
/// (<see cref="Read"/>), the rule the server applies to its settings.</para>
/// </remarks>
public static class DesktopTestGate
{
    /// <summary>Set this variable to <c>1</c> to run desktop tests of both tiers.</summary>
    public const string EnvironmentVariable = "HELMSMAN_ALLOW_DESKTOP_TESTS";

    /// <summary>
    /// Set this variable to <c>1</c> to run only the no-input tier
    /// (<see cref="DesktopNoInputFactAttribute"/>, <see cref="DesktopNoInputTheoryAttribute"/>).
    /// </summary>
    public const string NoInputEnvironmentVariable = "HELMSMAN_ALLOW_DESKTOP_NOINPUT_TESTS";

    /// <summary>The name of <see cref="EnvironmentVariable"/> before release 4.0.0, read when the new one is not set.</summary>
    public const string OldEnvironmentVariable = "WAM_ALLOW_DESKTOP_TESTS";

    /// <summary>The name of <see cref="NoInputEnvironmentVariable"/> before release 4.0.0, read when the new one is not set.</summary>
    public const string OldNoInputEnvironmentVariable = "WAM_ALLOW_DESKTOP_NOINPUT_TESTS";

    /// <summary>Trait name reported for every desktop test.</summary>
    public const string TraitName = "Category";

    /// <summary>Trait value reported for every desktop test, of both tiers.</summary>
    public const string TraitValue = "Desktop";

    /// <summary>Additional trait value reported for no-input desktop tests.</summary>
    public const string NoInputTraitValue = "DesktopNoInput";

    /// <summary>
    /// Returns the skip reason for a desktop test given the value of
    /// <see cref="EnvironmentVariable"/>, or null when desktop tests are allowed.
    /// </summary>
    public static string? GetSkipReason(string? environmentValue) =>
        environmentValue == "1"
            ? null
            : "Desktop test: it launches, kills or drives real applications on this machine. " +
              $"Set {EnvironmentVariable}=1 to run it.";

    /// <summary>
    /// Returns the skip reason for a no-input desktop test given the values of
    /// <see cref="NoInputEnvironmentVariable"/> and <see cref="EnvironmentVariable"/>, or null when it is
    /// allowed. The full opt-in includes the no-input tier.
    /// </summary>
    public static string? GetNoInputSkipReason(string? noInputValue, string? desktopValue) =>
        noInputValue == "1" || desktopValue == "1"
            ? null
            : "Desktop no-input test: it starts the suite's own fixture app and reads or captures its windows, " +
              $"without sending input. Set {NoInputEnvironmentVariable}=1 (or {EnvironmentVariable}=1) to run it.";

    /// <summary>
    /// The value of the opt-in <paramref name="name"/> (<see cref="EnvironmentVariable"/> or
    /// <see cref="NoInputEnvironmentVariable"/>) through <paramref name="readVariable"/>: its own when it is present, even
    /// empty; otherwise its old name's; null when neither is present.
    /// </summary>
    public static string? Read(Func<string, string?> readVariable, string name)
    {
        ArgumentNullException.ThrowIfNull(readVariable);
        var oldName = name switch
        {
            EnvironmentVariable => OldEnvironmentVariable,
            NoInputEnvironmentVariable => OldNoInputEnvironmentVariable,
            _ => throw new ArgumentException($"'{name}' is not an opt-in of the desktop test gate.", nameof(name))
        };

        return readVariable(name) ?? readVariable(oldName);
    }

    /// <summary>
    /// Returns the skip reason for a test of <paramref name="tier"/>, reading the opt-in variables
    /// through <paramref name="readVariable"/> (each under its old name when the new one is not set, <see cref="Read"/>),
    /// or null when the tier is allowed.
    /// </summary>
    public static string? GetSkipReason(DesktopTier tier, Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);

        return tier switch
        {
            DesktopTier.Input => GetSkipReason(Read(readVariable, EnvironmentVariable)),
            DesktopTier.NoInput => GetNoInputSkipReason(
                Read(readVariable, NoInputEnvironmentVariable),
                Read(readVariable, EnvironmentVariable)),
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown desktop tier.")
        };
    }

    /// <summary>
    /// Returns the skip reason for a test of <paramref name="tier"/> that also needs <paramref name="requirement"/> (an
    /// <see cref="IDesktopTestRequirement"/> type, or null for none), or null when it can run. The tier's opt-in is checked
    /// first, so a requirement is never probed in a run that did not opt in.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="requirement"/> is no <see cref="IDesktopTestRequirement"/> with a
    /// public parameterless constructor.</exception>
    public static string? GetSkipReason(DesktopTier tier, Type? requirement, Func<string, string?> readVariable)
    {
        var reason = GetSkipReason(tier, readVariable);
        if (reason != null || requirement == null)
        {
            return reason;
        }

        if (!typeof(IDesktopTestRequirement).IsAssignableFrom(requirement) || requirement.GetConstructor(Type.EmptyTypes) == null)
        {
            throw new ArgumentException(
                $"{requirement.FullName} is no {nameof(IDesktopTestRequirement)} with a public parameterless constructor.",
                nameof(requirement));
        }

        return ((IDesktopTestRequirement)Activator.CreateInstance(requirement)!).MissingReason;
    }

    /// <summary>The skip reason for the current process environment.</summary>
    public static string? CurrentSkipReason =>
        GetSkipReason(DesktopTier.Input, Environment.GetEnvironmentVariable);

    /// <summary>The skip reason for a no-input test in the current process environment.</summary>
    public static string? CurrentNoInputSkipReason =>
        GetSkipReason(DesktopTier.NoInput, Environment.GetEnvironmentVariable);

    /// <summary>
    /// Throws unless <paramref name="tier"/> is opted in. Harness code that starts processes or builds a
    /// desktop-facing server calls this first, so a test that forgot its gate attribute still cannot run it.
    /// </summary>
    /// <param name="tier">The tier the caller needs.</param>
    /// <param name="readVariable">Reads an environment variable; defaults to the process environment.</param>
    /// <exception cref="InvalidOperationException">The tier is not opted in.</exception>
    public static void ThrowIfNotAllowed(DesktopTier tier, Func<string, string?>? readVariable = null)
    {
        var reason = GetSkipReason(tier, readVariable ?? Environment.GetEnvironmentVariable);
        if (reason != null)
        {
            throw new InvalidOperationException(
                $"Refusing to touch the desktop from a test that is not opted in to the {tier} tier. {reason}");
        }
    }
}

/// <summary>
/// Something a desktop test needs besides its tier's opt-in, such as a runtime the machine may not have (roadmap M3.F4).
/// A test names it with its gate attribute's <c>Requires</c>, and is then skipped with <see cref="MissingReason"/> on a
/// machine without it, rather than failed. An implementation has a public parameterless constructor and only reads the
/// machine's state: it is created when the test is discovered, once the tier is opted in.
/// </summary>
public interface IDesktopTestRequirement
{
    /// <summary>Why the test cannot run on this machine, or null when it can.</summary>
    string? MissingReason { get; }
}

/// <summary>
/// A <see cref="FactAttribute"/> for a test with real desktop side effects. It is skipped
/// unless <c>HELMSMAN_ALLOW_DESKTOP_TESTS=1</c> and always carries the trait <c>Category=Desktop</c>.
/// </summary>
[TraitDiscoverer(DesktopTraitDiscoverer.TypeName, DesktopTraitDiscoverer.AssemblyName)]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DesktopFactAttribute : FactAttribute, ITraitAttribute
{
    private Type? _requires;

    public DesktopFactAttribute()
    {
        Skip = DesktopTestGate.CurrentSkipReason;
    }

    /// <summary>
    /// An <see cref="IDesktopTestRequirement"/> type the test needs besides the opt-in (roadmap M3.F4), such as a runtime
    /// the machine may not have: the test is skipped with its reason when it is missing. Checked only once the tier is opted in.
    /// </summary>
    public Type? Requires
    {
        get => _requires;
        set
        {
            _requires = value;
            Skip = DesktopTestGate.GetSkipReason(DesktopTier.Input, value, Environment.GetEnvironmentVariable);
        }
    }
}

/// <summary>
/// A <see cref="TheoryAttribute"/> for a test with real desktop side effects. It is skipped
/// unless <c>HELMSMAN_ALLOW_DESKTOP_TESTS=1</c> and always carries the trait <c>Category=Desktop</c>.
/// </summary>
[TraitDiscoverer(DesktopTraitDiscoverer.TypeName, DesktopTraitDiscoverer.AssemblyName)]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DesktopTheoryAttribute : TheoryAttribute, ITraitAttribute
{
    private Type? _requires;

    public DesktopTheoryAttribute()
    {
        Skip = DesktopTestGate.CurrentSkipReason;
    }

    /// <summary>
    /// An <see cref="IDesktopTestRequirement"/> type the test needs besides the opt-in (roadmap M3.F4), such as a runtime
    /// the machine may not have: the test is skipped with its reason when it is missing. Checked only once the tier is opted in.
    /// </summary>
    public Type? Requires
    {
        get => _requires;
        set
        {
            _requires = value;
            Skip = DesktopTestGate.GetSkipReason(DesktopTier.Input, value, Environment.GetEnvironmentVariable);
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for a no-input desktop test: it starts the suite's own fixture app and
/// only reads or captures it. It is skipped unless <c>HELMSMAN_ALLOW_DESKTOP_NOINPUT_TESTS=1</c> or
/// <c>HELMSMAN_ALLOW_DESKTOP_TESTS=1</c>, and carries the traits <c>Category=Desktop</c> and
/// <c>Category=DesktopNoInput</c>.
/// </summary>
[TraitDiscoverer(DesktopNoInputTraitDiscoverer.TypeName, DesktopNoInputTraitDiscoverer.AssemblyName)]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DesktopNoInputFactAttribute : FactAttribute, ITraitAttribute
{
    private Type? _requires;

    public DesktopNoInputFactAttribute()
    {
        Skip = DesktopTestGate.CurrentNoInputSkipReason;
    }

    /// <summary>
    /// An <see cref="IDesktopTestRequirement"/> type the test needs besides the opt-in (roadmap M3.F4), such as a runtime
    /// the machine may not have: the test is skipped with its reason when it is missing. Checked only once the tier is opted in.
    /// </summary>
    public Type? Requires
    {
        get => _requires;
        set
        {
            _requires = value;
            Skip = DesktopTestGate.GetSkipReason(DesktopTier.NoInput, value, Environment.GetEnvironmentVariable);
        }
    }
}

/// <summary>
/// A <see cref="TheoryAttribute"/> for a no-input desktop test. It is skipped unless
/// <c>HELMSMAN_ALLOW_DESKTOP_NOINPUT_TESTS=1</c> or <c>HELMSMAN_ALLOW_DESKTOP_TESTS=1</c>, and carries the traits
/// <c>Category=Desktop</c> and <c>Category=DesktopNoInput</c>.
/// </summary>
[TraitDiscoverer(DesktopNoInputTraitDiscoverer.TypeName, DesktopNoInputTraitDiscoverer.AssemblyName)]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DesktopNoInputTheoryAttribute : TheoryAttribute, ITraitAttribute
{
    private Type? _requires;

    public DesktopNoInputTheoryAttribute()
    {
        Skip = DesktopTestGate.CurrentNoInputSkipReason;
    }

    /// <summary>
    /// An <see cref="IDesktopTestRequirement"/> type the test needs besides the opt-in (roadmap M3.F4), such as a runtime
    /// the machine may not have: the test is skipped with its reason when it is missing. Checked only once the tier is opted in.
    /// </summary>
    public Type? Requires
    {
        get => _requires;
        set
        {
            _requires = value;
            Skip = DesktopTestGate.GetSkipReason(DesktopTier.NoInput, value, Environment.GetEnvironmentVariable);
        }
    }
}

/// <summary>
/// Reports <c>Category=Desktop</c> for tests marked with <see cref="DesktopFactAttribute"/>
/// or <see cref="DesktopTheoryAttribute"/>, so <c>--filter "Category!=Desktop"</c> works.
/// </summary>
public sealed class DesktopTraitDiscoverer : ITraitDiscoverer
{
    public const string TypeName = "Helmsman.Tests.Infrastructure.DesktopTraitDiscoverer";
    public const string AssemblyName = "Helmsman.Tests.Infrastructure";

    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>(DesktopTestGate.TraitName, DesktopTestGate.TraitValue);
    }
}

/// <summary>
/// Reports <c>Category=Desktop</c> and <c>Category=DesktopNoInput</c> for tests marked with
/// <see cref="DesktopNoInputFactAttribute"/> or <see cref="DesktopNoInputTheoryAttribute"/>.
/// A test filter treats a trait with several values as matching any of them, so
/// <c>Category!=Desktop</c> leaves these tests out, <c>Category=Desktop</c> includes them and
/// <c>Category=DesktopNoInput</c> selects only them.
/// </summary>
public sealed class DesktopNoInputTraitDiscoverer : ITraitDiscoverer
{
    public const string TypeName = "Helmsman.Tests.Infrastructure.DesktopNoInputTraitDiscoverer";
    public const string AssemblyName = DesktopTraitDiscoverer.AssemblyName;

    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>(DesktopTestGate.TraitName, DesktopTestGate.TraitValue);
        yield return new KeyValuePair<string, string>(DesktopTestGate.TraitName, DesktopTestGate.NoInputTraitValue);
    }
}
