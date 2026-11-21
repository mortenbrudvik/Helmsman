using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WinDesktopMcp.Infrastructure;
using WinDesktopMcp.Tools;

namespace WinDesktopMcp.Tests.Contract;

/// <summary>
/// Contract: allowHostTarget sends input to the agent's own host window (its terminal, editor or chat app) only after the
/// user confirms it through the client (elicitation), once per call, and fails closed otherwise: a client that cannot ask
/// (ConfirmationRequired), a user who declines, dismisses, leaves the box unticked or does not answer in time, and a client
/// that fails to ask (ConfirmationDeclined) all send nothing. allowHostTarget on an ordinary window asks nothing, and the
/// host without it is refused as before (TargetIsAgentHost), without a question. Runs the real InputSimulationTools,
/// HostTargetConfirmation, InputSimulationService and InputTargetGuard through an in-process MCP server, with every native
/// wrapper, probe and the foreground service mocked, so the input that is or is not sent is a mock call.
/// </summary>
public sealed class HostTargetConfirmationContractTests : IDisposable
{
    /// <summary>The input tools, as the gate filter lists them: a new input tool is checked here without being added by hand.</summary>
    public static TheoryData<string> InputTools() => new(InputGateToolFilter.InputTools.Order(StringComparer.Ordinal));

    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromMilliseconds(300);

    private static readonly nint Terminal = 0x100;
    private static readonly nint Notepad = 0x200;
    private const int TerminalPid = 1001;
    private const int NotepadPid = 2001;

    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<INativeHitTestWrapper> _hitTest = new();
    private readonly Mock<INativeInputWrapper> _input = new();
    private readonly Mock<IWindowService> _windowService = new();
    private readonly Mock<IForegroundService> _foreground = new();
    private readonly Mock<IProcessSecurityProbe> _security = new();
    private readonly Mock<IHostGuard> _hostGuard = new();
    private readonly Mock<INativeThreadInputWrapper> _threads = new();
    private readonly Mock<INativeSystemInfoWrapper> _systemInfo = new();
    private readonly InputSession _session;
    private readonly List<string> _questions = [];
    private nint _inFront;

    public HostTargetConfirmationContractTests()
    {
        _session = new InputSession(_input.Object);
        _threads.Setup(t => t.GetWindowThreadId(It.IsAny<nint>())).Returns((nint window) => (uint)window + 1);
        _threads.Setup(t => t.GetGuiThreadInfo(It.IsAny<uint>())).Returns(new GuiThreadInfo(0, 0, 0, 0, 0));

        Window(Terminal, TerminalPid, "WindowsTerminal", "pwsh");
        Window(Notepad, NotepadPid, "notepad", "Untitled - Notepad");
        _hostGuard.Setup(g => g.IsHostWindow(Terminal)).Returns(true);

        var medium = new ProcessIntegrity(0, IntegrityLevel.Medium, ProcessIntegrity.MediumRid, false, null);
        _security.Setup(s => s.ServerIntegrity).Returns(medium);
        _security.Setup(s => s.GetIntegrity(It.IsAny<int>())).Returns(medium);
        _security.Setup(s => s.IsInputDesktopAvailable()).Returns(true);

        _windows.Setup(w => w.GetForegroundWindow()).Returns(() => _inFront);
        _foreground.Setup(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()))
            .Returns((nint handle, int _) =>
            {
                _inFront = handle;
                return new FocusOutcome { Target = handle, IsForeground = true, Method = FocusMethod.SetForeground, ForegroundHandle = handle };
            });
        _foreground.Setup(f => f.IsInFront(It.IsAny<nint>())).Returns((nint handle) => handle == _inFront);

