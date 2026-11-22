namespace Helmsman.Models;

/// <summary>
/// The server's settings: the environment variables the user configures it with, each named <c>HELMSMAN_*</c>, and the one
/// rule for the names they had before release 4.0.0, after the product's first name: a setting's old name is
/// <see cref="OldPrefix"/> and the same suffix. When the HELMSMAN_ variable holds a value (not empty or only white space),
/// it is used and its old twin is ignored. When it is not present, or empty, the old one is read in its place and parsed
/// exactly as before, so a deny list, read-only mode or any other safety setting given under its old name keeps applying
/// even beside an empty new name (an MCP client's env block that expands an unset variable passes one). Every setting
/// treats an empty or white-space value as not set, so nothing is lost by reading the old name then. Every options class
/// reads its variable through <see cref="Read"/>; get_server_info and the log at start warn about every old name in use
/// (<see cref="FindOld"/>). SettingNamesArchitectureTests keeps every HELMSMAN_ name in the server's source listed in
/// <see cref="All"/>, keeps every other server type (but ExecutableResolver, for PATH and PATHEXT) from calling
/// Environment.GetEnvironmentVariable(s), and keeps this class from handing out a public delegate over the environment.
/// </summary>
public static class SettingNames
{
    /// <summary>The prefix of every setting's name.</summary>
    public const string Prefix = "HELMSMAN_";

    /// <summary>The prefix of a setting's name before release 4.0.0, still read when the HELMSMAN_ name is not set or empty.</summary>
    public const string OldPrefix = "WAM_";

    public const string Toolsets = "HELMSMAN_TOOLSETS";
    public const string ExcludeTools = "HELMSMAN_EXCLUDE_TOOLS";
    public const string ReadOnly = "HELMSMAN_READ_ONLY";
    public const string DenyProcesses = "HELMSMAN_DENY_PROCESSES";
    public const string AllowProcesses = "HELMSMAN_ALLOW_PROCESSES";
    public const string DestructiveChords = "HELMSMAN_DESTRUCTIVE_CHORDS";
    public const string AllowedProtocols = "HELMSMAN_ALLOWED_PROTOCOLS";
    public const string AllowUserClipboard = "HELMSMAN_ALLOW_USER_CLIPBOARD";
    public const string PauseHotkey = "HELMSMAN_PAUSE_HOTKEY";
    public const string TakeoverDetection = "HELMSMAN_TAKEOVER_DETECTION";
    public const string AuditPath = "HELMSMAN_AUDIT_PATH";
    public const string LayoutsPath = "HELMSMAN_LAYOUTS_PATH";
    public const string UiaEngine = "HELMSMAN_UIA_ENGINE";
    public const string UiaProviderContains = "HELMSMAN_UIA_PROVIDER_CONTAINS";

