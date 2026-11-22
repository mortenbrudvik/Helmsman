using System.Diagnostics;

namespace WinDesktopMcp.Tests.Unit.Services;

/// <summary>
/// Windows' own management consoles through the whole launch path: the validation, the real <see cref="ExecutableResolver"/>
/// and <see cref="InterpreterGuard"/>, over one fake file system (System32, the Windows folder, a PATH folder and the working
/// directory), so that what the guard judges is shown to be the file that is started. Nothing touches the real file system
/// or starts a process: the process wrapper records the start it is handed.
/// </summary>
public class LaunchPlanningSystemConsoleTests
{
    private const string WindowsDir = @"C:\Windows";
    private const string SystemDir = @"C:\Windows\System32";
    private const string PathDir = @"C:\Tools";
    private const string WorkingDir = @"C:\Users\me\repo";
    private const string Services = @"C:\Windows\System32\services.msc";

    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase)
    {
        Services, @"C:\Windows\System32\devmgmt.msc", @"C:\Windows\System32\mmc.exe",
        // A planted copy of the same name in the working and the current directory, and consoles only those places have
        @"C:\Users\me\repo\services.msc", @"C:\Users\me\repo\local.msc", @"C:\Tools\pathonly.msc",
        @"C:\Users\me\Downloads\services.msc", @"C:\Windows\SysWOW64\services.msc", @"C:\Windows\System32\en-US\services.msc"
    };

    private readonly Mock<INativeProcessWrapper> _processes = new(MockBehavior.Strict);
    private readonly List<ProcessStartInfo> _started = [];

    public LaunchPlanningSystemConsoleTests()
    {
        var process = new Mock<IProcessHandle>();
        process.SetupGet(p => p.Id).Returns(4242);
        _processes.Setup(p => p.StartProcess(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(_started.Add)
            .Returns(process.Object);
    }

    private WiredProcessService CreateSut() => WiredProcessService.Create(
        TestDataFactory.CreateMockWindowService().Object,
        new InputValidationService(ProtocolAllowlist.BuiltIn),
        TestDataFactory.CreateMockBrowserDetectionService().Object,
        new ExecutableResolver(_files.Contains, () => PathDir, _ => [], WindowsDir, SystemDir, () => WorkingDir,
            () => ".COM;.EXE;.BAT;.CMD;.VBS;.JS;.MSC"),
        Permissive.TargetPolicy,
        Permissive.HostGuard,
        new ChromiumPrograms(),
        new InterpreterGuard(_ => null, _files.Contains, SystemDir),
        new DebugBrowserLocator(),
        Permissive.SecurityProbe,
        new ChromiumSwitchGuard(),
        _processes.Object);

    [Theory]
    [InlineData("services.msc", null)]
    [InlineData("services.msc", WorkingDir)]
    [InlineData("SERVICES.MSC", WorkingDir)]
    [InlineData(Services, WorkingDir)]
    [InlineData(@"c:\windows\system32\SERVICES.msc", null)]
    public void PlanLaunch_AWindowsConsole_StartsItsFullSystem32Path_NeverTheCopyInTheWorkingDirectory(string executable, string? workingDirectory)
    {
        // Act
        var plan = CreateSut().PlanLaunch(executable, workingDirectory: workingDirectory);

        // Assert - the full System32 path, which the guard judged, not the bare name ShellExecute would search for
        plan.IsValid.Should().BeTrue(plan.ErrorMessage);
        plan.StartInfo!.FileName.Should().BeEquivalentTo(Services);
        Path.IsPathFullyQualified(plan.StartInfo.FileName).Should().BeTrue();
        plan.StartInfo.Arguments.Should().BeEmpty();
        plan.StartInfo.UseShellExecute.Should().BeTrue();
    }

    [Fact]
    public async Task LaunchApplication_ABareConsoleName_HandsTheLauncherTheFullSystem32Path()
    {
        // Act
        var result = await CreateSut().LaunchApplicationAsync("services.msc", workingDirectory: WorkingDir);

        // Assert
        result.Success.Should().BeTrue(result.ErrorMessage);
        _started.Should().ContainSingle();
        _started[0].FileName.Should().Be(Path.Combine(SystemDir, "services.msc"));
        _started[0].Arguments.Should().BeEmpty();
        _started[0].WorkingDirectory.Should().Be(WorkingDir);
    }

    [Fact]
    public void CheckLaunch_AsLayoutsCheckTheirLaunches_GetsTheSameNarrowAllowance()
    {
        // Act - apply_layout checks a preset's launch with CheckLaunch before it starts anything (LayoutApplier)
        var sut = CreateSut();
        var console = sut.CheckLaunch(new LaunchRequest("services.msc"));
        var downloaded = sut.CheckLaunch(new LaunchRequest(@"C:\Users\me\Downloads\services.msc"));
        var withArguments = sut.CheckLaunch(new LaunchRequest("services.msc", ["/s"]));

        // Assert
        console.Should().BeNull();
        downloaded!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        withArguments!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
    }

    /// <summary>
    /// What the launch path does with each odd form: some are refused by the validation or the resolver before the guard
    /// sees them, some are normalised by the validation to the plain path (which the guard then judges and which is what
    /// starts), and the rest are refused by the guard.
    /// </summary>
    public static readonly TheoryData<string, string> Refused = new()
    {
        // The bare name of a console only the working directory has: never resolved from there
        { "local.msc", nameof(LaunchErrorCode.ExecutableInWorkingDirectory) },
        // Only a PATH folder has it: resolved there, and refused as no Windows console
        { "pathonly.msc", nameof(LaunchErrorCode.ScriptFile) },
        { "nosuch.msc", nameof(LaunchErrorCode.ExecutableNotFound) },
        { @"C:\Windows\System32\nosuch.msc", nameof(LaunchErrorCode.ScriptFile) },
        { @"C:\Users\me\Downloads\services.msc", nameof(LaunchErrorCode.ScriptFile) },
        { @"C:\Users\me\repo\services.msc", nameof(LaunchErrorCode.ScriptFile) },
        { @"C:\Windows\SysWOW64\services.msc", nameof(LaunchErrorCode.ScriptFile) },
        { @"C:\Windows\System32\en-US\services.msc", nameof(LaunchErrorCode.ScriptFile) },
        { @"C:\Windows\System32evil\services.msc", nameof(LaunchErrorCode.ScriptFile) },
        { @"C:\Windows\System32\..\Temp\services.msc", nameof(LaunchErrorCode.PathTraversal) },
        { @"C:\Windows\System32\..\System32\services.msc", nameof(LaunchErrorCode.PathTraversal) },
        { @"C:\Windows\System32\services.msc:payload", nameof(LaunchErrorCode.ScriptFile) },
        { @"C:\Windows\System32\services.msc::$DATA", nameof(LaunchErrorCode.ScriptFile) },
        { @"C:\WINDOWS\SYSTEM~1\services.msc", nameof(LaunchErrorCode.ScriptFile) },
        { @"C:\Windows\System32\SERVIC~1.MSC", nameof(LaunchErrorCode.ScriptFile) },
        { @"\\?\C:\Windows\System32\services.msc", nameof(LaunchErrorCode.InvalidCharacters) },
        { @"\\localhost\C$\Windows\System32\services.msc", nameof(LaunchErrorCode.ScriptFile) },
        { @"\\server\share\services.msc", nameof(LaunchErrorCode.ScriptFile) },
        { "file:///C:/Windows/System32/../Temp/services.msc", nameof(LaunchErrorCode.ScriptFile) },
        { "file:///C:/Users/me/Downloads/services.msc", nameof(LaunchErrorCode.ScriptFile) }
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task LaunchApplication_AConsoleThatIsNotWindowsOwn_IsRefused_AndNothingStarts(string executable, string errorCode)
    {
        // Act
        var result = await CreateSut().LaunchApplicationAsync(executable, workingDirectory: WorkingDir);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(errorCode);
        _started.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\services.msc.")]
    [InlineData(@"C:\Windows\System32\services.msc. .")]
    [InlineData("C:/Windows/System32/services.msc")]
    [InlineData(@"C:\Windows\System32\.\services.msc")]
    [InlineData(@"C:\Windows\System32\\services.msc")]
    [InlineData("file:///C:/Windows/System32/services.msc")]
    public void PlanLaunch_AFormTheValidationNormalises_StartsOnlyThePlainSystem32Path(string executable)
    {
        // Act - the validation hands the resolver Path.GetFullPath's form (a file: URI's local path), which the guard judges
        // and which is started
        var plan = CreateSut().PlanLaunch(executable);

        // Assert
        plan.IsValid.Should().BeTrue(plan.ErrorMessage);
        plan.StartInfo!.FileName.Should().Be(Services);
    }

    [Theory]
    [InlineData("services.msc", new[] { "/s" }, nameof(LaunchErrorCode.ScriptFile))]
    [InlineData(Services, new[] { "/a" }, nameof(LaunchErrorCode.ScriptFile))]
    [InlineData("mmc", new[] { "services.msc" }, nameof(LaunchErrorCode.InterpreterWithArguments))]
    [InlineData("mmc.exe", new[] { "/a", "x.msc" }, nameof(LaunchErrorCode.InterpreterWithArguments))]
    public async Task LaunchApplication_AConsoleOrMmcWithArguments_IsRefused(string executable, string[] arguments, string errorCode)
    {
        // Act
        var result = await CreateSut().LaunchApplicationAsync(executable, arguments);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(errorCode);
        _started.Should().BeEmpty();
        if (errorCode == nameof(LaunchErrorCode.InterpreterWithArguments))
            result.ErrorMessage.Should().Contain("launch its .msc file itself").And.Contain("services.msc");
    }

    [Fact]
    public void PlanLaunch_Mmc_WithoutArguments_StillStarts()
    {
        var plan = CreateSut().PlanLaunch("mmc");

        plan.IsValid.Should().BeTrue(plan.ErrorMessage);
        plan.StartInfo!.FileName.Should().Be(@"C:\Windows\System32\mmc.exe");
    }
}
