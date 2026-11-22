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
