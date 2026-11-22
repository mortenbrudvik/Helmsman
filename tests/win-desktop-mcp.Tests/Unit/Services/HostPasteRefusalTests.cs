namespace WinDesktopMcp.Tests.Unit.Services;

/// <summary>
/// The send-time backstop of allowHostTarget's question: inside the input gate, right before every batch, input that can
/// paste is refused for a call whose target carries the user's approval of a host window (InputTarget.ConfirmedHost), and so
/// is a drag, whatever set_clipboard put on the clipboard. The question could not show what is pasted or dropped, and the
/// agent could change the clipboard after the user's yes. The paste guard here allows every paste, so only the backstop
/// refuses. Nothing reaches the desktop: the native input layer is <see cref="FakeInputWrapper"/>.
/// </summary>
public class HostPasteRefusalTests
{
    private static readonly nint Host = 0x5150;

    private readonly FakeInputWrapper _input = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard();

    private InputSimulationService CreateSut() =>
        new(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object, new InputSession(_input), Permissive.PasteGuard,
            Permissive.Takeover, keyboardLayout: new FakeKeyboardLayoutWrapper());

    private static InputTarget Confirmed => InputTarget.Window(Host, allowHostTarget: true) with
    {
        ConfirmedHost = new HostTargetApproval(Host, 5150, FakeKeyboardLayoutWrapper.Us)
    };

