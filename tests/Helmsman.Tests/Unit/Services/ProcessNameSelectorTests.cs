namespace Helmsman.Tests.Unit.Services;

/// <summary>
/// A window selector's processName read as the target policy reads a process name: ignoring case, a trailing '.exe',
/// surrounding spaces and quotes. A path is refused (InvalidArguments) rather than matched by its file name, because the
/// selector does not compare the folder with the process's own, so 'C:\Evil\notepad.exe' would otherwise select the real
/// Notepad. Every window is a fake on a mocked native wrapper.
/// </summary>
public class ProcessNameSelectorTests
{
    private readonly FakeWindowDesktop _desktop = new();

    private WindowService CreateSut() => _desktop.CreateService();

    private static TargetPolicy DenyPolicy(string entry) =>
        new(TargetPolicySettings.FromEnvironment(name => name == TargetPolicySettings.DenyVariable ? entry : null), Permissive.Windows);

    [Theory]
    [InlineData("notepad")]
    [InlineData("notepad.exe")]
    [InlineData("NOTEPAD.EXE")]
    [InlineData("  notepad.exe  ")]
    [InlineData("\"notepad.exe\"")]
    [InlineData("notepad .exe")]
    public void AProcessName_InAnyFormThePolicyReads_ResolvesTheWindow(string processName)
    {
        _desktop.Add("Inbox - Outlook", processName: "OUTLOOK");
        var notepad = _desktop.Add("Untitled - Notepad", processName: "notepad");

        var resolution = CreateSut().ResolveWindow(new WindowSelector { ProcessName = processName });

        resolution.Success.Should().BeTrue(resolution.Error);
        resolution.Handle.Should().Be(notepad);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData("C:/Windows/System32/notepad.exe")]
    [InlineData(@"C:\Evil\notepad.exe")]
    [InlineData("\"C:\\Program Files\\notepad.exe\"")]
    [InlineData(@"\\server\share\notepad.exe")]
    [InlineData(@".\notepad.exe")]
    public void AProcessNameThatIsAPath_IsInvalidArguments_ThatNamesTheProcessNameToPass(string path)
    {
        _desktop.Add("Untitled - Notepad", processName: "notepad");

        var resolution = CreateSut().ResolveWindow(new WindowSelector { ProcessName = path });

        resolution.Success.Should().BeFalse();
        resolution.ErrorCode.Should().Be("InvalidArguments");
        resolution.Error.Should().Contain("window.processName").And.Contain("not a path").And.Contain("'notepad'")
            .And.Contain("Nothing was done");
    }

