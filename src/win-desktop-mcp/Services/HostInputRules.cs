using WinDesktopMcp.Models;

namespace WinDesktopMcp.Services;

/// <summary>
/// The allowlist for a call that carries the user's yes to input into the agent's own host window (allowHostTarget): only
/// input whose effect the question can state unmistakably goes there. A key the question can only name (⟨APPS⟩, ⟨F10⟩) may
/// do anything in the host, such as opening a menu whose Paste item puts the clipboard in, so it is refused, before the
/// question and again inside the input gate before every batch.
/// <list type="bullet">
/// <item>Text: a character the question shows as itself (<see cref="ShownInput.IsShownAsItself"/>), sent as itself or typed by
/// a key of the window's keyboard layout with no modifier but Shift (or AltGr for the character the layout types with it);
/// and a line break, tab or carriage return, shown as ⏎, ⇥ and Enter.</item>
/// <item>Enter, Tab and Backspace, alone or with Shift.</item>
/// <item>Navigation and editing keys: the arrows, Home, End, Page Up, Page Down, Delete and Esc, alone or with Shift, Ctrl or
/// both.</item>
/// <item>Ctrl with one of <see cref="CtrlLetters"/> (A C D E H J K L M U Z), the control input of terminals and editors; Ctrl+M
/// and Ctrl+J count as Enter in the question.</item>
/// </list>
/// Everything else is refused: a modifier pressed alone (Alt opens the menu bar), Alt or Win with anything, Ctrl with Alt
/// (on a layout with AltGr an explicit Ctrl+Alt chord types a character), any other Ctrl chord, the Apps key, the function
/// keys, the numeric keypad, and every key without a name or that types nothing the question can show. Mouse events are left
/// to the click rules (a right or middle click and a drag are refused as pastes).
/// </summary>
public static class HostInputRules
{
    // Win32 values, as NativeEnums has them (Services computes with no Native type; NativeEnumsMirrorTests pins them)
    internal const uint KeyUp = 0x0002;
    internal const ushort VkBack = 0x08, VkTab = 0x09, VkReturn = 0x0D, VkEscape = 0x1B, VkPrior = 0x21, VkNext = 0x22, VkEnd = 0x23,
        VkHome = 0x24, VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28, VkDelete = 0x2E, VkLeftControl = 0xA2, VkRightAlt = 0xA5;

    /// <summary>The letters Ctrl may be pressed with: a terminal's or an editor's control input (interrupt, end of input, line editing).</summary>
    public static IReadOnlySet<char> CtrlLetters { get; } = new HashSet<char> { 'A', 'C', 'D', 'E', 'H', 'J', 'K', 'L', 'M', 'U', 'Z' };

    private static readonly HashSet<ushort> Navigation = [VkPrior, VkNext, VkEnd, VkHome, VkLeft, VkUp, VkRight, VkDown, VkDelete, VkEscape];

    /// <summary>
    /// The first press in <paramref name="events"/> that may not go to the agent's host, described for the refusal ("the key
    /// ⟨APPS⟩"); null when every one may. Keys are read on <paramref name="layout"/>, the window's, as they are sent.
    /// </summary>
    public static string? Refusal(IReadOnlyList<InputEvent> events, KeyboardLayout layout)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(layout);

