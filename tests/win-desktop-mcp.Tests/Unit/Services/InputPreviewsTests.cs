namespace WinDesktopMcp.Tests.Unit.Services;

/// <summary>
/// How allowHostTarget's question renders key and character events (<see cref="InputPreviews"/>, <see cref="ShownInput"/>):
/// what counts as pressing Enter, what is typed by its code with Alt and the numeric keypad, and the repeat notation. Pure.
/// </summary>
public class InputPreviewsTests
{
    private static InputPreview Render(IReadOnlyList<InputEvent> events) =>
        InputPreviews.Render(events, KeyboardLayout.Invariant);

    private static InputEvent Down(ushort key) => InputEvent.Key(key, 0);

    private static InputEvent Up(ushort key) => InputEvent.Key(key, 0x0002);

    [Theory]
    [InlineData('\r')]
    [InlineData('\n')]
    public void TheControlCharactersOfEnter_TypedAsCharacters_PressEnter(char c)
    {
        Render([InputEvent.Unicode(c, 0), InputEvent.Unicode(c, 0x0002)]).PressesEnter.Should().BeTrue();
    }

    [Theory]
    [InlineData('m')]
    [InlineData('J')]
    public void CtrlMAndCtrlJ_AsCharactersWithCtrlHeld_PressEnter(char c)
    {
        Render([Down(0xA2), InputEvent.Unicode(c, 0), InputEvent.Unicode(c, 0x0002), Up(0xA2)]).PressesEnter.Should().BeTrue();
    }

    [Fact]
    public void CtrlMAsAKey_PressesEnter_AndOtherCtrlChordsDoNot()
    {
        Render(SendKeysParser.Parse("^m")).PressesEnter.Should().BeTrue();
        Render(SendKeysParser.Parse("^+j")).PressesEnter.Should().BeTrue();
        Render(SendKeysParser.Parse("^c")).PressesEnter.Should().BeFalse();
        Render(SendKeysParser.Parse("m")).PressesEnter.Should().BeFalse();
    }

    private static KeyboardLayout Layout(nint layout) =>
        KeyboardLayout.Of(new FakeKeyboardLayoutWrapper { Layout = layout }, 0, capsLock: false);

    private static string? HostRefusal(string keys, nint? layout = null)
    {
        var read = layout is { } given ? Layout(given) : KeyboardLayout.Invariant;
        return HostInputRules.Refusal(KeySpecParser.Parse(keys, read).Events, read);
    }

    [Theory]
    [InlineData("echo hi{ENTER}")]
    [InlineData("{TAB}+{TAB}{BACKSPACE 3}")]
    [InlineData("{UP}{DOWN}{LEFT}{RIGHT}{HOME}{END}{PGUP}{PGDN}{DELETE}{ESC}")]
    [InlineData("+{HOME}^{END}^+{LEFT}")] // with Shift, Ctrl or both
    [InlineData("^c^d^z^l^a^e^u^k^h^m^j")]
    [InlineData("Hello, wörld!")]
    public void HostInputRules_Allow_TextEnterTabBackspace_TheNavigationKeys_AndTheControlLetters(string keys)
    {
        HostRefusal(keys).Should().BeNull();
    }

    [Theory]
    [InlineData("{APPS}p", "APPS")] // the context menu, whose Paste puts the clipboard in
    [InlineData("+{F10}p", "F10")]
    [InlineData("{F10}", "F10")]
    [InlineData("{F5}", "F5")]
    [InlineData("{ALT}", "pressed alone")]
    [InlineData("% ", "Alt")] // Alt+Space, the window menu
    [InlineData("%e", "Alt")]
    [InlineData("^%q", "Ctrl+Alt")]
    [InlineData("^+t", "Ctrl+Shift")]
    [InlineData("^s", "Ctrl+S")]
    [InlineData("#d", "Win")]
    [InlineData("{NUMPAD1}", "NUMPAD1")]
    [InlineData("%{NUMPAD1}%{NUMPAD3}", "Alt")] // Alt with keypad digits types a character by its code
    [InlineData("{VOLUME_MUTE}", "VOLUME")]
    public void HostInputRules_Refuse_MenusFunctionKeys_OtherChords_AndUnnamedKeys(string keys, string named)
    {
        HostRefusal(keys).Should().Contain(named);
    }

    [Fact]
    public void HostInputRules_ACharacterTheLayoutTypesWithAltGr_IsText_ButAnExplicitCtrlAltChordIsNot()
    {
        // On a German keyboard @ is AltGr+Q: as a character it is text; ^%q types @ too, but is shown as a chord
        HostRefusal("user@example.de", FakeKeyboardLayoutWrapper.German).Should().BeNull();
        HostRefusal("^%q", FakeKeyboardLayoutWrapper.German).Should().Contain("Ctrl+Alt");
    }

