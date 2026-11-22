namespace Helmsman.Tests.Unit.Services;

/// <summary>
/// The input service tells the capture freshness store (warning CaptureUnchangedAfterInput) which top-level window received
/// input SendInput inserted, and whether it only moved the pointer; input that was refused or not inserted is not told. The
/// native input wrapper is mocked, so no real input reaches the desktop.
/// </summary>
public class InputSimulationServiceFreshnessTests
{
    private static readonly nint Receiving = new(0x5150);

    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();
    private readonly Mock<INativeInputWrapper> _input = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard();
    private readonly Mock<ICaptureFreshness> _freshness = new();

    public InputSimulationServiceFreshnessTests()
    {
        _input.Setup(w => w.SetCursorPos(It.IsAny<int>(), It.IsAny<int>())).Returns(true);
        _input.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()))
            .Returns((IReadOnlyList<InputEvent> events) => (uint)events.Count);
    }

    private InputSimulationService CreateSut() =>
        new(_monitors.Object, _input.Object, _guard.Object, new InputSession(_input.Object), Permissive.PasteGuard, Permissive.Takeover,
            captureFreshness: _freshness.Object);

    #region Input that was sent

    [Fact]
    public void Click_ThatWasSent_TellsTheReceivingWindow()
    {
        // Act
        CreateSut().Click(500, 300).Success.Should().BeTrue();

        // Assert
        _freshness.Verify(f => f.InputSent(Receiving, false), Times.AtLeastOnce);
        _freshness.Verify(f => f.InputSent(It.IsAny<nint>(), true), Times.Never);
    }

    [Fact]
    public void MoveMouse_ThatWasSent_TellsAPointerMoveOnly()
    {
        // Act
        CreateSut().MoveMouse(500, 300).Success.Should().BeTrue();

        // Assert
        _freshness.Verify(f => f.InputSent(Receiving, true), Times.AtLeastOnce);
        _freshness.Verify(f => f.InputSent(It.IsAny<nint>(), false), Times.Never);
    }

    [Fact]
    public async Task TypeText_ThatWasSent_TellsTheReceivingWindow()
    {
        // Act
        (await CreateSut().TypeTextAsync("hi")).Success.Should().BeTrue();

        // Assert
        _freshness.Verify(f => f.InputSent(Receiving, false), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SendKeys_ThatWereSent_TellTheReceivingWindow()
    {
        // Act
        (await CreateSut().SendKeysAsync("{ENTER}")).Success.Should().BeTrue();

        // Assert
        _freshness.Verify(f => f.InputSent(Receiving, false), Times.AtLeastOnce);
    }

    /// <summary>
    /// A service whose native input is <see cref="FakeInputWrapper"/> (it keeps the pointer where it was put and the keys that
    /// are down), on a 1920x1080 screen with a US layout: drag, scroll and press_keys read that state back.
    /// </summary>
    private InputSimulationService CreateSutWithFakeInput()
    {
        var input = new FakeInputWrapper();
        var systemInfo = new Mock<INativeSystemInfoWrapper>();
        systemInfo.Setup(s => s.GetVirtualScreen()).Returns(new VirtualScreenBounds(0, 0, 1920, 1080));
        systemInfo.Setup(s => s.AreMouseButtonsSwapped()).Returns(false);
        systemInfo.Setup(s => s.GetDragThreshold()).Returns((4, 4));
        var monitors = TestDataFactory.CreateMockMonitorService(
            [TestDataFactory.CreateMonitorInfo(boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)]);
        return new(monitors.Object, input, _guard.Object, new InputSession(input), Permissive.PasteGuard, Permissive.Takeover,
            systemInfo: systemInfo.Object, keyboardLayout: new FakeKeyboardLayoutWrapper(), captureFreshness: _freshness.Object);
    }

    [Fact]
    public async Task Drag_ThatWasSent_TellsTheReceivingWindow()
    {
        // Act
        var result = await CreateSutWithFakeInput().DragAsync(new DragRequest(100, 100, 300, 100, MouseButton.Primary, [], 4, 0, 0),
            InputTarget.Window(Receiving), InputTarget.Window(Receiving), false, CancellationToken.None);

        // Assert - the press and the drop are more than pointer moves
        result.Success.Should().BeTrue(result.Error);
        _freshness.Verify(f => f.InputSent(Receiving, false), Times.AtLeastOnce);
    }

    [Fact]
    public void Scroll_ThatWasSent_TellsTheReceivingWindow()
    {
        // Act
        var result = CreateSutWithFakeInput().Scroll(400, 300, 2, 0, [], InputTarget.Window(Receiving), false, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        _freshness.Verify(f => f.InputSent(Receiving, false), Times.AtLeastOnce);
    }

    [Fact]
    public async Task PressKeys_ThatWereSent_TellTheReceivingWindow()
    {
        // Act
        var result = await CreateSutWithFakeInput().PressKeysAsync(KeyList.Of("ctrl+s"), 1, 0, 0, InputTarget.Window(Receiving), false,
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        _freshness.Verify(f => f.InputSent(Receiving, false), Times.AtLeastOnce);
        _freshness.Verify(f => f.InputSent(It.IsAny<nint>(), true), Times.Never);
    }

    #endregion

    #region Input that was not sent

    [Fact]
    public void Click_RefusedByTheGuard_TellsNothing()
    {
        // Arrange
        _guard.Setup(g => g.CheckMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>()))
            .Returns((int _, int _, InputTarget target) =>
                InputGuardResult.Refuse(InputErrorCode.TargetDeniedByPolicy, "The user's policy denies it.", target));

        // Act
        CreateSut().Click(500, 300).Success.Should().BeFalse();

        // Assert
        _freshness.Verify(f => f.InputSent(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void Click_ThatSendInputDidNotInsert_TellsNothing()
    {
        // Arrange
        _input.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns(0u);

        // Act
        CreateSut().Click(500, 300).Success.Should().BeFalse();

        // Assert
        _freshness.Verify(f => f.InputSent(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task TypeText_ThatSendInputDidNotInsert_TellsNothing()
    {
        // Arrange
        _input.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns(0u);

        // Act
        (await CreateSut().TypeTextAsync("hi")).Success.Should().BeFalse();

        // Assert
        _freshness.Verify(f => f.InputSent(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    #endregion
}
