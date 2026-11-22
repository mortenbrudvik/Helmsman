namespace WinDesktopMcp.Tests.Unit.Services;

/// <summary>
/// The mouse half of the allowlist for a host window the user confirmed (allowHostTarget): a primary click, a pointer move
/// and a wheel scroll, without modifier keys, go there; any other button is refused, a right or middle click as a paste
/// (PasteNotAllowed) and every other one (x1, x2) as outside the allowlist (InvalidArguments). Checked inside the input gate
/// right before the batch, so a caller that skips the tool's check before the question still sends nothing. Nothing
/// reaches the desktop: the native input layer is <see cref="FakeInputWrapper"/>.
/// </summary>
public class HostMouseAllowlistTests
{
    private static readonly nint Host = 0x5150;

    private readonly FakeInputWrapper _input = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard();

    private InputSimulationService CreateSut(bool buttonsSwapped = false)
    {
        var systemInfo = new Mock<INativeSystemInfoWrapper>();
        systemInfo.Setup(s => s.AreMouseButtonsSwapped()).Returns(buttonsSwapped);
        return new(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object, new InputSession(_input), Permissive.PasteGuard,
            Permissive.Takeover, systemInfo: systemInfo.Object, keyboardLayout: new FakeKeyboardLayoutWrapper());
    }

    private static InputTarget Confirmed => InputTarget.Window(Host, allowHostTarget: true) with
    {
        ConfirmedHost = new HostTargetApproval(Host, 5150, FakeKeyboardLayoutWrapper.Us)
    };

