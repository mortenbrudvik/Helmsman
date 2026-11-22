namespace Helmsman.Models;

/// <summary>
/// The server's settings: the environment variables the user configures it with, each named <c>HELMSMAN_*</c>, and the one
/// rule for the names they had before release 4.0.0, after the product's first name: a setting's old name is
/// <see cref="OldPrefix"/> and the same suffix. When the HELMSMAN_ variable is present in the environment, whatever its
/// value (an empty one included), it is used and its old twin is ignored. When it is not present, the old one is read in
/// its place and parsed exactly as before, so a deny list, read-only mode or any other safety setting given only under its
/// old name keeps applying. Every options class reads its variable through <see cref="Read"/>.
/// SettingNamesArchitectureTests keeps every HELMSMAN_ name in the server's source listed in <see cref="All"/>, and keeps
/// the options classes from reading the environment themselves.
/// </summary>
public static class SettingNames
{
    /// <summary>The prefix of every setting's name.</summary>
    public const string Prefix = "HELMSMAN_";

    /// <summary>The prefix of a setting's name before release 4.0.0, still read when the HELMSMAN_ name is not set.</summary>
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

    /// <summary>Every setting.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Toolsets, ExcludeTools, ReadOnly, DenyProcesses, AllowProcesses, DestructiveChords, AllowedProtocols, AllowUserClipboard,
        PauseHotkey, TakeoverDetection, AuditPath, LayoutsPath, UiaEngine, UiaProviderContains
    ];

    /// <summary>
    /// The server process's environment: the reader the options classes pass to <see cref="Read"/> by default. The only
    /// place in the server that reads an environment variable by name, apart from PATH and PATHEXT (ExecutableResolver).
    /// </summary>
    public static Func<string, string?> ProcessEnvironment { get; } = Environment.GetEnvironmentVariable;

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
    /// the HELMSMAN_ variable's when it is present, even empty; otherwise the old name's; null when neither is present.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not one of <see cref="All"/>.</exception>
    public static string? Read(Func<string, string?>? environment, string name)
    {
        var oldName = OldNameOf(name);
        var read = environment ?? ProcessEnvironment;
        return read(name) ?? read(oldName);
    }
}