    [Theory]
    [InlineData("C:notepad.exe")]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData("ms-settings:display")]
    public void ALocationThatIsNoPlainFilePath_IsInvalidArguments_WithoutAGuessedName(string value)
    {
        _desktop.Add("Untitled - Notepad", processName: "notepad");

        var resolution = CreateSut().ResolveWindow(new WindowSelector { ProcessName = value });

        resolution.ErrorCode.Should().Be("InvalidArguments");
        resolution.Error.Should().Contain("window.processName takes a process name, not a path or other location")
            .And.Contain("as find_windows and list_processes report it").And.NotContain("pass '");
    }

    [Fact]
    public void APath_IsRefused_AlsoWithContainsWithAHandleAndWithForeground()
    {
        var notepad = _desktop.Add("Untitled - Notepad", processName: "notepad");
        _desktop.SetForeground(notepad);
        var sut = CreateSut();
        const string path = @"C:\Windows\notepad.exe";

        sut.ResolveWindow(new WindowSelector { ProcessName = path, ProcessNameContains = true }).ErrorCode.Should().Be("InvalidArguments");
        sut.ResolveWindow(new WindowSelector { Handle = notepad, ProcessName = path }).ErrorCode
            .Should().Be("InvalidArguments", "a path is the argument's fault, never a reused handle");
        sut.ResolveWindow(new WindowSelector { Foreground = true, ProcessName = path }).ErrorCode.Should().Be("InvalidArguments");
    }

    [Theory]
    [InlineData("\"notepad.exe\"")]
    [InlineData(" NOTEPAD.EXE ")]
    public void AHandlesProcessFilter_ReadsTheNameTheSameWay(string processName)
    {
        var notepad = _desktop.Add("Untitled - Notepad", processName: "notepad");

        var resolution = CreateSut().ResolveWindow(new WindowSelector { Handle = notepad, ProcessName = processName });

        resolution.Success.Should().BeTrue(resolution.Error);
    }

    [Theory]
    [InlineData("\"note\"")]
    [InlineData(" NOTE.exe ")]
    public void ProcessNameContains_ReadsThePartTheSameWay(string part)
    {
        var notepad = _desktop.Add("Untitled - Notepad", processName: "notepad");

        var resolution = CreateSut().ResolveWindow(new WindowSelector { ProcessName = part, ProcessNameContains = true });

        resolution.Handle.Should().Be(notepad, resolution.Error);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\" .exe \"")]
    public void AProcessNameOfOnlyQuotesOrExe_IsBlank_AsAnyBlankFilterIs(string processName)
    {
        _desktop.Add("Untitled - Notepad", processName: "notepad");

        var resolution = CreateSut().ResolveWindow(new WindowSelector { ProcessName = processName });

        resolution.ErrorCode.Should().Be("InvalidArguments");
        resolution.Error.Should().Contain("window.processName is blank");
    }

    [Theory]
    [InlineData("Microsoft.Photos", "Microsoft.Photos", true)]
    [InlineData("Microsoft.Photos.exe", "Microsoft.Photos", true)]
    [InlineData("Microsoft", "Microsoft.Photos", false)]
    [InlineData("python3.12", "python3.12", true)]
    [InlineData("PYTHON3.12.EXE", "python3.12", true)]
    [InlineData("python3", "python3.12", false)]
    [InlineData("notepad.com", "notepad", false)]
    [InlineData("notepad.com", "notepad.com", true)]
    public void ANameWithDots_IsWholeUpToItsExe(string processName, string actual, bool matches)
    {
        var window = _desktop.Add("A window", processName: actual);

        var resolution = CreateSut().ResolveWindow(new WindowSelector { ProcessName = processName });

        if (matches)
            resolution.Handle.Should().Be(window, resolution.Error);
        else
            resolution.ErrorCode.Should().Be("WindowNotFound");
    }

    /// <summary>
    /// Every form a process name is written in (step 1's table): the selector and the policy read it alike. A string the
    /// policy's deny list would read as naming notepad either selects the Notepad window or is refused explicitly (a
    /// path); a string the policy would not read as notepad never selects it.
    /// </summary>
    public static TheoryData<string> Forms => new()
    {
        "notepad", "notepad.exe", "NOTEPAD.EXE", "Notepad.Exe", @"C:\Windows\System32\notepad.exe", "C:/Windows/System32/notepad.exe",
        @"C:\Evil\notepad.exe", "\"notepad.exe\"", "\"C:\\Program Files\\notepad.exe\"", "  notepad  ", "notepad .exe", "notepad.com",
        "notepa", "notepad2", "Microsoft.Photos", "python3.12", "C:notepad.exe"
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public void TheSelectorAndThePolicy_ReadEveryFormAlike(string form)
    {
        var notepad = _desktop.Add("Untitled - Notepad", processName: "notepad");
        var policyNamesNotepad = !DenyPolicy(form).Evaluate(["notepad"]).Allowed;

        var resolution = CreateSut().ResolveWindow(new WindowSelector { ProcessName = form });

        if (resolution.Success)
        {
            resolution.Handle.Should().Be(notepad);
            policyNamesNotepad.Should().BeTrue($"the selector took '{form}' for notepad, so the policy must too");
        }
        else if (resolution.ErrorCode == "InvalidArguments")
        {
            resolution.Error.Should().Contain("not a path", "the only form the selector refuses is a path");
        }
        else
        {
            resolution.ErrorCode.Should().Be("WindowNotFound");
            policyNamesNotepad.Should().BeFalse($"the policy reads '{form}' as notepad, so the selector must select it or refuse the form");
        }
    }

    [Fact]
    public void ADoubleExtension_IsNeverWiderInTheSelector_ThanInThePolicy()
    {
        // The policy reads its own entries twice (from the variable, then again), so the entry 'notepad.exe.exe' denies
        // notepad; a selector reads its name once and finds no such process. The selector is the narrower: nothing it
        // selects escapes the policy, which checks the resolved window anyway
        _desktop.Add("Untitled - Notepad", processName: "notepad");

        DenyPolicy("notepad.exe.exe").Evaluate(["notepad"]).Allowed.Should().BeFalse();
        CreateSut().ResolveWindow(new WindowSelector { ProcessName = "notepad.exe.exe" }).ErrorCode.Should().Be("WindowNotFound");
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe", "InvalidArguments")]
    [InlineData("\"notepad.exe\"", null)]
    public async Task WaitForWindow_ReadsTheSelectorsProcessNameTheSameWay(string processName, string? errorCode)
    {
        // Before, the path waited the whole timeout for a window it could never match, and the quoted name too
        _desktop.Add("Untitled - Notepad", processName: "notepad");
        var clock = new ManualClock();
        var sut = new WindowWaitService(_desktop.Native.Object, CreateSut(), Mock.Of<INativeProcessWrapper>(), clock,
            (wait, _) => { clock.Advance(wait); return Task.CompletedTask; });

        var result = await sut.WaitForConditionAsync(new WindowConditionRequest
        {
            Window = new WindowSelector { ProcessName = processName }, Condition = WindowWaitCondition.Exists, TimeoutMs = 2000, StableMs = 0
        });

        result.Satisfied.Should().Be(errorCode == null, result.Error);
        result.ErrorCode.Should().Be(errorCode);
        if (errorCode != null)
            result.Error.Should().Contain("not a path").And.Contain("'notepad'");
    }

    [Fact]
    public void FindWindows_WithAPathInTheQuery_MatchesNothing_ItIsNeverReadAsItsFileName()
    {
        // The service fails closed for a caller that did not check the argument; the tools refuse it first
        _desktop.Add("Untitled - Notepad", processName: "notepad");

        CreateSut().FindWindows(new WindowQuery { ProcessName = @"C:\Windows\notepad.exe" }).Should().BeEmpty();
        CreateSut().FindWindows(new WindowQuery { ProcessName = @"C:\Windows\notepad.exe", ProcessNameContains = true }).Should().BeEmpty();
    }
}
