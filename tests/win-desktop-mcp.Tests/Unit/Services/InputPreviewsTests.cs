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

    [Theory]
    [InlineData("%{NUMPAD1}%{NUMPAD3}")]
    [InlineData("{ALT down}{NUMPAD0}{NUMPAD9}{ALT up}")]
    public void AltWithANumericKeypadDigit_IsFlagged(string keys)
    {
        Render(SendKeysParser.Parse(keys)).TypesByCode.Should().BeTrue();
    }

    [Fact]
    public void ANumericKeypadDigitWithoutAlt_IsNotFlagged()
    {
        Render(SendKeysParser.Parse("{NUMPAD1}")).TypesByCode.Should().BeFalse();
    }

    [Fact]
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
