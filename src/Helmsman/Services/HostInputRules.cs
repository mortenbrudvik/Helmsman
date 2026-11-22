using Helmsman.Models;

namespace Helmsman.Services;

/// <summary>
/// The allowlist for a call that carries the user's yes to input into the agent's own host window (allowHostTarget): only
/// input whose effect the question can state unmistakably goes there. A key the question can only name (⟨APPS⟩, ⟨F10⟩) may
/// do anything in the host, such as opening a menu whose Paste item puts the clipboard in, so it is refused, before the
/// question and again inside the input gate before every batch.
/// <list type="bullet">
/// <item>Text: a character the question shows as itself (<see cref="ShownInput.IsShownAsItself"/>), sent as itself or typed by
/// a key of the window's keyboard layout with no modifier but Shift (or AltGr for the character the layout types with it);
/// and a line break, tab or carriage return, shown as ⏎, ⇥ and ⟨CR⟩.</item>
/// <item>Enter, Tab and Backspace, alone or with Shift.</item>
/// <item>The arrows, Home, End, Page Up and Page Down, alone or with Shift, Ctrl or both; Delete alone or with Ctrl (Shift+Delete
/// cuts the host's text to the clipboard); Esc alone or with Shift (Ctrl+Esc opens the Start menu, Ctrl+Shift+Esc Task
/// Manager).</item>
/// <item>Ctrl with one of <see cref="CtrlLetters"/> (A C D J M U Z), the control input of terminals; Ctrl+M and Ctrl+J count as
/// Enter in the question. Not E, H, K or L: in common hosts they open Quick Open, Replace, a browser's address bar or a chord
/// whose next key arrives as text, which the question could only name.</item>
/// </list>
/// Everything else is refused: a modifier pressed alone (Alt opens the menu bar), Alt or Win with anything, Ctrl with Alt
/// (on a layout with AltGr an explicit Ctrl+Alt chord types a character), any other Ctrl chord, the Apps key, the function
/// keys, the numeric keypad (whose keys depend on Num Lock), and every key without a name or that types nothing the question
/// can show. Mouse events are left to <see cref="MouseRefusal"/>: a primary click, a pointer move and a wheel scroll go there,
/// and nothing else (InputSimulationService refuses a right or middle click and a drag as pastes first, and a modifier held
/// around mouse input as a chord).
/// </summary>
public static class HostInputRules
{
    /// <summary>
    /// Copies of the Win32 values the rules use, under NativeEnums' own names: Services may not depend on Native (the layering
    /// test, NamespaceDependencyTests), so NativeEnumsMirrorTests pins each one to the NativeEnums constant of the same name.
    /// </summary>
    internal static class Win32
    {
        internal const uint KEYEVENTF_KEYUP = 0x0002;
        internal const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_PRIOR = 0x21, VK_NEXT = 0x22,
            VK_END = 0x23, VK_HOME = 0x24, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_DELETE = 0x2E,
            VK_NUMPAD0 = 0x60, VK_DIVIDE = 0x6F, VK_LCONTROL = 0xA2, VK_RMENU = 0xA5;
        internal const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
            MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040,
            MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000,
            MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
        internal const uint XBUTTON1 = 0x0001, XBUTTON2 = 0x0002;
    }

    internal const uint KeyUp = Win32.KEYEVENTF_KEYUP;
    internal const ushort VkBack = Win32.VK_BACK, VkTab = Win32.VK_TAB, VkReturn = Win32.VK_RETURN, VkEscape = Win32.VK_ESCAPE,
        VkDelete = Win32.VK_DELETE, VkLeftControl = Win32.VK_LCONTROL, VkRightAlt = Win32.VK_RMENU;

    /// <summary>The letters Ctrl may be pressed with: a terminal's control input (interrupt, end of input, line editing, Enter).</summary>
    public static IReadOnlySet<char> CtrlLetters { get; } = new HashSet<char> { 'A', 'C', 'D', 'J', 'M', 'U', 'Z' };

    private static readonly HashSet<ushort> Navigation =
        [Win32.VK_PRIOR, Win32.VK_NEXT, Win32.VK_END, Win32.VK_HOME, Win32.VK_LEFT, Win32.VK_UP, Win32.VK_RIGHT, Win32.VK_DOWN];

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

    /// <summary>
    /// The first mouse event in <paramref name="asUnswapped"/> that may not go to the agent's host, described for the refusal;
    /// null when every one may. Only these go there: a pointer move (MOUSEEVENTF_MOVE, absolute or not), a press or release of
    /// the left button, and one vertical or horizontal wheel event. Anything else is refused, whatever it is: the right,
    /// middle or an X button (x1, x2) going down or up, and an event that does two things at once. The events must name the
    /// buttons as the user knows them (MouseBatch.AsUnswapped: the primary one as the left), not as SendInput gets them with
    /// swapped buttons. Keyboard events are left to <see cref="Refusal"/>. The tools check the button before the question
    /// (<see cref="ShownInput.HostButtonRefusal"/>); this is the check right before the batch.
    /// </summary>
    public static string? MouseRefusal(IReadOnlyList<InputEvent> asUnswapped)
    {
        ArgumentNullException.ThrowIfNull(asUnswapped);
        const uint Move = Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK;
        foreach (var e in asUnswapped)
        {
            if (e.Kind != InputEventKind.Mouse)
            {
                continue;
            }

            var allowed = e.Flags switch
            {
                Win32.MOUSEEVENTF_LEFTDOWN or Win32.MOUSEEVENTF_LEFTUP => e.MouseData == 0,
                Win32.MOUSEEVENTF_WHEEL or Win32.MOUSEEVENTF_HWHEEL => true,
                var flags => (flags & Win32.MOUSEEVENTF_MOVE) != 0 && (flags & ~Move) == 0
            };
            if (!allowed)
            {
                return MouseEventName(e);
            }
        }

        return null;
    }

    /// <summary>A mouse event as the refusal names it: its button by name ("the x1 mouse button going down"), else its flags.</summary>
    private static string MouseEventName(InputEvent e)
    {
        var x = e.MouseData switch { Win32.XBUTTON1 => "the x1", Win32.XBUTTON2 => "the x2", _ => "an X" };
        return e.Flags switch
        {
            Win32.MOUSEEVENTF_RIGHTDOWN => "the secondary mouse button going down",
            Win32.MOUSEEVENTF_RIGHTUP => "the secondary mouse button going up",
            Win32.MOUSEEVENTF_MIDDLEDOWN => "the middle mouse button going down",
            Win32.MOUSEEVENTF_MIDDLEUP => "the middle mouse button going up",
            Win32.MOUSEEVENTF_XDOWN => $"{x} mouse button going down",
            Win32.MOUSEEVENTF_XUP => $"{x} mouse button going up",
            var flags => $"a mouse event with the flags 0x{flags:X4}"
        };
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

        if (code == VkDelete)
        {
            return held is KeyModifiers.None or KeyModifiers.Control;
        }

        if (code == VkEscape)
        {
            return shiftAtMost;
        }

        // The numeric keypad types a digit or moves the caret by Num Lock, which the question cannot know
        if (code is >= Win32.VK_NUMPAD0 and <= Win32.VK_DIVIDE)
        {
            return false;
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