        var down = new List<(ushort Key, bool AltGr)>();
        ushort tapCandidate = 0;
        char? highSurrogate = null;
        foreach (var e in events)
        {
            if (e.Kind == InputEventKind.Mouse)
            {
                continue;
            }

            var up = (e.Flags & KeyUp) != 0;
            if (e.Kind == InputEventKind.VirtualKey && SendKeysParser.ModifierOf(e.Code) != KeyModifiers.None)
            {
                if (!up)
                {
                    down.Add((e.Code, e.AltGr));
                    tapCandidate = e.Code;
                    continue;
                }

                down.RemoveAll(d => d.Key == e.Code);
                if (tapCandidate == e.Code)
                {
                    return $"the key {Name(e.Code)} pressed alone";
                }

                tapCandidate = 0;
                continue;
            }

            if (up)
            {
                continue;
            }

            tapCandidate = 0;
            var held = Held(down, out var altGr);
            if (e.Kind == InputEventKind.Unicode)
            {
                var c = (char)e.Code;
                if (char.IsHighSurrogate(c))
                {
                    if (highSurrogate is { } unpaired)
                    {
                        return $"the character {ShownInput.CodePoint(unpaired)}";
                    }

                    highSurrogate = c;
                    continue;
                }

                string character;
                if (char.IsLowSurrogate(c) && highSurrogate is { } high)
                {
                    character = new string([high, c]);
                }
                else if (highSurrogate is { } unpaired)
                {
                    return $"the character {ShownInput.CodePoint(unpaired)}";
                }
                else
                {
                    character = c.ToString();
                }

                highSurrogate = null;
                if ((held & ~KeyModifiers.Shift) != KeyModifiers.None)
                {
                    if (held == KeyModifiers.Control && character.Length == 1 && CtrlLetters.Contains(char.ToUpperInvariant(character[0])))
                    {
                        continue;
                    }

                    return $"the chord {Chord(held, ShownInput.Escape(character))}";
                }

                if (ShownInput.FirstUnshown(character) is { } unshown)
                {
                    return $"the character {unshown}, which the question cannot show as itself";
                }

                continue;
            }

            if (highSurrogate is { } pending)
            {
                return $"the character {ShownInput.CodePoint(pending)}";
            }

            if (Allowed(e.Code, held, altGr, layout))
            {
                continue;
            }

            return $"the key {Chord(held, KeyTable.NameOf(e.Code) ?? KeyNames.Of(e.Code))}";
        }

        return highSurrogate is { } last ? $"the character {ShownInput.CodePoint(last)}" : null;
    }

    private static bool Allowed(ushort code, KeyModifiers held, bool altGr, KeyboardLayout layout)
    {
        var shiftAtMost = (held & ~KeyModifiers.Shift) == KeyModifiers.None;
        if (code is VkReturn or VkTab or VkBack)
        {
            return shiftAtMost;
        }

        if (Navigation.Contains(code))
        {
            return (held & ~(KeyModifiers.Shift | KeyModifiers.Control)) == KeyModifiers.None;
        }

        if (held == KeyModifiers.Control)
        {
            return code is >= 'A' and <= 'Z' && CtrlLetters.Contains((char)code);
        }

        // Text: what the key types on the window's layout, shown as itself
        return shiftAtMost && layout.TextOf(code, (held & KeyModifiers.Shift) != 0, altGr) is { Length: > 0 } typed &&
               ShownInput.FirstUnshown(typed) == null && !typed.Any(char.IsControl);
    }

    private static string Chord(KeyModifiers held, string name) =>
        $"{ShownInput.KeyOpen}" +
        ((held & KeyModifiers.Control) != 0 ? "Ctrl+" : string.Empty) +
        ((held & KeyModifiers.Alt) != 0 ? "Alt+" : string.Empty) +
        ((held & KeyModifiers.Shift) != 0 ? "Shift+" : string.Empty) +
        ((held & KeyModifiers.Windows) != 0 ? "Win+" : string.Empty) +
        $"{name}{ShownInput.KeyClose}";

    private static string Name(ushort code) => $"{ShownInput.KeyOpen}{KeyTable.NameOf(code) ?? KeyNames.Of(code)}{ShownInput.KeyClose}";

    /// <summary>The modifiers held; the left Ctrl and right Alt a parser pressed as AltGr for a character hold nothing, and set <paramref name="altGr"/>.</summary>
    private static KeyModifiers Held(List<(ushort Key, bool AltGr)> down, out bool altGr)
    {
        altGr = down.Contains((VkLeftControl, true)) && down.Contains((VkRightAlt, true));
        var held = KeyModifiers.None;
        foreach (var (key, isAltGr) in down)
        {
            if (!(altGr && isAltGr))
            {
                held |= SendKeysParser.ModifierOf(key);
            }
        }

        return held;
    }


    /// <summary>The refusal of input outside the allowlist, for the error (InvalidArguments).</summary>
    public static string Message(string what, string window) => ShownInput.HostAllowlistRefusal(what, window);
}