        _input.Setup(i => i.SetCursorPos(It.IsAny<int>(), It.IsAny<int>())).Returns(true);
        _input.Setup(i => i.GetCursorPos()).Returns((400, 300));
        _input.Setup(i => i.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()))
            .Returns((IReadOnlyList<InputEvent> events) => (uint)events.Count);
        _systemInfo.Setup(s => s.GetMouseWheelRouting()).Returns(MouseWheelRouting.MousePosition);
    }

    public void Dispose() => _session.Dispose();

    private void Window(nint handle, int processId, string processName, string title)
    {
        _windows.Setup(w => w.IsWindow(handle)).Returns(true);
        _windows.Setup(w => w.GetAncestor(handle, It.IsAny<WindowAncestor>())).Returns(handle);
        _windows.Setup(w => w.GetWindowThreadProcessId(handle)).Returns((uint)processId);
        _windows.Setup(w => w.GetWindowLongPtr(handle, -16)).Returns(new nint(0x10CF0000)); // visible overlapped window
        _windowService.Setup(s => s.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo((int)handle, title, processName, processId));
    }

    /// <summary>Puts <paramref name="window"/> at every point and in front.</summary>
    private void At(nint window)
    {
        _inFront = window;
        _hitTest.Setup(h => h.WindowFromPoint(It.IsAny<int>(), It.IsAny<int>())).Returns(window);
        _hitTest.Setup(h => h.GetRootWindow(window)).Returns(window);
    }

    private bool AnythingSent() =>
        _input.Invocations.Any(i => i.Method.Name is nameof(INativeInputWrapper.SendInput) or nameof(INativeInputWrapper.SetCursorPos));

    /// <summary>How the user (or the client) answers the question.</summary>
    public enum Answer
    {
        Yes,
        Decline,
        Dismiss,
        Unticked,
        NoAnswerInTime,
        ClientFails
    }

    /// <summary>
    /// Starts a server whose client answers every question as <paramref name="answer"/> says, or that cannot ask at all (null:
    /// no elicitation handler, so the client does not advertise elicitation). <paramref name="meanwhile"/> runs while the
    /// question is open.
    /// </summary>
    private Task<InProcessMcpServer> StartAsync(Answer? answer, string? revision = null, Action? meanwhile = null)
    {
        var guard = new InputTargetGuard(
            _windows.Object, _hitTest.Object, _threads.Object, new Mock<INativeProcessWrapper>().Object,
            _windowService.Object, _foreground.Object, _security.Object, _hostGuard.Object,
            new TargetPolicy(TargetPolicySettings.Default, Permissive.Windows), InputGuardOptions.Default);
        var monitors = TestDataFactory.CreateMockMonitorService(
            [TestDataFactory.CreateMonitorInfo(boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)]);
        var service = new InputSimulationService(monitors.Object, _input.Object, guard, _session, Permissive.PasteGuard,
            Permissive.Takeover, hitTest: _hitTest.Object, systemInfo: _systemInfo.Object);
        var tools = new InputSimulationTools(service, Mock.Of<ICoordinateSpaceResolver>(),
            new HostTargetConfirmation(guard, _session, AnswerTimeout));

        McpClientOptions? options = null;
        if (answer is { } given)
        {
            options = new McpClientOptions
            {
                Handlers = new McpClientHandlers
                {
                    ElicitationHandler = async (request, token) =>
                    {
                        lock (_questions)
                        {
                            _questions.Add(request!.Message);
                        }

                        meanwhile?.Invoke();
                        switch (given)
                        {
                            case Answer.ClientFails:
                                throw new InvalidOperationException("The client could not show the question.");
                            case Answer.NoAnswerInTime:
                                await Task.Delay(TimeSpan.FromSeconds(2), token);
                                break;
                        }

                        return given switch
                        {
                            Answer.Decline => new ElicitResult { Action = "decline" },
                            Answer.Dismiss => new ElicitResult { Action = "cancel" },
                            _ => new ElicitResult
                            {
                                Action = "accept",
                                Content = new Dictionary<string, JsonElement>
                                {
                                    [UserConfirmation.ConfirmField] = JsonSerializer.SerializeToElement(given != Answer.Unticked)
                                }
                            }
                        };
                    }
                }
            };
        }

        return InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddToolErrorReporting().WithServerTools(tools),
            clientOptions: InProcessMcpServer.WithRevision(options, revision));
    }

    /// <summary>A call of <paramref name="tool"/> aimed at <paramref name="window"/>, at (400, 300) for the mouse tools.</summary>
    private static Dictionary<string, object?> Call(string tool, nint window, bool allowHostTarget = true)
    {
        var arguments = tool switch
        {
            "mouse_click" => new Dictionary<string, object?> { ["x"] = 400, ["y"] = 300 },
            "drag" => new Dictionary<string, object?>
            {
                ["fromX"] = 400, ["fromY"] = 300, ["toX"] = 420, ["toY"] = 300, ["steps"] = 1, ["stepDelayMs"] = 0, ["holdBeforeMoveMs"] = 0
            },
            "mouse_move" => new Dictionary<string, object?> { ["x"] = 400, ["y"] = 300 },
            "scroll" => new Dictionary<string, object?> { ["x"] = 400, ["y"] = 300, ["deltaY"] = 2 },
            "type_text" => new Dictionary<string, object?> { ["text"] = "git status", ["method"] = "unicode" },
            "send_keys" => new Dictionary<string, object?> { ["keys"] = "y{ENTER}" },
            "press_keys" => new Dictionary<string, object?> { ["keys"] = "ctrl+c" },
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Not an input tool this test knows: add its arguments.")
        };
        arguments["windowHandle"] = (long)window;
        if (allowHostTarget)
        {
            arguments["allowHostTarget"] = true;
        }

        return arguments;
    }

    private static JsonElement Payload(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();

    private IReadOnlyList<string> Questions()
    {
        lock (_questions)
        {
            return _questions.ToList();
        }
    }

    /// <summary>
    /// The input went out: the call succeeded, or for drag the button was pressed (the mocked pointer stays where SetCursorPos
    /// left it, so the drag's own check of the pointer at the drop may cancel it; it was sent all the same).
    /// </summary>
    private void AssertSent(string tool, CallToolResult result)
    {
        var payload = Payload(result);
        payload.GetProperty(tool == "drag" ? "pressed" : "success").GetBoolean().Should().BeTrue(payload.ToString());
        AnythingSent().Should().BeTrue();
    }

    private void AssertRefused(CallToolResult result, string errorCode, int questions)
    {
        var payload = Payload(result);
        result.IsError.Should().BeTrue(payload.ToString());
        payload.GetProperty("errorCode").GetString().Should().Be(errorCode, payload.ToString());
        payload.GetProperty("error").GetString().Should().Contain("Nothing was sent");
        Questions().Should().HaveCount(questions);
        AnythingSent().Should().BeFalse("nothing is sent without the user's yes");
    }

    // ---- The user says yes ----

    [Theory]
    [MemberData(nameof(InputTools))]
    public async Task TheUserConfirms_TheInputIsSentToTheHostWindow_AfterOneQuestion(string tool)
    {
        // Arrange
        At(Terminal);
        await using var server = await StartAsync(Answer.Yes);

        // Act
        var result = await server.CallToolAsync(tool, Call(tool, Terminal));

        // Assert
        AssertSent(tool, result);
        var question = Questions().Should().ContainSingle().Subject;
        question.Should().Contain("'pwsh' (WindowsTerminal, PID 1001)").And.Contain(tool);
    }

    [Theory]
    [MemberData(nameof(InputTools))]
    public async Task EachCallAsksAgain_AnApprovalIsNeverRemembered(string tool)
    {
        At(Terminal);
        await using var server = await StartAsync(Answer.Yes);

        await server.CallToolAsync(tool, Call(tool, Terminal));
        await server.CallToolAsync(tool, Call(tool, Terminal));

        Questions().Should().HaveCount(2);
    }

    // ---- Anything but a ticked yes sends nothing ----

    [Theory]
    [MemberData(nameof(InputTools))]
    public async Task TheUserDeclines_IsConfirmationDeclined_AndNothingIsSent(string tool)
    {
        At(Terminal);
        await using var server = await StartAsync(Answer.Decline);

        var result = await server.CallToolAsync(tool, Call(tool, Terminal));

        AssertRefused(result, nameof(WindowErrorCode.ConfirmationDeclined), questions: 1);
        Payload(result).GetProperty("error").GetString().Should().Contain("declined");
    }

    [Theory]
    [InlineData(Answer.Dismiss, "dismissed")]
    [InlineData(Answer.Unticked, "did not confirm")]
    public async Task TheUserDismissesOrLeavesTheBoxUnticked_IsConfirmationDeclined(Answer answer, string reason)
    {
        At(Terminal);
        await using var server = await StartAsync(answer);

        var result = await server.CallToolAsync("type_text", Call("type_text", Terminal));

        AssertRefused(result, nameof(WindowErrorCode.ConfirmationDeclined), questions: 1);
        Payload(result).GetProperty("error").GetString().Should().Contain(reason);
    }

    [Theory]
    [MemberData(nameof(InputTools))]
    public async Task AClientThatCannotAsk_IsConfirmationRequired_AndNothingIsSent(string tool)
    {
        // No elicitation handler: the client does not advertise elicitation, and no argument stands in for the user
        At(Terminal);
        await using var server = await StartAsync(answer: null);

        var result = await server.CallToolAsync(tool, Call(tool, Terminal));

        AssertRefused(result, nameof(WindowErrorCode.ConfirmationRequired), questions: 0);
        Payload(result).GetProperty("error").GetString().Should().Contain("cannot ask the user");
    }

    [Theory]
    [MemberData(nameof(InputTools))]
    public async Task NoAnswerInTime_IsConfirmationDeclined_AndNothingIsSent(string tool)
    {
        At(Terminal);
        await using var server = await StartAsync(Answer.NoAnswerInTime, InProcessMcpServer.InitializeRevision);

        var result = await server.CallToolAsync(tool, Call(tool, Terminal));

        AssertRefused(result, nameof(WindowErrorCode.ConfirmationDeclined), questions: 1);
        Payload(result).GetProperty("error").GetString().Should().Contain("did not answer");
    }

    [Theory]
    [MemberData(nameof(InputTools))]
    public async Task TheClientFailsToAsk_IsConfirmationDeclined_AndNothingIsSent(string tool)
    {
        At(Terminal);
        await using var server = await StartAsync(Answer.ClientFails, InProcessMcpServer.InitializeRevision);

        var result = await server.CallToolAsync(tool, Call(tool, Terminal));

        AssertRefused(result, nameof(WindowErrorCode.ConfirmationDeclined), questions: 1);
        Payload(result).GetProperty("error").GetString().Should().Contain("could not be asked");
    }

    // ---- When nothing is asked ----

    [Theory]
    [MemberData(nameof(InputTools))]
    public async Task AllowHostTargetOnAnOrdinaryWindow_AsksNothing_AndSends(string tool)
    {
        At(Notepad);
        await using var server = await StartAsync(Answer.Decline);

        var result = await server.CallToolAsync(tool, Call(tool, Notepad));

        AssertSent(tool, result);
        Questions().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(InputTools))]
    public async Task TheHostWithoutAllowHostTarget_IsTargetIsAgentHost_WithoutAQuestion(string tool)
    {
        At(Terminal);
        await using var server = await StartAsync(Answer.Yes);

        var result = await server.CallToolAsync(tool, Call(tool, Terminal, allowHostTarget: false));

        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.TargetIsAgentHost), payload.ToString());
        Questions().Should().BeEmpty();
        AnythingSent().Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(InputTools))]
    public async Task WhileInputIsPaused_NothingIsAsked_AndTheCallIsPausedByUser(string tool)
    {
        At(Terminal);
        _session.Pause(InputControlSources.Hotkey);
        await using var server = await StartAsync(Answer.Yes);

        var result = await server.CallToolAsync(tool, Call(tool, Terminal));

        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.PausedByUser));
        Questions().Should().BeEmpty();
        AnythingSent().Should().BeFalse();
    }

    // ---- The confirmation is bound to the window the user was shown ----

    [Theory]
    [MemberData(nameof(InputTools))]
    public async Task TheConfirmedWindowsHandleBelongsToAnotherProcessAtSendTime_IsRefused_AndNothingIsSent(string tool)
    {
        // Arrange - while the user answers, the terminal closes and another host process's window gets its handle
        At(Terminal);
        await using var server = await StartAsync(Answer.Yes, meanwhile: () => Window(Terminal, 4242, "WindowsTerminal", "pwsh"));

        // Act
        var result = await server.CallToolAsync(tool, Call(tool, Terminal));

        // Assert
        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.TargetIsAgentHost), payload.ToString());
        Questions().Should().ContainSingle();
        AnythingSent().Should().BeFalse();
    }

    [Fact]
    public async Task Drag_OntoTheHostAsToWindowHandle_AsksOnce_NamingTheHost_AndDropsThere()
    {
        // Arrange - from Notepad onto the terminal
        _inFront = Notepad;
        _hitTest.Setup(h => h.WindowFromPoint(400, 300)).Returns(Notepad);
        _hitTest.Setup(h => h.GetRootWindow(Notepad)).Returns(Notepad);
        _hitTest.Setup(h => h.WindowFromPoint(It.Is<int>(x => x > 400), 300)).Returns(Terminal);
        _hitTest.Setup(h => h.GetRootWindow(Terminal)).Returns(Terminal);
        await using var server = await StartAsync(Answer.Yes);

        // Act
        var arguments = Call("drag", Notepad);
        arguments["toX"] = 900;
        arguments["toWindowHandle"] = (long)Terminal;
        var result = await server.CallToolAsync("drag", arguments);

        // Assert
        AssertSent("drag", result);
        Questions().Should().ContainSingle().Which.Should().Contain("'pwsh'").And.NotContain("Notepad");
    }

    // ---- What the question says ----

    [Fact]
    public async Task TheQuestion_ForTypeText_GivesTheLengthAndABoundedOneLinePreview_NeverTheWholeText()
    {
        // Arrange - a long text over several lines, whose end must not be shown
        At(Terminal);
        var text = "rm -rf ~/projects\n" + new string('x', 3000) + "\nSECRET-END";
        await using var server = await StartAsync(Answer.Decline);

        // Act
        await server.CallToolAsync("type_text", new Dictionary<string, object?>
        {
            ["text"] = text, ["method"] = "unicode", ["controlChars"] = "unicode", ["windowHandle"] = (long)Terminal, ["allowHostTarget"] = true
        });

        // Assert
        var question = Questions().Should().ContainSingle().Subject;
        question.Should().Contain($"{text.Length} characters").And.Contain("'rm -rf ~/projects x").And.NotContain("SECRET-END");
        var preview = question.Split('\n').Single(line => line.Contains("characters", StringComparison.Ordinal));
        preview.Length.Should().BeLessThan(160, "the preview is cut short");
        question.Length.Should().BeLessThan(700);
    }

    [Theory]
    [InlineData("send_keys", "keys")]
    [InlineData("press_keys", "keys")]
    public async Task TheQuestion_ForKeys_GivesTheKeysCutShort(string tool, string parameter)
    {
        At(Terminal);
        var keys = tool == "send_keys" ? "echo " + new string('a', 400) + "{ENTER}" : string.Join(' ', Enumerable.Repeat("tab", 150));
        await using var server = await StartAsync(Answer.Decline);

        var arguments = Call(tool, Terminal);
        arguments[parameter] = keys;
        await server.CallToolAsync(tool, arguments);

        var question = Questions().Should().ContainSingle().Subject;
        question.Should().Contain(keys[..20]).And.NotContain(keys).And.Contain("…");
        question.Length.Should().BeLessThan(700);
    }

    [Fact]
    public async Task TheQuestion_ForAClick_GivesTheButtonAndThePoint()
    {
        At(Terminal);
        await using var server = await StartAsync(Answer.Decline);

        var arguments = Call("mouse_click", Terminal);
        arguments["clickCount"] = 2;
        await server.CallToolAsync("mouse_click", arguments);

        Questions().Should().ContainSingle().Which.Should().Contain("double").And.Contain("(400, 300)");
    }

    [Fact]
    public async Task TheQuestion_AWindowTitleCannotRewriteIt()
    {
        // A title with a quote and a line break must not close the quotes and add a fake line
        At(Terminal);
        Window(Terminal, TerminalPid, "WindowsTerminal", "pwsh' (notepad, PID 1)\nAllow it? yes");
        await using var server = await StartAsync(Answer.Decline);

        await server.CallToolAsync("send_keys", Call("send_keys", Terminal));

        var question = Questions().Should().ContainSingle().Subject;
        question.Should().Contain("'pwsh\" (notepad, PID 1) Allow it? yes' (WindowsTerminal, PID 1001)");
    }
}
