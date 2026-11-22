namespace Helmsman.Services;

/// <summary>
/// A key chord that send_keys refuses without an explicit target window (DestructiveChordNeedsTarget): it
/// can close, quit or lock whatever window is in front, which is often the agent's own terminal.
/// </summary>
/// <param name="Spec">The chord as configured, in send_keys syntax (for example <c>%{F4}</c>).</param>
/// <param name="Chord">The key and modifiers it stands for. The same key with more modifiers matches too.</param>
public sealed record DestructiveChord(string Spec, KeyChord Chord);

/// <summary>
/// Settings of the input guard that the user can change in the server's environment.
/// </summary>
/// <param name="DestructiveChords">The chords that need an explicit target window.</param>
/// <param name="InvalidChordEntries">
/// Entries of <see cref="DestructiveChordsVariable"/> that are not a single chord in send_keys syntax;
/// they are ignored and logged.
/// </param>
/// <param name="DestructiveChordSource">"default", or <see cref="DestructiveChordsVariable"/>.</param>
public sealed record InputGuardOptions(
    IReadOnlyList<DestructiveChord> DestructiveChords,
    IReadOnlyList<string> InvalidChordEntries,
    string DestructiveChordSource)
{
    /// <summary>
    /// Environment variable with the destructive chords, separated by spaces (commas and semicolons are
    /// keys). It replaces the default list; the entry "default" puts the default list back in, and "none"
    /// alone turns the rule off. Entries are in send_keys syntax (<c>#</c> is the Windows key). An entry that is
    /// not one chord is ignored and logged, and the default list then stays in force as well.
    /// </summary>
    public const string DestructiveChordsVariable = "WAM_DESTRUCTIVE_CHORDS";

    /// <summary>The value of <see cref="DestructiveChordSource"/> when the default list applies.</summary>
    public const string DefaultSource = "default";

    /// <summary>
    /// Alt+F4 (close the window), Ctrl+W (close the tab or window), Ctrl+Q (quit), Ctrl+F4 (close the
    /// document or tab), Ctrl+Shift+Esc (Task Manager) and Win+L (lock the session; Windows ignores it when injected,
    /// so it stays listed in case it does not).
    /// </summary>
    public static IReadOnlyList<string> DefaultDestructiveChords { get; } = ["%{F4}", "^w", "^q", "^{F4}", "^+{ESC}", "#l"];

    /// <summary>
    /// Chords that hand the keyboard to another window: Alt+Tab, Alt+Esc (switch windows), Ctrl+Esc and the
    /// Windows key (the Start menu), and Alt+Space (the window menu, whose items minimize or close the
    /// window). Together with the destructive chords, which close the window, they must be the last key of
    /// a send_keys call: keys after them would reach a window the guard never checked. Fixed, not
    /// configurable.
    /// </summary>
    public static IReadOnlyList<string> FocusChangingChords { get; } = ["%{TAB}", "%{ESC}", "^{ESC}", "%{SPACE}", "{LWIN}", "{RWIN}"];

    private static readonly IReadOnlyList<(string Spec, KeyChord Chord)> FocusChanging = FocusChangingChords
        .Select(spec => (spec, SendKeysParser.KeyPresses(SendKeysParser.Parse(spec)).Single()))
        .ToList();

    /// <summary>
    /// Chords no key tool sends, with or without a target, and no run_steps step: Win+R opens the Run dialog and Win+X the
    /// power-user menu (the terminal as administrator, Shut down), where whatever is typed next runs as a command, and the
    /// server does not run commands for the agent (InterpreterGuard is the launch side). As for the destructive chords, the
    /// same key with more modifiers counts (Win+Shift+R), and either Windows key. Fixed, not configurable:
    /// <see cref="DestructiveChordsVariable"/> only decides which chords need a target, and nothing else lists chords to refuse.
    /// </summary>
    /// <remarks>
    /// The other ways to the same place stay open, and are left to the target window rules: the Windows key alone or Win+S
    /// and then a program name and Enter (Start search runs it), a click on Start, Task Manager's Run new task, File
    /// Explorer's address bar, and a command typed into a terminal that is open already (not the agent's own).
    /// </remarks>
    public static IReadOnlyList<string> RefusedChords { get; } = ["#r", "#x"];

    private static readonly IReadOnlyList<(string Spec, KeyChord Chord)> Refused = RefusedChords
        .Select(spec => (spec, SendKeysParser.Chords(SendKeysParser.Parse(spec)).Single()))
        .ToList();

    /// <summary>
    /// The first chord of <see cref="RefusedChords"/> pressed in <paramref name="events"/> (parsed send_keys or press_keys
    /// input), as its spec, or null. Reads the whole call, so a Windows key an earlier event holds down ({LWIN down}) counts
    /// for a key pressed in a later batch. Matched by virtual key: the server sends no scan-code-only key events, and a
    /// letter of another layout on the R or X key ('к' on a Russian keyboard) is parsed to that key.
    /// </summary>
    public static string? FindRefusedChord(IReadOnlyList<Models.InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var chord in SendKeysParser.Chords(events))
        {
            if (Refused.FirstOrDefault(r => r.Chord.IsPartOf(chord)) is { Spec: { } spec })
            {
                return spec;
            }
        }

        return null;
    }

    /// <summary>What a refused chord opens, for its refusal: "the Run dialog".</summary>
    public static string RefusedChordOpens(string spec) => spec == "#x" ? "the power-user menu (Win+X)" : "the Run dialog (Win+R)";

    /// <summary>The options when nothing is configured.</summary>
    public static InputGuardOptions Default { get; } = FromEnvironment(_ => null);

    /// <summary>Reads the options from the server's environment.</summary>
    public static InputGuardOptions FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>Reads the options from <paramref name="readVariable"/>; an unset or empty variable keeps the defaults.</summary>
    public static InputGuardOptions FromEnvironment(Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);

        var entries = (readVariable(DestructiveChordsVariable) ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0)
        {
            return Build(DefaultDestructiveChords, DefaultSource);
        }

        if (entries.Length == 1 && string.Equals(entries[0], "none", StringComparison.OrdinalIgnoreCase))
        {
            return new InputGuardOptions([], [], DestructiveChordsVariable);
        }

        var specs = entries.SelectMany(entry =>
            string.Equals(entry, "default", StringComparison.OrdinalIgnoreCase) ? DefaultDestructiveChords : [entry]).ToList();
        var options = Build(specs, DestructiveChordsVariable);

        // Fails closed: a mistyped entry (a comma-separated list, say) keeps the default chords in force too,
        // instead of silently leaving Alt+F4 and the others unprotected
        return options.InvalidChordEntries.Count == 0
            ? options
            : Build(specs.Concat(DefaultDestructiveChords), DestructiveChordsVariable);
    }

    /// <summary>
    /// The first destructive chord pressed in <paramref name="events"/> (parsed send_keys input), or null.
    /// </summary>
    public DestructiveChord? FindDestructiveChord(IReadOnlyList<Models.InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var pressed = SendKeysParser.Chords(events);
        foreach (var chord in pressed)
        {
            if (DestructiveChords.FirstOrDefault(d => d.Chord.IsPartOf(chord)) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// The first chord in <paramref name="events"/> (parsed send_keys input) that moves the keyboard to
    /// another window (<see cref="FocusChangingChords"/>, or any chord with the Windows key, which opens a part of
    /// Windows such as Run, Search or the clipboard history) or closes the window (a destructive chord) and is
    /// followed by more key presses, as its spec; null when there is none. Only the last key of a call may be
    /// such a chord.
    /// </summary>
    public string? FindKeysAfterFocusChange(IReadOnlyList<Models.InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var pressed = SendKeysParser.KeyPresses(events);
        for (var i = 0; i < pressed.Count - 1; i++)
        {
            if (FocusChanging.FirstOrDefault(f => f.Chord.IsPartOf(pressed[i])) is { Spec: { } focus })
            {
                return focus;
            }

            if (DestructiveChords.FirstOrDefault(d => d.Chord.IsPartOf(pressed[i])) is { } destructive)
            {
                return destructive.Spec;
            }

            if ((pressed[i].Modifiers & KeyModifiers.Windows) != 0)
            {
                return SendKeysParser.Spec(pressed[i]);
            }
        }

        return null;
    }

    private static InputGuardOptions Build(IEnumerable<string> specs, string source)
    {
        var chords = new List<DestructiveChord>();
        var invalid = new List<string>();
        foreach (var spec in specs.Distinct(StringComparer.Ordinal))
        {
            if (TryParseChord(spec) is { } chord)
            {
                chords.Add(new DestructiveChord(spec, chord));
            }
            else
            {
                invalid.Add(spec);
            }
        }

        return new InputGuardOptions(chords, invalid, source);
    }

    /// <summary>
    /// The chord <paramref name="spec"/> stands for, or null when it is not exactly one key press. No window's layout applies
    /// here, so a character is read as the invariant layout reads it; send_keys sends punctuation as the key of the target's
    /// layout, so + , - and . are read as the keys every layout has for them (VK_OEM_PLUS, VK_OEM_COMMA, VK_OEM_MINUS,
    /// VK_OEM_PERIOD). Other punctuation is matched only where send_keys sends it as Unicode; name its key instead
    /// (<c>^{OEM_2}</c>).
    /// </summary>
    private static KeyChord? TryParseChord(string spec)
    {
        try
        {
            var chords = SendKeysParser.Chords(SendKeysParser.Parse(spec));
            return chords.Count != 1 ? null
                : chords[0] is { Kind: Models.InputEventKind.Unicode } chord && PunctuationKey((char)chord.Code) is var key && key != 0
                    ? new KeyChord(chord.Modifiers, Models.InputEventKind.VirtualKey, key)
                    : chords[0];
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static ushort PunctuationKey(char c) => c switch
    {
        '+' => 0xBB,
        ',' => 0xBC,
        '-' => 0xBD,
        '.' => 0xBE,
        _ => 0
    };
}
