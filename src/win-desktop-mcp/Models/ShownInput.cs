using System.Buffers;
using System.Globalization;
using System.Text;

namespace WinDesktopMcp.Models;

/// <summary>
/// Input as the user's confirmation question shows it before allowHostTarget sends it to the agent's own host window (see
/// Infrastructure.HostTargetConfirmation): every character, nothing hidden by rendering. A line break is ⏎ and a tab ⇥; a key
/// or chord is ⟨NAME⟩ (⟨ENTER⟩, ⟨Ctrl+C⟩), and a run of the same one ⟨NAME⟩×n. Every character that could hide or disguise
/// what is sent is its code point, ⟨U+XXXX⟩: control and format characters (bidirectional overrides and isolates, zero-width
/// characters), line and paragraph separators, spaces other than the plain space, private-use and unassigned code points,
/// a lone surrogate, and the marker characters themselves (⏎ ⇥ ⟨ ⟩ ⟦ ⟧), so a marker in the question is always one.
/// </summary>
public static class ShownInput
{
    public const char LineBreak = '⏎';
    public const char Tab = '⇥';
    public const char KeyOpen = '⟨';
    public const char KeyClose = '⟩';

    /// <summary>The marks around the shown input in the question.</summary>
    public const char Open = '⟦';

    /// <inheritdoc cref="Open"/>
    public const char Close = '⟧';

    /// <summary>What the markers mean, for the question.</summary>
    public const string Legend =
        "⏎ is a line break, ⇥ a tab, ⟨NAME⟩ a key or chord (×n: pressed n times), ⟨U+XXXX⟩ an invisible or look-alike character.";

    private static readonly HashSet<int> Markers = [LineBreak, Tab, KeyOpen, KeyClose, Open, Close];

    /// <summary><paramref name="text"/> as the question shows it, each character as <see cref="Append(StringBuilder, Rune)"/> shows it.</summary>
    public static string Escape(string? text)
    {
        var shown = new StringBuilder();
        var source = (text ?? string.Empty).AsSpan();
        while (!source.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(source, out var rune, out var consumed) != OperationStatus.Done)
            {
                // A lone surrogate, which is no character: its code unit
                AppendCodePoint(shown, source[0]);
                source = source[1..];
                continue;
            }

            Append(shown, rune);
            source = source[consumed..];
        }

        return shown.ToString();
    }

    /// <summary>Appends one typed character as the question shows it.</summary>
    public static void Append(StringBuilder shown, Rune rune)
    {
        ArgumentNullException.ThrowIfNull(shown);
        if (rune.Value == '\n')
        {
            shown.Append(LineBreak);
            return;
        }

        if (rune.Value == '\t')
        {
            shown.Append(Tab);
            return;
        }

        if (rune.Value != ' ' && (Markers.Contains(rune.Value) || IsHidden(Rune.GetUnicodeCategory(rune))))
        {
            AppendCodePoint(shown, rune.Value);
            return;
        }

        shown.Append(rune.ToString());
    }

    /// <summary>Appends ⟨U+XXXX⟩.</summary>
    public static void AppendCodePoint(StringBuilder shown, int codePoint)
    {
        ArgumentNullException.ThrowIfNull(shown);
        shown.Append(KeyOpen).Append("U+").Append(codePoint.ToString("X4", CultureInfo.InvariantCulture)).Append(KeyClose);
    }

    private static bool IsHidden(UnicodeCategory category) => category is UnicodeCategory.Control or UnicodeCategory.Format or
        UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator or
        UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;
}

/// <summary>
/// What a call would send to the agent's host window, as its confirmation question shows it (<see cref="ShownInput"/>).
/// </summary>
/// <param name="Shown">Every character and key the call sends, in order.</param>
/// <param name="How">How it is sent, for the question: "type these 12 characters", "press these keys".</param>
/// <param name="PressesEnter">True when a key press of the input is Enter (with any modifiers), which runs a command in a terminal.</param>
/// <param name="TypesLineBreak">True when the input types or pastes a line break character, which a terminal may run too.</param>
public sealed record InputPreview(string Shown, string How, bool PressesEnter = false, bool TypesLineBreak = false)
{
    /// <summary>A text set or typed as characters (element_action's value, file_dialog's path): every line break typed.</summary>
    public static InputPreview OfText(string text, string how) =>
        new(ShownInput.Escape(text), how, TypesLineBreak: text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal));
}
