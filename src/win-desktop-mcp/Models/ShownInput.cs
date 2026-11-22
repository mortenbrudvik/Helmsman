using System.Buffers;
using System.Globalization;
using System.Text;

namespace WinDesktopMcp.Models;

/// <summary>
/// Input as the user's confirmation question shows it before allowHostTarget sends it to the agent's own host window (see
/// Infrastructure.HostTargetConfirmation): every character, nothing hidden by rendering. A line break is ⏎ and a tab ⇥; a key
/// or chord is ⟨NAME⟩ (⟨ENTER⟩, ⟨Ctrl+C⟩), and a run of the same one ⟨NAME ×n⟩, the count inside the marker. A character is
/// shown as itself only when it is visibly printable (<see cref="IsShownAsItself"/>): a letter, a digit or other number,
/// punctuation or a symbol, or the plain space, that is not one of the exceptions below. Every other code point is shown as
/// ⟨U+XXXX⟩: control and format characters (bidirectional overrides and isolates, zero-width characters), combining and
/// enclosing marks and variation selectors (which change or hide the character before them), line and paragraph separators,
/// spaces other than the plain space, private-use, unassigned and unpaired surrogate code points; the letters and symbols
/// that draw nothing (the Hangul fillers U+115F, U+1160, U+3164, U+FFA0 and the blank braille pattern U+2800); every
/// character of a right-to-left script (Hebrew, Arabic, Syriac, Thaana, N'Ko and the others of U+0590-U+08FF, their
/// presentation forms U+FB1D-U+FDFF and U+FE70-U+FEFF, and U+10800-U+10FFF, U+1E800-U+1EFFF), which a client drawing the
/// line right to left would show with its neighbours reordered; a compatibility form of ASCII text (its NFKC form is ASCII:
/// fullwidth and mathematical letters, ligatures such as ﬁ); and the marker characters themselves (⏎ ⇥ ⟨ ⟩ ⟦ ⟧ ×), so a
/// marker in the question is always one. Letters of other scripts that merely resemble Latin ones (Cyrillic а) are shown as
/// they are.
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
        "⏎ is a line break (LF), ⟨CR⟩ a carriage return, ⇥ a tab, ⟨NAME⟩ a key or chord, ⟨NAME ×n⟩ the same key pressed n " +
        "times, and ⟨U+XXXX⟩ a character " +
        "shown by its code: one that is not a visible letter, digit, punctuation mark, symbol or plain space, a right-to-left " +
        "letter, or a fullwidth or other compatibility form of a plain ASCII character. Letters of other scripts that resemble " +
        "Latin ones are shown as they are.";

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
                // An unpaired surrogate, which is no character: its code unit
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

        // A carriage return is shown by name: CRLF as ⟨CR⟩⏎, so each character a terminal may take as Enter shows
        if (rune.Value == '\r')
        {
            shown.Append(KeyOpen).Append("CR").Append(KeyClose);
            return;
        }

        if (!IsShownAsItself(rune))
        {
            AppendCodePoint(shown, rune.Value);
            return;
        }

        shown.Append(rune.ToString());
    }

    /// <summary>True when the question shows <paramref name="rune"/> as itself, not as its code (see the class remarks).</summary>
    public static bool IsShownAsItself(Rune rune)
    {
        var value = rune.Value;
        if (value == ' ')
        {
            return true;
        }

        return !Markers.Contains(value) && !Blanks.Contains(value) && !IsRightToLeft(value) && IsVisible(Rune.GetUnicodeCategory(rune)) &&
               !IsCompatibilityAscii(rune);
    }

    /// <summary>
    /// The first character of <paramref name="text"/> the agent's host may not get as text: one the question does not show as
    /// itself, other than a line break or a tab (shown as ⏎ and ⇥), as ⟨U+XXXX⟩; null when there is none.
    /// </summary>
    public static string? FirstUnshown(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var source = text.AsSpan();
        while (!source.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(source, out var rune, out var consumed) != OperationStatus.Done)
            {
                return CodePoint(source[0]);
            }

            if (rune.Value is not ('\n' or '\r' or '\t') && !IsShownAsItself(rune))
            {
                return CodePoint(rune.Value);
            }

            source = source[consumed..];
        }

        return null;
    }

    /// <summary>
    /// The refusal of input outside the allowlist for the agent's host (Services.HostInputRules), for the error
    /// (InvalidArguments): <paramref name="what"/> is the press or character, <paramref name="window"/> the host window.
    /// </summary>
    public static string HostAllowlistRefusal(string what, string window) =>
        $"The input contains {what}, which never goes to {window}, the agent's own host: the question to the user can only " +
        "promise what text, Enter, Tab, Backspace, the arrows, Home, End, Page Up and Page Down (also with Shift or Ctrl), " +
        "Delete, Esc and Ctrl with A, C, D, J, M, U or Z do there. A key it can only name (a menu key, a function key, an Alt, " +
        "Win or other chord, a modifier held around mouse input) could do anything, such as paste through a menu or open a link.";

    /// <summary>⟨U+XXXX⟩.</summary>
    public static string CodePoint(int codePoint)
    {
        var shown = new StringBuilder();
        AppendCodePoint(shown, codePoint);
        return shown.ToString();
    }

    /// <summary>Appends ⟨U+XXXX⟩.</summary>
    public static void AppendCodePoint(StringBuilder shown, int codePoint)
    {
        ArgumentNullException.ThrowIfNull(shown);
        shown.Append(KeyOpen).Append("U+").Append(codePoint.ToString("X4", CultureInfo.InvariantCulture)).Append(KeyClose);
    }

    /// <summary>The blocks of the right-to-left scripts and their presentation forms (bidi classes R and AL, and their digits).</summary>
    private static bool IsRightToLeft(int value) => value is
        (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF) or
        (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF);

    /// <summary>A character outside ASCII whose compatibility form (NFKC) is ASCII text: it looks like that text.</summary>
    private static bool IsCompatibilityAscii(Rune rune)
    {
        if (rune.IsAscii)
        {
            return false;
        }

        var text = rune.ToString();
        var normalized = text.Normalize(NormalizationForm.FormKC);
        return normalized != text && normalized.All(char.IsAscii);
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
/// <param name="PressesEnter">
/// True when the input presses Enter, or a key or character a terminal takes as Enter (Ctrl+M, Ctrl+J, a typed CR or LF).
/// </param>
/// <param name="TypesLineBreak">True when the input types or pastes a line break character, which a terminal may run too.</param>
public sealed record InputPreview(string Shown, string How, bool PressesEnter = false, bool TypesLineBreak = false)
{
    /// <summary>
    /// True when the input can paste (Ctrl+V with any other modifiers, Shift+Insert, Win+V, the SYN character), or pastes by
    /// its method: what it pastes cannot be shown, so the agent's host never gets it.
    /// </summary>
    public bool Pastes { get; init; }

    /// <summary>
    /// Why the agent's host may not get this input, by the allowlist for a confirmed host window (Services.HostInputRules): the
    /// first key, chord or character outside it; null when all of it is allowed.
    /// </summary>
    public string? NotAllowed { get; init; }

    /// <summary>
    /// The keyboard layout (HKL) the keys were read on; null when the input reads no layout (characters sent as themselves),
    /// so no layout is recorded in the approval and none is checked when it is sent.
    /// </summary>
    public nint? Layout { get; init; }

    /// <summary>Whether Caps Lock was on when the keys were read on <see cref="Layout"/>; it changes what a letter key types.</summary>
    public bool CapsLock { get; init; }

    /// <summary>A text set or typed as characters (element_action's value, file_dialog's path): every line break typed.</summary>
    public static InputPreview OfText(string text, string how)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lineBreak = text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal);
        return new(ShownInput.Escape(text), how, PressesEnter: lineBreak, TypesLineBreak: lineBreak)
        {
            Pastes = text.Contains('\u0016', StringComparison.Ordinal),
            NotAllowed = ShownInput.FirstUnshown(text) is { } character
                ? $"the character {character}, which the question cannot show as itself"
                : null
        };
    }
}