    /// <summary>Every setting, in the order the warnings list them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Toolsets, ExcludeTools, ReadOnly, DenyProcesses, AllowProcesses, DestructiveChords, AllowedProtocols, AllowUserClipboard,
        PauseHotkey, TakeoverDetection, AuditPath, LayoutsPath, UiaEngine, UiaProviderContains
    ];

    /// <summary>
    /// The server process's environment, which <see cref="Read"/> and <see cref="FindOld"/> use when given no reader. The
    /// only place in the server that reads an environment variable by name, apart from PATH and PATHEXT (ExecutableResolver).
    /// Private, so a caller cannot read a HELMSMAN_ name past the fallback: it passes null instead.
    /// </summary>
    private static readonly Func<string, string?> ProcessEnvironment = Environment.GetEnvironmentVariable;

    /// <summary>The name <paramref name="name"/> had before release 4.0.0: <c>WAM_</c> and the same suffix.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not one of <see cref="All"/>.</exception>
    public static string OldNameOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!All.Contains(name, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{name}' is not one of the server's settings (SettingNames.All).", nameof(name));
        }

        return OldPrefix + name[Prefix.Length..];
    }

    /// <summary>
    /// The value of the setting <paramref name="name"/> in <paramref name="environment"/> (the process environment when null):
    /// the HELMSMAN_ variable's when it holds a value; otherwise the old name's when that is present; otherwise the
    /// HELMSMAN_ variable's empty value, or null when neither is present.
    /// </summary>
    /// <remarks>
    /// The desktop test gate (DesktopTestGate.Read) keeps the stricter rule that a present new name wins even when empty.
    /// Each fails safe: here an empty new name must not drop a safety setting given under the old one; there an empty new
    /// name keeps the gate closed.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not one of <see cref="All"/>.</exception>
    public static string? Read(Func<string, string?>? environment, string name)
    {
        var oldName = OldNameOf(name);
        var read = environment ?? ProcessEnvironment;
        var value = read(name);
        return IsSet(value) ? value : read(oldName) ?? value;
    }

    /// <summary>True when a variable's value counts as set: present and not empty or only white space.</summary>
    private static bool IsSet(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>The old names in use in <paramref name="environment"/> (the process environment when null).</summary>
    public static OldSettingNames FindOld(Func<string, string?>? environment = null)
    {
        var read = environment ?? ProcessEnvironment;
        var readInstead = new List<string>();
        var ignored = new List<string>();
        foreach (var name in All)
        {
            var oldValue = read(OldNameOf(name));
            if (oldValue == null)
            {
                continue;
            }

            // Read instead when the new name is not set (or empty); ignored only when both hold a value. An empty old name
            // beside a set new one changes nothing and is not mentioned.
            if (!IsSet(read(name)))
            {
                readInstead.Add(OldNameOf(name));
            }
            else if (IsSet(oldValue))
            {
                ignored.Add(OldNameOf(name));
            }
        }

        return new OldSettingNames(readInstead, ignored);
    }
}

/// <summary>
/// The settings still given under their names from before release 4.0.0 (<see cref="SettingNames"/>), and the warnings
/// get_server_info and the log at start give about them. The warnings name variables, never their values, which can hold
/// paths or process names.
/// </summary>
/// <param name="ReadInstead">Old names that are read because their HELMSMAN_ name is not set, or is empty.</param>
/// <param name="Ignored">Old names that hold a value but are ignored because their HELMSMAN_ name holds one too.</param>
public sealed record OldSettingNames(IReadOnlyList<string> ReadInstead, IReadOnlyList<string> Ignored)
{
    /// <summary>No old name in use.</summary>
    public static OldSettingNames None { get; } = new([], []);

    /// <summary>One warning for <see cref="ReadInstead"/> and one for <see cref="Ignored"/>, each only when it lists any.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var warnings = new List<string>();
            if (ReadInstead.Count > 0)
            {
                var one = ReadInstead.Count == 1;
                warnings.Add(
                    $"{List(ReadInstead)} {(one ? "is the name" : "are the names")} from before release 4.0.0 of " +
                    $"{List(NewNames(ReadInstead))}, which {(one ? "is" : "are")} not set (or empty), so the server reads " +
                    $"{(one ? "it" : "them")} instead. Rename {(one ? "it" : "them")} in the server's environment. Tell the user.");
            }

            if (Ignored.Count > 0)
            {
                var one = Ignored.Count == 1;
                warnings.Add(
                    $"{List(Ignored)} {(one ? "is" : "are")} ignored: {List(NewNames(Ignored))} {(one ? "is" : "are")} set too, " +
                    $"and the {SettingNames.Prefix} name wins. Remove the old {(one ? "name" : "names")} from the server's " +
                    "environment, after moving any value that was meant to the new name. Tell the user.");
            }

            return warnings;
        }
    }

    private static IEnumerable<string> NewNames(IEnumerable<string> oldNames) =>
        oldNames.Select(n => SettingNames.Prefix + n[SettingNames.OldPrefix.Length..]);

    private static string List(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 1 ? list[0] : $"{string.Join(", ", list[..^1])} and {list[^1]}";
    }
}
