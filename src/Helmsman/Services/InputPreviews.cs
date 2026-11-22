using System.Text;
using Helmsman.Models;

namespace Helmsman.Services;

/// <summary>
/// Key and character events as the user's confirmation question shows them (<see cref="ShownInput"/>), before allowHostTarget
/// sends them to the agent's own host window. A character sent as itself is shown as that character. A key pressed with no
/// modifier but Shift (or a parser's AltGr) is shown as what it types on the keyboard layout the keys were read on, the
/// window's own (<see cref="KeyboardLayout.TextOf"/>), or as ⟨NAME⟩ when it types nothing there; every other key press as
/// ⟨Mods+NAME⟩, a modifier pressed and released alone as ⟨NAME⟩, and a run of the same key as ⟨NAME ×n⟩. The same events, read
/// on the same layout, go out after the user's yes, and the service refuses them when the window's layout changed meanwhile
/// (<see cref="HostTargetApproval.KeyboardLayout"/>); a test renders the events that were sent and compares.
/// </summary>
public static class InputPreviews
{
    private const uint KeyUp = HostInputRules.KeyUp;
    private const ushort VkReturn = HostInputRules.VkReturn, VkLeftControl = HostInputRules.VkLeftControl, VkRightAlt = HostInputRules.VkRightAlt;

    /// <summary>
    /// The events' key presses and characters, read on <paramref name="layout"/> (How is left empty): whether one presses
    /// Enter or a key or character a terminal takes as Enter (Ctrl+M, Ctrl+J, CR, LF), types a line break, can paste
    /// (<see cref="ClipboardOrigin.PasteTrigger"/>), or types a character by its code with Alt and the numeric keypad.
    /// </summary>
    public static InputPreview Render(IReadOnlyList<InputEvent> events, KeyboardLayout layout)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(layout);

        var shown = new StringBuilder();
        var down = new List<(ushort Key, bool AltGr)>();
        string? lastKey = null;
        var repeat = 0;
        ushort tapCandidate = 0;
        char? highSurrogate = null;
        bool enter = false, lineBreak = false;

        void Flush()
        {
            if (lastKey != null)
            {
                shown.Append(ShownInput.KeyOpen).Append(lastKey);
                if (repeat > 1)
                {
                    shown.Append(' ').Append(ShownInput.Times).Append(repeat);
                }

                shown.Append(ShownInput.KeyClose);
            }

            lastKey = null;
            repeat = 0;
        }

        void Key(KeyModifiers held, string name)
        {
            var token = Modifiers(held) + name;
            if (token == lastKey)
            {
                repeat++;
                return;
            }

            Flush();
            lastKey = token;
            repeat = 1;
        }

        void Text(string text)
        {
            Flush();
            shown.Append(ShownInput.Escape(text));
        }

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
                    Key(Held(down, out _), KeyTable.NameOf(e.Code) ?? KeyNames.Of(e.Code));
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
            var ctrl = (held & KeyModifiers.Control) != 0;
            if (e.Kind == InputEventKind.Unicode)
            {
                var c = (char)e.Code;
                if (char.IsHighSurrogate(c))
                {
                    FlushSurrogate();
                    highSurrogate = c;
                    continue;
                }

                string character;
                if (char.IsLowSurrogate(c) && highSurrogate is { } high)
                {
                    character = new string([high, c]);
                    highSurrogate = null;
                }
                else
                {
                    FlushSurrogate();
                    character = c.ToString();
                }

                lineBreak |= c is '\n' or '\r';
                enter |= c is '\n' or '\r' || (ctrl && c is 'm' or 'M' or 'j' or 'J');
                if ((held & ~KeyModifiers.Shift) != KeyModifiers.None)
                {
                    Key(held, ShownInput.Escape(character));
                }
                else
                {
                    Text(character);
                }

                continue;
            }

            FlushSurrogate();
            var code = e.Code;
            enter |= code == VkReturn || (ctrl && code is 'M' or 'J');
            if ((held & ~KeyModifiers.Shift) == KeyModifiers.None &&
                layout.TextOf(code, (held & KeyModifiers.Shift) != 0, altGr) is { Length: > 0 } typed && !typed.Any(char.IsControl))
            {
                Text(typed);
            }
            else
            {
                Key(held, KeyTable.NameOf(code) ?? (code is >= 'A' and <= 'Z' or >= '0' and <= '9' ? ((char)code).ToString() : KeyNames.Of(code)));
            }
        }

        FlushSurrogate();
        Flush();
        return new InputPreview(shown.ToString(), string.Empty, enter, lineBreak)
        {
            Pastes = ClipboardOrigin.PasteTrigger(events) != null,
            NotAllowed = HostInputRules.Refusal(events, layout),
            Layout = layout.Handle,
            CapsLock = layout.CapsLock
        };

        // A high surrogate no low one followed is no character: it is sent as its code unit, and shown so
        void FlushSurrogate()
        {
            if (highSurrogate is { } unpaired)
            {
                Text(unpaired.ToString());
                highSurrogate = null;
            }
        }
    }

    /// <summary>
    /// The modifiers <paramref name="down"/> holds; the left Ctrl and right Alt a parser pressed as AltGr for a character hold
    /// nothing, and set <paramref name="altGr"/>.
    /// </summary>
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

    private static string Modifiers(KeyModifiers held) =>
        ((held & KeyModifiers.Control) != 0 ? "Ctrl+" : string.Empty) +
        ((held & KeyModifiers.Alt) != 0 ? "Alt+" : string.Empty) +
        ((held & KeyModifiers.Shift) != 0 ? "Shift+" : string.Empty) +
        ((held & KeyModifiers.Windows) != 0 ? "Win+" : string.Empty);
}
