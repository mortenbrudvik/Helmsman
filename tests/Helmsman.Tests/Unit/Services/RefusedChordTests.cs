namespace Helmsman.Tests.Unit.Services;

/// <summary>
/// Unit tests for the chords no key tool sends, with or without a target: Win+R (the Run dialog) and Win+X (the power-user
/// menu, with the terminal as administrator and Shut down), where whatever is typed next runs as a command. Every spelling
/// of the chord counts: the left or right Windows key, more modifiers, a capital, the key pressed while an earlier
/// {LWIN down} still holds Win, press_keys' names, and the letter of another layout that is on the R or X key. Pure:
/// nothing is sent.
/// </summary>
public class RefusedChordTests
{
    private static KeyboardLayout Layout(nint layout) =>
        KeyboardLayout.Of(new FakeKeyboardLayoutWrapper { Layout = layout }, 0, capsLock: false);

    private static string? FindInSendKeys(string keys, nint? layout = null) =>
        InputGuardOptions.FindRefusedChord(layout is { } given ? KeySpecParser.Parse(keys, Layout(given)).Events : SendKeysParser.Parse(keys));

    private static string? FindInPressKeys(KeyList keys) =>
        InputGuardOptions.FindRefusedChord(PressKeysParser.Parse(keys, 1, KeyboardLayout.Invariant).Events);

    [Fact]
    public void RefusedChords_AreWinRAndWinX()
    {
        InputGuardOptions.RefusedChords.Should().Equal("#r", "#x");
    }

    [Theory]
    [InlineData("#r", "#r")]
    [InlineData("#R", "#r")] // a capital is the key with Shift
    [InlineData("#x", "#x")]
    [InlineData("#X", "#x")]
    [InlineData("+#r", "#r")] // more modifiers: still the chord, as for the destructive chords
    [InlineData("^#r", "#r")]
    [InlineData("%#x", "#x")]
    [InlineData("^+%#x", "#x")]
    [InlineData("{LWIN down}r{LWIN up}", "#r")]
    [InlineData("{RWIN down}r{RWIN up}", "#r")] // the right Windows key
    [InlineData("{RWIN down}X{RWIN up}", "#x")]
    [InlineData("{LWIN down}{DOWN}{UP}r{LWIN up}", "#r")] // held across other keys, as across batches
    [InlineData("abc#r", "#r")] // anywhere in the keys, not only first
    [InlineData("#rnotepad{ENTER}", "#r")]
    public void FindRefusedChord_EverySendKeysSpelling_IsFound(string keys, string expected)
    {
        FindInSendKeys(keys).Should().Be(expected);
    }

    [Theory]
    [InlineData("#к", "#r")] // к is on the R key of a Russian keyboard: send_keys presses that key
    [InlineData("#ч", "#x")]
    [InlineData("+#К", "#r")]
    public void FindRefusedChord_TheLetterOnTheKeyInTheWindowsLayout_IsFound(string keys, string expected)
    {
        FindInSendKeys(keys, FakeKeyboardLayoutWrapper.Russian).Should().Be(expected);
    }

    [Fact]
    public void FindRefusedChord_TheVirtualKeyIsWhatCounts_WhateverItsScanCode()
    {
        // The server sends virtual keys (never KEYEVENTF_SCANCODE); the R key's event carries its scan code beside it
        var events = new List<InputEvent>
        {
            InputEvent.Key(0x5B, 0x0001, 0x5B), InputEvent.Key(0x52, 0, 0x13), InputEvent.Key(0x52, 0x0002, 0x13),
            InputEvent.Key(0x5B, 0x0003, 0x5B)
        };

        InputGuardOptions.FindRefusedChord(events).Should().Be("#r");
    }

    [Theory]
    [InlineData("win+r", "#r")]
    [InlineData("WIN+R", "#r")]
    [InlineData("rwin+r", "#r")]
    [InlineData("lwin+x", "#x")]
    [InlineData("win+shift+r", "#r")]
    [InlineData("ctrl+win+x", "#x")]
    [InlineData("tab win+x enter", "#x")]
    public void FindRefusedChord_EveryPressKeysSpelling_IsFound(string keys, string expected)
    {
        FindInPressKeys(KeyList.Of(keys)).Should().Be(expected);
    }

    [Fact]
    public void FindRefusedChord_PressKeysAsAnArray_IsFound()
    {
        FindInPressKeys(KeyList.Of(["win", "r"])).Should().Be("#r");
        FindInPressKeys(KeyList.Of(["win", "shift", "x"])).Should().Be("#x");
    }

    [Theory]
    [InlineData("#d")] // the desktop: allowed as the last key, as today
    [InlineData("#e")] // File Explorer
    [InlineData("#{UP}")] // maximize
    [InlineData("#l")] // lock: a destructive chord, refused only without windowHandle
    [InlineData("{WIN}r")] // the Windows key is released before r: Start menu, then a letter
    [InlineData("{#}r")] // the character #
    [InlineData("r")]
    [InlineData("^r")]
    [InlineData("%x")]
    [InlineData("+x")]
    public void FindRefusedChord_OtherKeys_FindNothing(string keys)
    {
        FindInSendKeys(keys).Should().BeNull();
    }

    [Fact]
    public void FindKeysAfterFocusChange_TheNeighbouringWindowsKeyChords_StayAllowedAsTheLastKey()
    {
        // Win+R and Win+X are refused outright; the other Windows-key chords keep their rule: the last key of a call
        foreach (var keys in new[] { "#d", "#e", "#{UP}", "#{DOWN}", "+#s" })
        {
            InputGuardOptions.Default.FindKeysAfterFocusChange(SendKeysParser.Parse(keys)).Should().BeNull(keys);
            InputGuardOptions.Default.FindKeysAfterFocusChange(SendKeysParser.Parse(keys + "a")).Should().NotBeNull(keys);
        }
    }
}
