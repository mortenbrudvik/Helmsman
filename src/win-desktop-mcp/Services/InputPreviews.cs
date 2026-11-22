using System.Text;
using WinDesktopMcp.Models;

namespace WinDesktopMcp.Services;

/// <summary>
/// Key and character events as the user's confirmation question shows them (<see cref="ShownInput"/>), before allowHostTarget
/// sends them to the agent's own host window: a character typed (as a character, or by its letter or digit key with no
/// modifier but Shift) is shown as that character, every other key press as ⟨Mods+NAME⟩, a modifier pressed and released
/// alone as ⟨NAME⟩, and a run of the same key as ⟨NAME⟩×n. The same events go out after the user's yes, so the question
/// shows what is sent; a test renders the events that were sent and compares.
/// </summary>
public static class InputPreviews
{
    // Win32 values, as NativeEnums has them (Services computes with no Native type)
    private const uint KeyUp = 0x0002;
    private const ushort VkReturn = 0x0D, VkSpace = 0x20, VkLeftControl = 0xA2, VkRightAlt = 0xA5;

    /// <summary>The events' key presses and characters, with whether one presses Enter or types a line break.</summary>
    public static (string Shown, bool PressesEnter, bool TypesLineBreak) Render(IReadOnlyList<InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

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
                shown.Append(lastKey);
                if (repeat > 1)
                {
                    shown.Append('×').Append(repeat);
                }
            }

            lastKey = null;
            repeat = 0;
        }

        void Key(KeyModifiers held, string name)
        {
            var token = $"{ShownInput.KeyOpen}{Modifiers(held)}{name}{ShownInput.KeyClose}";
            if (token == lastKey)
            {
                repeat++;
                return;
            }

            Flush();
            lastKey = token;
            repeat = 1;
        }

        void Character(Rune rune)
        {
            Flush();
            ShownInput.Append(shown, rune);
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
                    Key(Held(down), KeyTable.NameOf(e.Code) ?? KeyNames.Of(e.Code));
                }

                tapCandidate = 0;
                continue;
            }

            if (up)
            {
                continue;
            }

            tapCandidate = 0;
            var held = Held(down);
            if (e.Kind == InputEventKind.Unicode)
            {
                var c = (char)e.Code;
                Rune rune;
                if (char.IsHighSurrogate(c))
                {
                    highSurrogate = c;
                    continue;
                }

                if (char.IsLowSurrogate(c) && highSurrogate is { } high)
                {
                    rune = new Rune(high, c);
                }
                else if (!Rune.TryCreate(c, out rune))
                {
                    // A lone surrogate: its code unit
                    Flush();
                    ShownInput.AppendCodePoint(shown, c);
                    highSurrogate = null;
                    continue;
                }

                highSurrogate = null;
                lineBreak |= c is '\n' or '\r';
                if ((held & ~KeyModifiers.Shift) != KeyModifiers.None)
                {
                    var name = new StringBuilder();
                    ShownInput.Append(name, rune);
                    Key(held, name.ToString());
                }
                else
                {
                    Character(rune);
                }

                continue;
            }

            var code = e.Code;
            enter |= code == VkReturn;
            if (code is >= 'A' and <= 'Z' && (held & ~KeyModifiers.Shift) == KeyModifiers.None)
            {
                Character(new Rune(held == KeyModifiers.Shift ? (char)code : char.ToLowerInvariant((char)code)));
            }
            else if ((code is >= '0' and <= '9' || code == VkSpace) && held == KeyModifiers.None)
            {
                Character(new Rune((char)code));
            }
            else
            {
                Key(held, KeyTable.NameOf(code) ?? (code is >= 'A' and <= 'Z' or >= '0' and <= '9' ? ((char)code).ToString() : KeyNames.Of(code)));
            }
        }

        Flush();
        return (shown.ToString(), enter, lineBreak);
    }

    /// <summary>The modifiers <paramref name="down"/> holds; the left Ctrl and right Alt a parser pressed as AltGr for a character hold nothing.</summary>
    private static KeyModifiers Held(List<(ushort Key, bool AltGr)> down)
    {
        var altGr = down.Contains((VkLeftControl, true)) && down.Contains((VkRightAlt, true));
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