    [Theory]
    [InlineData("a\u202Eb")]
    [InlineData("\u05D0;rm -r ~ #\u05D1")] // Hebrew: drawn right to left, the command could read as a comment
    [InlineData("\uFF52\uFF4D")] // fullwidth rm
    [InlineData("\U0001D42B")] // mathematical bold r
    public void HostInputRules_Refuse_TextTheQuestionCannotShowAsItself(string text)
    {
        HostInputRules.Refusal(text.Select(c => InputEvent.Unicode(c, 0)).ToList(), KeyboardLayout.Invariant).Should().Contain("U+");
    }

    [Fact]
    public void HostInputRules_RefuseAnUnpairedSurrogate()
    {
        HostInputRules.Refusal([InputEvent.Unicode('a', 0), InputEvent.Unicode('\uD800', 0), InputEvent.Unicode('b', 0)],
            KeyboardLayout.Invariant).Should().Contain("U+D800");
        HostInputRules.Refusal([InputEvent.Unicode('a', 0), InputEvent.Unicode('\uD800', 0)], KeyboardLayout.Invariant)
            .Should().Contain("U+D800");
    }

    [Theory]
    [InlineData("\u05D0", "⟨U+05D0⟩")]
    [InlineData("\u0627", "⟨U+0627⟩")] // Arabic
    [InlineData("\uFF21", "⟨U+FF21⟩")] // fullwidth A
    [InlineData("\uFB01", "⟨U+FB01⟩")] // the fi ligature
    [InlineData("\u0430", "\u0430")] // Cyrillic а is a letter of its own script: shown as it is, as the legend says
    public void Escape_RightToLeftLettersAndCompatibilityFormsOfAscii(string text, string shown)
    {
        ShownInput.Escape(text).Should().Be(shown);
    }

    [Fact]
    public void Render_AnUnpairedHighSurrogate_IsShownByItsCode_InTheMiddleAndAtTheEnd()
    {
        Render([InputEvent.Unicode('a', 0), InputEvent.Unicode('\uD800', 0), InputEvent.Unicode('b', 0)]).Shown.Should().Be("a⟨U+D800⟩b");
        Render([InputEvent.Unicode('a', 0), InputEvent.Unicode('\uD800', 0)]).Shown.Should().Be("a⟨U+D800⟩");
    }

    [Fact]
    public void TheAllowlistsCopiesOfWin32Values_AreNativeEnums()
    {
        // Services keeps its own copies (no Services -> Native dependency); both are internal, so they are read by reflection
        var assembly = typeof(HostInputRules).Assembly;
        var native = assembly.GetType("WinDesktopMcp.Native.NativeEnums")!;
        var copies = new Dictionary<string, string>
        {
            ["KeyUp"] = "KEYEVENTF_KEYUP", ["VkBack"] = "VK_BACK", ["VkTab"] = "VK_TAB", ["VkReturn"] = "VK_RETURN", ["VkEscape"] = "VK_ESCAPE",
            ["VkPrior"] = "VK_PRIOR", ["VkNext"] = "VK_NEXT", ["VkEnd"] = "VK_END", ["VkHome"] = "VK_HOME", ["VkLeft"] = "VK_LEFT",
            ["VkUp"] = "VK_UP", ["VkRight"] = "VK_RIGHT", ["VkDown"] = "VK_DOWN", ["VkDelete"] = "VK_DELETE",
            ["VkLeftControl"] = "VK_LCONTROL", ["VkRightAlt"] = "VK_RMENU"
        };
        const System.Reflection.BindingFlags Static = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public |
                                                      System.Reflection.BindingFlags.NonPublic;

        foreach (var (copy, nativeName) in copies)
        {
            var value = typeof(HostInputRules).GetField(copy, Static)!.GetValue(null)!;
            Convert.ToUInt32(value).Should().Be(Convert.ToUInt32(native.GetField(nativeName, Static)!.GetValue(null)!), copy);
        }
    }    [Fact]
    public void ARepeatCount_IsInsideTheKeysMarker()
    {
        Render(SendKeysParser.Parse("{ENTER}{ENTER}2")).Shown.Should().Be("⟨ENTER ×2⟩2");
    }

    [Fact]
    public void ALoneSurrogate_IsItsCodeUnit()
    {
        ShownInput.Escape("a\uD800b").Should().Be("a⟨U+D800⟩b");
    }
}
