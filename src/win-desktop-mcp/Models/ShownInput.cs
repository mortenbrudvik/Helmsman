using System.Buffers;
using System.Globalization;
using System.Text;

namespace WinDesktopMcp.Models;

/// <summary>
/// Input as the user's confirmation question shows it before allowHostTarget sends it to the agent's own host window (see
/// Infrastructure.HostTargetConfirmation): every character, nothing hidden by rendering. A line break is ⏎ and a tab ⇥; a key
/// or chord is ⟨NAME⟩ (⟨ENTER⟩, ⟨Ctrl+C⟩), and a run of the same one ⟨NAME ×n⟩, the count inside the marker. A character is
/// shown as itself only when it is visibly printable: a letter, a digit or other number, punctuation or a symbol, or the plain
/// space. Every other code point is shown as ⟨U+XXXX⟩: control and format characters (bidirectional overrides and isolates,
/// zero-width characters), combining and enclosing marks and variation selectors (which change or hide the character before
/// them), line and paragraph separators, spaces other than the plain space, private-use, unassigned and surrogate code
/// points; the letters and symbols that draw nothing (the Hangul fillers U+115F, U+1160, U+3164, U+FFA0 and the blank
/// braille pattern U+2800); and the marker characters themselves (⏎ ⇥ ⟨ ⟩ ⟦ ⟧ ×), so a marker in the question is always one.
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
        "⏎ is a line break, ⇥ a tab, ⟨NAME⟩ a key or chord, ⟨NAME ×n⟩ the same key pressed n times, and ⟨U+XXXX⟩ any character " +
        "that is not a visible letter, digit, punctuation mark, symbol or plain space (an invisible, combining or look-alike one).";

    /// <summary>The sign of a repeat count, inside a key marker: ⟨DOWN ×3⟩.</summary>
    public const char Times = '×';

    private static readonly HashSet<int> Markers = [LineBreak, Tab, KeyOpen, KeyClose, Open, Close, Times];

    /// <summary>Letters and symbols that draw nothing: the Hangul fillers and the blank braille pattern.</summary>
    private static readonly HashSet<int> Blanks = [0x115F, 0x1160, 0x3164, 0xFFA0, 0x2800];

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

        if (rune.Value != ' ' && (Markers.Contains(rune.Value) || Blanks.Contains(rune.Value) || !IsVisible(Rune.GetUnicodeCategory(rune))))
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

    /// <summary>The categories of visibly printable characters: letters, numbers, punctuation and symbols. Everything else is escaped.</summary>
    private static bool IsVisible(UnicodeCategory category) => category is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
        UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
        UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or
        UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or
        UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or
        UnicodeCategory.OtherPunctuation or
        UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;
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
    /// <summary>
    /// True when the input can paste (Ctrl+V with any other modifiers, Shift+Insert, Win+V, the SYN character), or pastes by
    /// its method: what it pastes cannot be shown, so the agent's host never gets it.
    /// </summary>
    public bool Pastes { get; init; }

    /// <summary>
    /// True when the input presses a numeric keypad digit with Alt held, which types a character by its code: the agent's host
    /// never gets it, since the character it makes is not what the question would show.
    /// </summary>
    public bool TypesByCode { get; init; }

    /// <summary>The keyboard layout (HKL) the keys were read on; 0 for the invariant layout (characters sent as themselves).</summary>
    public nint Layout { get; init; }

    /// <summary>A text set or typed as characters (element_action's value, file_dialog's path): every line break typed.</summary>
    public static InputPreview OfText(string text, string how)
    {
        var lineBreak = text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal);
        return new(ShownInput.Escape(text), how, PressesEnter: lineBreak, TypesLineBreak: lineBreak)
        {
            Pastes = text.Contains('\u0016', StringComparison.Ordinal)
        };
    }
}