    [Theory]
    [InlineData("^v")]
    [InlineData("+{INSERT}")]
    [InlineData("^+v")]
    [InlineData("#v")]
    [InlineData("abc{CTRL down}v{CTRL up}")]
    public async Task SendKeys_ThatPaste_ToAConfirmedHost_AreRefusedAtSendTime(string keys)
    {
        var result = await CreateSut().SendKeysAsync(keys, Confirmed);

        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        result.Error.Should().Contain("Nothing").And.Contain("never");
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task SendKeys_ThatPaste_ToAnOrdinaryTarget_FollowTheClipboardRuleAsBefore()
    {
        var result = await CreateSut().SendKeysAsync("^v", InputTarget.Window(Host));

        result.Success.Should().BeTrue(result.Error);
    }

    [Theory]
    [InlineData(MouseButton.Secondary)]
    [InlineData(MouseButton.Middle)]
    public void MouseClick_RightOrMiddle_OnAConfirmedHost_IsRefusedAtSendTime(MouseButton button)
    {
        var result = CreateSut().MouseClick(400, 300, button, 1, [], Confirmed);

        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        _input.Batches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(MouseModifier.Ctrl)]
    [InlineData(MouseModifier.Shift)]
    [InlineData(MouseModifier.Alt)]
    [InlineData(MouseModifier.Win)]
    public void MouseInputWithModifiers_ToAConfirmedHost_IsRefusedAtSendTime(MouseModifier modifier)
    {
        var sut = CreateSut();

        sut.MouseClick(400, 300, MouseButton.Primary, 1, [modifier], Confirmed).ErrorCode.Should().Be(InputErrorCode.InvalidArguments);
        sut.Scroll(400, 300, 1, 0, [modifier], Confirmed).ErrorCode.Should().Be(InputErrorCode.InvalidArguments);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public void AModifierTheServerStillHolds_IsReleasedBeforeAClickToAConfirmedHost_SoTheClickIsNoChord()
    {
        // A modifier the server pressed and holds is let go of before the next call's input: the click then goes out alone
        var session = new InputSession(_input);
        session.Send([InputEvent.Key(0xA2, 0)]);
        var before = _input.Batches.Count;
        var sut = new InputSimulationService(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object, session,
            Permissive.PasteGuard, Permissive.Takeover, keyboardLayout: new FakeKeyboardLayoutWrapper());

        sut.MouseClick(400, 300, MouseButton.Primary, 1, [], Confirmed);

        var after = _input.Batches.Skip(before).SelectMany(b => b).ToList();
        var press = after.FindIndex(e => e.Kind == InputEventKind.Mouse && (e.Flags & 0x0002) != 0);
        var ctrlUp = after.FindIndex(e => e.Kind == InputEventKind.VirtualKey && e.Code == 0xA2 && (e.Flags & 0x0002) != 0);
        if (press >= 0)
        {
            ctrlUp.Should().BeInRange(0, press - 1, "Ctrl is up before the button goes down");
        }
    }
    [Fact]
    public void AClick_ToAConfirmedHost_WhileTheSessionReportsAServerHeldModifier_IsRefused_AndNothingIsSent()
    {
        // A session whose release reports success but that still lists Ctrl as held by the server: the click would be a chord
        var session = new ServerHoldsCtrl(new InputSession(_input));
        var sut = new InputSimulationService(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object, session,
            Permissive.PasteGuard, Permissive.Takeover, keyboardLayout: new FakeKeyboardLayoutWrapper());

        var result = sut.MouseClick(400, 300, MouseButton.Primary, 1, [], Confirmed);

        result.ErrorCode.Should().Be(InputErrorCode.InvalidArguments, result.Error);
        _input.Batches.Should().BeEmpty("no SendInput call reached the input wrapper");
    }

    /// <summary>An input session that reports the left Ctrl as held by the server, whatever its release did; all else is the real session's.</summary>
    private sealed class ServerHoldsCtrl(InputSession inner) : IInputSession
    {
        public IReadOnlyList<HeldInput> ServerHeld => [HeldInput.Modifier(0xA2)];
        public bool IsPaused => inner.IsPaused;
        public InputPauseState PauseState => inner.PauseState;
        public CancellationToken PauseToken => inner.PauseToken;
        public string? PauseChord => inner.PauseChord;
        public bool Pause(string source) => inner.Pause(source);
        public bool Resume(string source, long? pauseNumber = null) => inner.Resume(source, pauseNumber);
        public void TogglePauseByHotkey() => inner.TogglePauseByHotkey();
        public void ShareDesktopPause(IDesktopPauseFlag flag) => inner.ShareDesktopPause(flag);
        public bool SharesDesktopPause => inner.SharesDesktopPause;
        public bool IsSharedPauseUnreadable => inner.IsSharedPauseUnreadable;
        public bool IsPauseChordHeld(IReadOnlyList<HeldInput> modifiersDown) => inner.IsPauseChordHeld(modifiersDown);
        public string? FindPauseChord(IReadOnlyList<InputEvent> events) => inner.FindPauseChord(events);
        public InputSendOutcome Send(IReadOnlyList<InputEvent> events) => inner.Send(events);
        public bool? SetCursorPos(int x, int y) => inner.SetCursorPos(x, y);
        public IReadOnlyList<HeldInput> ReadModifiersDown() => inner.ReadModifiersDown();
        public InputReleaseOutcome ReleaseHeldInput(string reason) => inner.ReleaseHeldInput(reason);
        public InputReleaseOutcome ReleaseModifiers(IReadOnlyList<HeldInput> keys, string reason) => inner.ReleaseModifiers(keys, reason);
        public InputReleaseRecord? LastRelease => inner.LastRelease;
        public IDisposable? TryOpenResumeQuestion() => inner.TryOpenResumeQuestion();
    }

    [Fact]
    public async Task TypeText_TheSynCharacter_ToAConfirmedHost_IsRefusedAtSendTime()
    {
        // Characters sent as themselves read no layout: the question's approval says so (0)
        var confirmed = Confirmed with { ConfirmedHost = new HostTargetApproval(Host, 5150) };

        var result = await CreateSut().TypeTextAsync("a\u0016b", 0, confirmed, false, TextControlChars.Unicode, TextInputMethod.Unicode);

        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task Drag_WithAConfirmedHost_IsRefused_AndNothingIsPressed()
    {
        var result = await CreateSut().DragAsync(new DragRequest(400, 300, 420, 300, MouseButton.Primary, [], 1, 0, 0),
            InputTarget.Window(0x200), Confirmed, false, CancellationToken.None);

        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        result.Pressed.Should().BeFalse();
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task ServerChosenKeys_ToAConfirmedHostWithNoLayoutRecorded_AreSent_OnAnyLayout()
    {
        // element_action's mode type sends ^a or ^{END}, then the text as characters: its approval records no layout, and the
        // host's own layout (Russian here) must not make the keys a mismatch
        var layouts = new FakeKeyboardLayoutWrapper { Layout = FakeKeyboardLayoutWrapper.Russian };
        var sut = new InputSimulationService(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object, new InputSession(_input),
            Permissive.PasteGuard, Permissive.Takeover, keyboardLayout: layouts);
        var noLayout = Confirmed with { ConfirmedHost = new HostTargetApproval(Host, 5150) };

        (await sut.SendKeysAsync("^a", noLayout)).Success.Should().BeTrue();
        (await sut.SendKeysAsync("^{END}", noLayout)).Success.Should().BeTrue();
        (await sut.TypeTextAsync("да", 0, noLayout, false, TextControlChars.Unicode, TextInputMethod.Unicode)).Success.Should().BeTrue();
    }

    [Fact]
    public async Task SendKeys_TheLayoutSwitchingBetweenBatches_StopsTheCallBeforeTheNextBatch()
    {
        // Another call, or the user, switches the input language after the first batch (each key is a batch with delayMs)
        var layouts = new FakeKeyboardLayoutWrapper { Layout = FakeKeyboardLayoutWrapper.Us };
        _input.BeforeSend = _ => layouts.Layout = FakeKeyboardLayoutWrapper.Russian;
        var sut = new InputSimulationService(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object, new InputSession(_input),
            Permissive.PasteGuard, Permissive.Takeover, keyboardLayout: layouts);

        var result = await sut.SendKeysAsync("abc", Confirmed, delayMs: 1);

        result.ErrorCode.Should().Be(InputErrorCode.HandleMismatch);
        result.KeysSent.Should().Be(1);
    }

    [Theory]
    [InlineData("{APPS}")]
    [InlineData("+{F10}")]
    [InlineData("%e")]
    public async Task SendKeys_OutsideTheAllowlist_ToAConfirmedHost_AreRefusedAtSendTime(string keys)
    {
        var result = await CreateSut().SendKeysAsync(keys, Confirmed);

        result.ErrorCode.Should().Be(InputErrorCode.InvalidArguments);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task SendKeys_ToAConfirmedHostWhoseLayoutIsNotTheOneShown_IsRefused()
    {
        var approvedOnFrench = Confirmed with { ConfirmedHost = new HostTargetApproval(Host, 5150, FakeKeyboardLayoutWrapper.French) };

        var result = await CreateSut().SendKeysAsync("y", approvedOnFrench);

        result.ErrorCode.Should().Be(InputErrorCode.HandleMismatch);
        _input.Batches.Should().BeEmpty();
    }
}