    [Theory]
    [InlineData(MouseButton.X1, 1)]
    [InlineData(MouseButton.X2, 1)]
    [InlineData(MouseButton.X1, 3)]
    public void MouseClick_OfAnExtraButton_OnAConfirmedHost_IsRefusedAtSendTime(MouseButton button, int clicks)
    {
        var result = CreateSut().MouseClick(400, 300, button, clicks, [], Confirmed);

        result.ErrorCode.Should().Be(InputErrorCode.InvalidArguments, result.Error);
        result.Error.Should().Contain("primary click").And.Contain("Nothing was sent");
        _input.Batches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(MouseButton.X1)]
    [InlineData(MouseButton.X2)]
    public void MouseClick_OfAnExtraButton_ToAnOrdinaryTarget_IsSent(MouseButton button)
    {
        var result = CreateSut().MouseClick(400, 300, button, 1, [], InputTarget.Window(Host));

        result.Success.Should().BeTrue(result.Error);
        _input.Batches.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MouseClick_Primary_OnAConfirmedHost_IsSent(int clicks)
    {
        var result = CreateSut().MouseClick(400, 300, MouseButton.Primary, clicks, [], Confirmed);

        result.Success.Should().BeTrue(result.Error);
    }

    [Fact]
    public void MouseClick_Secondary_WithSwappedButtons_OnAConfirmedHost_IsRefusedAsAPaste()
    {
        // With swapped buttons the secondary click goes out as the left button's events: the rules read the buttons as the
        // user knows them, and the user's secondary button pastes in a terminal
        var result = CreateSut(buttonsSwapped: true).MouseClick(400, 300, MouseButton.Secondary, 1, [], Confirmed);

        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed, result.Error);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public void MouseClick_Primary_WithSwappedButtons_OnAConfirmedHost_IsSent()
    {
        // ... and the primary click, which goes out as the right button's events, is a primary click
        var result = CreateSut(buttonsSwapped: true).MouseClick(400, 300, MouseButton.Primary, 1, [], Confirmed);

        result.Success.Should().BeTrue(result.Error);
        _input.Batches.SelectMany(b => b).Should().Contain(e => e.Kind == InputEventKind.Mouse && e.Flags == RightDown);
    }

    // ---- The rule itself (HostInputRules.MouseRefusal), as the send-time check reads a batch ----

    private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010, MiddleDown = 0x0020,
        MiddleUp = 0x0040, XDown = 0x0080, XUp = 0x0100;

    private static readonly InputEvent Move = InputEvent.MouseMoveAbsolute(32768, 32768);

    public static TheoryData<string, InputEvent[]> AllowedBatches() => new()
    {
        { "a primary click", [Move, InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp)] },
        { "a triple click", [Move, .. Enumerable.Repeat<InputEvent[]>([InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp)], 3).SelectMany(e => e)] },
        { "a move", [Move] },
        { "a relative move", [InputEvent.Mouse(0x0001)] },
        { "a vertical scroll", [Move, InputEvent.Wheel(-120), InputEvent.Wheel(-120)] },
        { "a horizontal scroll", [Move, InputEvent.HorizontalWheel(120)] },
        { "nothing", [] }
    };

    [Theory]
    [MemberData(nameof(AllowedBatches))]
    public void TheRule_AllowsAPrimaryClick_AMove_AndAWheelScroll(string what, InputEvent[] batch)
    {
        HostInputRules.MouseRefusal(batch).Should().BeNull(what);
    }

    public static TheoryData<string, InputEvent[], string> RefusedBatches() => new()
    {
        { "an x1 click", [Move, InputEvent.Mouse(XDown, 1), InputEvent.Mouse(XUp, 1)], "x1 mouse button going down" },
        { "an x2 click", [Move, InputEvent.Mouse(XDown, 2), InputEvent.Mouse(XUp, 2)], "x2 mouse button going down" },
        { "an x1 press alone", [InputEvent.Mouse(XDown, 1)], "x1 mouse button going down" },
        { "an x2 release alone", [Move, InputEvent.Mouse(XUp, 2)], "x2 mouse button going up" },
        { "an x2 press after a primary click", [Move, InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp), InputEvent.Mouse(XDown, 2)], "x2" },
        { "a secondary release alone", [InputEvent.Mouse(RightUp)], "secondary mouse button going up" },
        { "a secondary press", [InputEvent.Mouse(RightDown)], "secondary mouse button going down" },
        { "a middle press and release", [InputEvent.Mouse(MiddleDown), InputEvent.Mouse(MiddleUp)], "middle mouse button going down" },
        { "a primary press with an X button's data", [InputEvent.Mouse(LeftDown, 1)], "0x0002" },
        { "a move that also presses", [InputEvent.Mouse(0x0001 | 0x8000 | LeftDown)], "0x8003" },
        { "a press and a release in one event", [InputEvent.Mouse(LeftDown | LeftUp)], "0x0006" }
    };

    [Theory]
    [MemberData(nameof(RefusedBatches))]
    public void TheRule_RefusesEveryOtherMouseEvent(string what, InputEvent[] batch, string named)
    {
        HostInputRules.MouseRefusal(batch).Should().NotBeNull(what).And.Contain(named);
    }

    [Fact]
    public void TheRule_LeavesKeyboardEventsToTheKeyRules()
    {
        HostInputRules.MouseRefusal([InputEvent.Key(0x5B, 0), InputEvent.Key(0x5B, 0x0002)]).Should().BeNull();
    }

    [Theory]
    [InlineData(MouseButton.Primary, null)]
    [InlineData(MouseButton.Secondary, "secondary")]
    [InlineData(MouseButton.Middle, "middle")]
    [InlineData(MouseButton.X1, "x1")]
    [InlineData(MouseButton.X2, "x2")]
    public void TheButtonRule_AllowsOnlyThePrimaryButton(MouseButton button, string? named)
    {
        var refused = ShownInput.HostButtonRefusal(button);

        if (named == null)
        {
            refused.Should().BeNull();
        }
        else
        {
            refused.Should().Contain(named);
        }
    }

    [Fact]
    public void EveryButtonTheToolAccepts_ButThePrimary_IsRefused()
    {
        // A button added to MouseButton later is refused for the host until someone decides otherwise
        Enum.GetValues<MouseButton>().Where(b => ShownInput.HostButtonRefusal(b) == null).Should().Equal(MouseButton.Primary);
    }

    [Fact]
    public void AMoveAndAScroll_OnAConfirmedHost_AreSent()
    {
        var sut = CreateSut();

        sut.MoveMouse(400, 300, Confirmed, false, CancellationToken.None).Success.Should().BeTrue();
        sut.Scroll(400, 300, 2, 0, [], Confirmed).Success.Should().BeTrue();
        sut.Scroll(400, 300, 0, -2, [], Confirmed).Success.Should().BeTrue();
    }
}
