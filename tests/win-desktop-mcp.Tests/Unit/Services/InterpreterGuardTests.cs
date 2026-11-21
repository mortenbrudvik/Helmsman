namespace WinDesktopMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="InterpreterGuard"/> (roadmap M3.F8): launch_application refuses a command interpreter or
/// script host started with arguments, and a script file that Windows would run with one. Only names are read; the 8.3
/// short-name expansion is a fake, so nothing touches the file system.
/// </summary>
public class InterpreterGuardTests
{
    private const string Cmd = @"C:\Windows\System32\cmd.exe";
    private const string Notepad = @"C:\Windows\System32\notepad.exe";

    private readonly Dictionary<string, string> _longPaths = new(StringComparer.OrdinalIgnoreCase);

    private InterpreterGuard CreateSut() => new(path => _longPaths.TryGetValue(path, out var longPath) ? longPath : null);

    #region Interpreters with arguments

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData(@"C:\Windows\System32\wscript.exe")]
    [InlineData(@"C:\Windows\System32\cscript.exe")]
    [InlineData(@"C:\Windows\System32\mshta.exe")]
    [InlineData(@"C:\Windows\System32\rundll32.exe")]
    [InlineData(@"C:\Windows\System32\regsvr32.exe")]
    [InlineData(@"C:\Windows\System32\wsl.exe")]
    [InlineData(@"C:\Windows\System32\bash.exe")]
    [InlineData(@"C:\Python312\python.exe")]
    [InlineData(@"C:\Python312\pythonw.exe")]
    [InlineData(@"C:\Windows\py.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\python3.12.exe")]
    [InlineData(@"C:\Program Files\nodejs\node.exe")]
    [InlineData(@"C:\Program Files\Java\jdk-21\bin\java.exe")]
    [InlineData(@"C:\Program Files\Java\jdk-21\bin\javaw.exe")]
    [InlineData(@"C:\Windows\System32\msiexec.exe")]
    [InlineData(@"C:\Windows\System32\conhost.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\ubuntu2204.exe")]
    public void Check_AnInterpreterWithArguments_IsRefused(string interpreter)
    {
        // Act
        var refusal = CreateSut().Check(interpreter, interpreter, ["-c", "calc"]);

        // Assert
        refusal.Should().NotBeNull();
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        refusal.Message.Should().Contain(interpreter).And.Contain("does not run commands or scripts")
            .And.Contain("Ask the user").And.Contain("Nothing was started");
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("CMD.EXE")]
    [InlineData("\"C:\\Windows\\System32\\cmd.exe\"")]
    [InlineData(@"C:/Windows/System32/Cmd.Exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe.")]
    [InlineData(@"C:\Windows\System32\cmd.exe ")]
    [InlineData(@"C:\Windows\System32\cmd.exe::$DATA")]
    [InlineData(@"C:\Windows\System32\cmd")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    public void Check_TheInterpreterName_IsReadRobustly(string executable)
    {
        // Act - the requested name alone, as when the resolver handed it back unchanged
        var refusal = CreateSut().Check(executable, executable, ["/c", "calc"]);

        // Assert
        refusal.Should().NotBeNull();
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
    }

    [Fact]
    public void Check_TheResolvedProgram_Counts_WhateverNameWasAskedFor()
    {
        // Act - an App Paths entry may map any name to an interpreter
        var refusal = CreateSut().Check("myshell", Cmd, ["/c", "calc"]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        refusal.Message.Should().Contain("myshell").And.Contain(Cmd);
    }

    [Fact]
    public void Check_AnEightDotThreeShortName_IsExpandedFirst()
    {
        // Arrange
        const string shortPath = @"C:\Windows\System32\WINDOW~1\v1.0\POWERS~1.EXE";
        _longPaths[shortPath] = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

        // Act
        var refusal = CreateSut().Check(shortPath, shortPath, ["-Command", "calc"]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Check_AnInterpreterWithoutArguments_IsAllowed(bool emptyArray)
    {
        // Act - an empty console or terminal runs nothing on its own
        var refusal = CreateSut().Check("cmd", Cmd, emptyArray ? [] : null);

        // Assert
        refusal.Should().BeNull();
    }

    [Fact]
    public void Check_AnEmptyArgument_StillCounts()
    {
        // Act
        var refusal = CreateSut().Check("cmd", Cmd, [""]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"C:\Program Files\Microsoft VS Code\Code.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Tools\cmdline-helper.exe")]
    [InlineData(@"C:\Tools\pythonista.exe")]
    [InlineData("https://example.com/cmd.exe")]
    [InlineData("ms-settings:display")]
    public void Check_OtherProgramsAndUris_WithArguments_AreAllowed(string target)
    {
        // Act
        var refusal = CreateSut().Check(target, target, ["--flag", "value"]);

        // Assert
        refusal.Should().BeNull();
    }

    [Fact]
    public void Interpreters_CoverTheShellsScriptHostsAndRuntimes()
    {
        // Assert - the names are kept without .exe, in lower case
        InterpreterGuard.Interpreters.Should().Contain([
            "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "wsl", "bash",
            "python", "pythonw", "py", "node", "java", "javaw", "msiexec", "conhost", "wt"]);
        InterpreterGuard.Interpreters.Should().OnlyContain(name => name == name.ToLowerInvariant() && !name.EndsWith(".exe"));
    }

    #endregion

    #region Script files

    [Theory]
    [InlineData(@"C:\Users\me\Desktop\run.bat")]
    [InlineData(@"C:\Users\me\Desktop\run.CMD")]
    [InlineData(@"C:\Users\me\Desktop\run.ps1")]
    [InlineData(@"C:\Users\me\Desktop\run.vbs")]
    [InlineData(@"C:\Users\me\Desktop\run.js")]
    [InlineData(@"C:\Users\me\Desktop\run.wsf")]
    [InlineData(@"C:\Users\me\Desktop\run.hta")]
    [InlineData(@"C:\Users\me\Desktop\run.py")]
    [InlineData(@"C:\Users\me\Desktop\run.jar")]
    [InlineData(@"C:\Users\me\Desktop\setup.msi")]
    [InlineData(@"C:\Users\me\Desktop\run.bat.")]
    [InlineData(@"C:\Users\me\Desktop\run.bat::$DATA")]
    public void Check_AScriptFile_IsRefused_WithOrWithoutArguments(string script)
    {
        // Act
        var bare = CreateSut().Check(script, script, null);
        var withArguments = CreateSut().Check(script, script, ["x"]);

        // Assert
        bare!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        bare.Message.Should().Contain(script).And.Contain("does not run commands or scripts").And.Contain("Nothing was started");
        withArguments!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
    }

    [Fact]
    public void Check_ABareNameThatResolvedToABatchFile_IsRefused_AndPointsAtTheProgramsExe()
    {
        // Act - VS Code's 'code' is bin\code.cmd on PATH
        var refusal = CreateSut().Check("code", @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd", [@"C:\Projects\MyApp"]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        refusal.Message.Should().Contain("code.cmd").And.Contain("code.exe");
    }

    [Theory]
    [InlineData(@"C:\Users\me\Documents\report.pdf")]
    [InlineData(@"C:\Users\me\Documents\notes.txt")]
    [InlineData(@"C:\Users\me\Documents")]
    [InlineData("https://example.com/install.ps1")]
    public void Check_DocumentsFoldersAndWebAddresses_AreAllowed(string target)
    {
        // Act
        var refusal = CreateSut().Check(target, target, null);

        // Assert
        refusal.Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\Users\me\Desktop\run.bat")]
    [InlineData("\"C:\\Users\\me\\Desktop\\run.vbs\"")]
    [InlineData("file:///C:/Users/me/Desktop/run.hta")]
    [InlineData(@"/select,C:\Users\me\Desktop\run.ps1")]
    public void Check_ExplorerGivenAScript_IsRefused(string argument)
    {
        // Act - explorer.exe opens its argument with the associated program, which runs a script
        var refusal = CreateSut().Check("explorer", @"C:\Windows\explorer.exe", [argument]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        refusal.Message.Should().Contain("explorer");
    }

    [Theory]
    [InlineData(@"C:\Users\me\Documents")]
    [InlineData(@"C:\Users\me\Documents\report.pdf")]
    [InlineData(@"/select,C:\Users\me\Documents\report.pdf")]
    public void Check_ExplorerGivenAFolderOrDocument_IsAllowed(string argument)
    {
        // Act
        var refusal = CreateSut().Check("explorer", @"C:\Windows\explorer.exe", [argument]);

        // Assert
        refusal.Should().BeNull();
    }

    [Fact]
    public void Check_AnotherProgramGivenAScriptAsItsDocument_IsAllowed()
    {
        // Act - Notepad opens the script as text
        var refusal = CreateSut().Check("notepad", Notepad, [@"C:\Users\me\Desktop\run.bat"]);

        // Assert
        refusal.Should().BeNull();
    }

    [Fact]
    public void ScriptExtensions_CoverBatchPowerShellScriptHostAndPythonFiles()
    {
        // Assert
        InterpreterGuard.ScriptExtensions.Should().Contain([".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".py", ".pyw"]);
        // .com is a program type the resolver starts as a native program (tree.com, more.com), as .exe is
        InterpreterGuard.ScriptExtensions.Should().NotContain([".exe", ".com", ".txt", ".pdf", ".docx", ".png"]);
    }

    #endregion

    #region System tools, transfer tools and openers refused with arguments (launch guard blocklist)

    /// <summary>
    /// The programs added to <see cref="InterpreterGuard.Interpreters"/> by the launch guard blocklist review: native tools
    /// whose arguments destroy data, change the system, kill or control processes and services, run another command, or move
    /// data off the machine. Kept here by hand, so that dropping one from the guard fails a test.
    /// </summary>
    public static readonly TheoryData<string> SystemTools = new()
    {
        // Shells, terminals and WSL
        "zsh", "fish", "nu", "ksh", "dash", "tcsh", "csh", "busybox", "mintty", "git-bash", "git-cmd", "wslconfig",

        // Language runtimes
        "bun", "go", "cargo", "julia", "lua", "rscript", "jshell", "csi", "fsi",

        // Power and sessions
        "shutdown", "logoff", "tsdiscon", "rwinsta", "psshutdown",

        // Processes
        "taskkill", "tskill", "pskill", "pssuspend",

        // Registry
        "reg", "regedit", "regedt32", "regini",

        // Services, drivers, network, accounts, policy and settings
        "sc", "net", "net1", "netsh", "route", "ipconfig", "pnputil", "bcdedit", "bcdboot", "reagentc", "dism", "wusa", "setx",
        "auditpol", "secedit", "powercfg", "cmdkey", "mpcmdrun", "fltmc", "tscon", "dsregcmd",

        // Disks, files and permissions
        "cipher", "format", "diskpart", "mountvol", "fsutil", "vssadmin", "wbadmin", "wevtutil", "manage-bde", "takeown",
        "icacls", "cacls", "robocopy", "xcopy", "sdelete", "tar", "chkdsk", "convert", "mbr2gpt",

        // Programs that run, load or open what their arguments name
        "runas", "psexec", "psexec64", "paexec", "at", "winrs", "msdt", "infdefaultinstall", "odbcconf", "mavinject",
        "presentationhost", "syncappvpublishingserver", "appvlp", "atbroker", "pcwrun", "tttracer", "register-cimprovider",
        "wuauclt", "wlrmdr", "mmc", "control", "hh",

        // The programs that open a refused file type when named with it (mstsc x.rdp, AutoHotkey64 x.ahk, javaws x.jnlp)
        "mstsc", "autohotkey", "autohotkeyu", "autohotkeya", "autohotkeyux", "autoit3", "autoit3_x64", "javaws", "vstoinstaller",
        "appinstaller", "provtool", "keyhh", "msrdc",

        // 64-bit builds, matched by their own names (system tools are matched exactly, without a version)
        "nc64", "pskill64", "pssuspend64", "psshutdown64", "sdelete64",

        // Download, upload and remote access
        "curl", "wget", "certutil", "certreq", "bitsadmin", "ftp", "tftp", "finger", "esentutl", "expand", "extrac32",
        "replace", "cmdl32", "desktopimgdownldr", "ssh", "scp", "sftp", "sshd", "rsync", "plink", "pscp", "psftp", "nc", "ncat",
        "netcat", "rclone", "azcopy", "aws", "gh", "git",

        // Package managers and containers
        "winget", "choco", "pip", "pipx", "uv", "uvx", "conda", "pnpm", "docker", "podman", "kubectl"
    };

    [Theory]
    [MemberData(nameof(SystemTools))]
    public void Check_ASystemToolWithArguments_IsRefused_AndWithoutArguments_IsAllowed(string tool)
    {
        // Arrange - as the resolver hands back a System32 program (the folder does not matter, only the name)
        var path = $@"C:\Windows\System32\{tool}.exe";

        // Act
        var withArguments = CreateSut().Check(tool, path, ["/x"]);
        var bare = CreateSut().Check(tool, path, null);
        var empty = CreateSut().Check(tool, path, []);

        // Assert
        withArguments.Should().NotBeNull($"{tool} with arguments must be refused");
        withArguments!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        withArguments.Message.Should().Contain(path).And.Contain("does not run commands or scripts").And.Contain("Nothing was started");
        bare.Should().BeNull($"{tool} without arguments starts as usual");
        empty.Should().BeNull();
        InterpreterGuard.Interpreters.Should().Contain(tool);
    }

    [Theory]
    [InlineData("shutdown", "/s")]
    [InlineData("taskkill", "/F")]
    [InlineData("net1", "user")]
    [InlineData("psexec64", "-s")]
    [InlineData("regedt32", "/s")]
    [InlineData("manage-bde", "-off")]
    [InlineData("ssh", "host")]
    [InlineData("curl", "-T")]
    [InlineData("format", "C:")]
    public void Check_ASystemToolsName_IsReadRobustly(string tool, string argument)
    {
        string[] forms =
        [
            tool,
            tool.ToUpperInvariant(),
            $"{tool}.exe",
            $"{tool}.com",
            $@"C:\Windows\System32\{tool.ToUpperInvariant()}.COM",
            $"{tool.ToUpperInvariant()}.EXE",
            $@"C:\Windows\System32\{tool}.exe",
            $@"C:\WINDOWS\SYSTEM32\{tool.ToUpperInvariant()}.EXE",
            $@"C:\Windows\System32\{tool}",
            $"\"C:\\Windows\\System32\\{tool}.exe\"",
            $"C:/Windows/System32/{tool}.exe",
            $"file:///C:/Windows/System32/{tool}.exe",
            $@"C:\Windows\System32\{tool}.exe.",
            $@"C:\Windows\System32\{tool}.exe ",
            $@"C:\Windows\System32\{tool}.exe::$DATA"
        ];

        foreach (var form in forms)
        {
            // Act - the requested name alone, as when the resolver handed it back unchanged
            var refusal = CreateSut().Check(form, form, [argument]);

            // Assert
            refusal.Should().NotBeNull($"'{form}' with arguments must be refused");
            refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        }
    }

    [Theory]
    [InlineData("shutdown")]
    [InlineData("taskkill")]
    [InlineData("reg")]
    public void Check_ASystemToolBehindAnotherName_IsRefused_ByTheResolvedFile(string tool)
    {
        // Act - an App Paths entry may map any name to the tool
        var refusal = CreateSut().Check("helper", $@"C:\Windows\System32\{tool}.exe", ["/x"]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
    }

    [Fact]
    public void Check_AnEightDotThreeShortNameOfASystemTool_IsExpandedFirst()
    {
        // Arrange
        const string shortPath = @"C:\Windows\System32\MANAGE~1.EXE";
        _longPaths[shortPath] = @"C:\Windows\System32\manage-bde.exe";

        // Act
        var refusal = CreateSut().Check(shortPath, shortPath, ["-off", "C:"]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
    }

    [Theory]
    [InlineData("pip3.12", "pip")]
    [InlineData("lua54", "lua")]
    [InlineData("go1.22", "go")]
    [InlineData("psexec64", "psexec64")]
    [InlineData("net1", "net1")]
    [InlineData("regedt32", "regedt32")]
    public void Check_ADigitInAToolsName_IsMatchedAsTheToolOrItsVersion(string program, string shown)
    {
        // Act
        var refusal = CreateSut().Check(program, $@"C:\Tools\{program}.exe", ["x"]);

        // Assert - the entry that matched is the one the message names
        refusal!.Message.Should().Contain($"({shown})");
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\StarCraft II\Support64\SC2.exe")]
    [InlineData(@"C:\Games\at2.exe")]
    [InlineData(@"C:\Tools\net5.exe")]
    [InlineData(@"C:\Tools\nc7.exe")]
    [InlineData(@"C:\Tools\reg2.exe")]
    [InlineData(@"C:\Tools\taskkill2.exe")]
    [InlineData(@"C:\Tools\control3.exe")]
    public void Check_ASystemToolsNameWithDigitsAfterIt_IsAnotherProgram_AndAllowed(string program)
    {
        // Act - a version is read off only the names that ship versioned (shells, script hosts, runtimes, package managers)
        var refusal = CreateSut().Check(program, program, ["x"]);

        // Assert
        refusal.Should().BeNull();
    }

    [Theory]
    [InlineData("python3.12", "python")]
    [InlineData("lua54", "lua")]
    [InlineData("go1.22", "go")]
    [InlineData("pip3", "pip")]
    [InlineData("ubuntu2204", "ubuntu")]
    [InlineData("busybox64", "busybox")]
    [InlineData("AutoHotkey64", "autohotkey")]
    [InlineData("AutoHotkeyU64", "autohotkeyu")]
    [InlineData("AutoHotkeyA32", "autohotkeya")]
    public void Check_AVersionedRuntimeOrScriptHost_IsStillRefused(string program, string shown)
    {
        // Act
        var refusal = CreateSut().Check(program, $@"C:\Tools\{program}.exe", ["x"]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        refusal.Message.Should().Contain($"({shown})");
    }

    [Theory]
    [InlineData("mstsc", @"C:\Users\me\Downloads\work.rdp")]
    [InlineData("mstsc", "/v:attacker.example.com")]
    [InlineData("AutoHotkey64", @"C:\Users\me\Downloads\x.ahk")]
    [InlineData("AutoIt3", @"C:\Users\me\Downloads\x.au3")]
    [InlineData("AutoIt3_x64", @"C:\Users\me\Downloads\x.au3")]
    [InlineData("javaws", @"C:\Users\me\Downloads\app.jnlp")]
    [InlineData("VSTOInstaller", @"C:\Users\me\Downloads\addin.vsto")]
    [InlineData("AppInstaller", @"C:\Users\me\Downloads\app.msix")]
    [InlineData("provtool", @"C:\Users\me\Downloads\setup.ppkg")]
    [InlineData("keyhh", @"C:\Users\me\Downloads\help.chm")]
    [InlineData("msrdc", @"C:\Users\me\Downloads\work.rdp")]
    [InlineData("wusa", @"C:\Users\me\Downloads\kb.msu")]
    [InlineData("msdt", @"/cab C:\Users\me\Downloads\fix.diagcab")]
    public void Check_TheProgramARefusedFileTypeOpensWith_GivenTheFile_IsRefused(string handler, string argument)
    {
        // Act - naming the handler with the file must not get around refusing the file
        var refusal = CreateSut().Check(handler, $@"C:\Program Files\Handler\{handler}.exe", [argument]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
    }

    [Fact]
    public void Check_ControlWithArguments_PointsToSettingsPages()
    {
        // Act
        var refusal = CreateSut().Check("control", @"C:\Windows\System32\control.exe", ["printers"]);

        // Assert - the next step: a Settings page opens by its ms-settings: URI
        refusal!.Message.Should().Contain("ms-settings:");
    }

    [Theory]
    [InlineData(@"C:\Program Files\NetBeans\bin\netbeans64.exe")]
    [InlineData(@"C:\Program Files\Notepad++\notepad++.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\GitHubDesktop\GitHubDesktop.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\gitkraken\gitkraken.exe")]
    [InlineData(@"C:\Tools\network-monitor.exe")]
    [InlineData(@"C:\Tools\scanner.exe")]
    [InlineData(@"C:\Tools\regex-tester.exe")]
    [InlineData(@"C:\Tools\controller.exe")]
    public void Check_ProgramsWhoseNamesOnlyStartLikeATool_AreAllowed(string program)
    {
        // Act
        var refusal = CreateSut().Check(program, program, ["x"]);

        // Assert
        refusal.Should().BeNull();
    }

    [Theory]
    [InlineData("control", @"C:\Windows\System32\ncpa.cpl")]
    [InlineData("control", "printers")]
    [InlineData("mmc", @"C:\Users\me\Downloads\console.msc")]
    [InlineData("hh", @"C:\Users\me\Downloads\help.chm")]
    [InlineData("regedit", @"C:\Users\me\Downloads\settings.reg")]
    [InlineData("rundll32", "url.dll,FileProtocolHandler")]
    public void Check_AnOpenerThatIsAlsoAToolWithArguments_IsRefused(string opener, string argument)
    {
        // Act - refused for any arguments, so it never gets to the opener check (ScriptOpeners holds explorer alone)
        var refusal = CreateSut().Check(opener, $@"C:\Windows\System32\{opener}.exe", [argument]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
    }

    #endregion

    #region File types that run, install or open what they name (launch guard blocklist)

    /// <summary>The file types added to <see cref="InterpreterGuard.ScriptExtensions"/> by the launch guard blocklist review.</summary>
    public static readonly TheoryData<string> RefusedFileTypes = new()
    {
        ".lnk", ".url", ".website", ".pif", ".appref-ms", ".settingcontent-ms",
        ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".application", ".vsto", ".msu", ".ppkg", ".gadget", ".jnlp",
        ".reg", ".msc", ".chm", ".scr", ".wsc", ".sct", ".psc1", ".ahk", ".au3", ".diagcab", ".xll", ".rdp"
    };

    [Theory]
    [MemberData(nameof(RefusedFileTypes))]
    public void Check_AFileThatRunsOrInstallsWhatItNames_IsRefused_AsTheTarget(string extension)
    {
        // Arrange
        var file = $@"C:\Users\me\Downloads\thing{extension}";
        var upper = $@"C:\Users\me\Downloads\THING{extension.ToUpperInvariant()}";

        // Act
        var bare = CreateSut().Check(file, file, null);
        var withArguments = CreateSut().Check(file, file, ["x"]);
        var inUpperCase = CreateSut().Check(upper, upper, null);
        var asFileUri = CreateSut().Check($"file:///C:/Users/me/Downloads/thing{extension}", $"file:///C:/Users/me/Downloads/thing{extension}", null);

        // Assert
        bare.Should().NotBeNull($"a {extension} target must be refused");
        bare!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        bare.Message.Should().Contain(extension).And.Contain("Nothing was started");
        withArguments!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        inUpperCase!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        asFileUri!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        InterpreterGuard.ScriptExtensions.Should().Contain(extension);
    }

    [Theory]
    [MemberData(nameof(RefusedFileTypes))]
    public void Check_AFileThatRunsOrInstallsWhatItNames_IsRefused_AsWhatAnOpenerOpens(string extension)
    {
        var file = $@"C:\Users\me\Downloads\thing{extension}";

        // explorer opens its argument with the program registered for it
        var byExplorer = CreateSut().Check("explorer", @"C:\Windows\explorer.exe", [file]);
        byExplorer.Should().NotBeNull($"explorer given a {extension} must be refused");
        byExplorer!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        byExplorer.Message.Should().Contain(file);

        // The other openers are refused for any arguments
        foreach (var opener in new[] { "control", "mmc", "hh", "regedit", "rundll32" })
        {
            var refusal = CreateSut().Check(opener, $@"C:\Windows\System32\{opener}.exe", [file]);
            refusal.Should().NotBeNull($"{opener} given a {extension} must be refused");
        }
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\services.msc")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Accessories\Paint.lnk")]
    [InlineData(@"C:\Users\me\Desktop\Shared Folder.lnk")]
    public void Check_AShortcutOrConsoleTheAgentNames_IsRefused_WhereverItIs(string file)
    {
        // Act - the guard cannot see what a shortcut or a console runs, so the file is refused, not followed
        var refusal = CreateSut().Check(file, file, null);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
    }

    [Fact]
    public void Check_ANameThatAppPathsMapsToAShortcut_IsRefused()
    {
        // Act - App Paths may point a bare name at any file; a shortcut's target is not read, so the shortcut is refused
        var refusal = CreateSut().Check("mytool", @"C:\Tools\mytool.lnk", null);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        refusal.Message.Should().Contain("mytool").And.Contain(".lnk");
    }

    [Theory]
    [InlineData("notepad", @"C:\Windows\System32\notepad.exe", @"C:\Users\me\Documents\file.txt")]
    [InlineData("notepad", @"C:\Windows\System32\notepad.exe", @"C:\Users\me\Downloads\settings.reg")]
    [InlineData("mspaint", @"C:\Windows\System32\mspaint.exe", @"C:\Users\me\Pictures\x.png")]
    [InlineData("explorer", @"C:\Windows\explorer.exe", @"C:\some\folder")]
    [InlineData("explorer", @"C:\Windows\explorer.exe", @"/select,C:\Users\me\Documents\report.pdf")]
    [InlineData("msedge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "https://example.com")]
    [InlineData("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "https://example.com/setup.msix")]
    [InlineData("firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe", "https://example.com")]
    [InlineData("winword", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", @"C:\Users\me\Documents\x.docx")]
    [InlineData("excel", @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE", @"C:\Users\me\Documents\x.xlsx")]
    [InlineData("wordpad", @"C:\Program Files\Windows NT\Accessories\wordpad.exe", @"C:\Users\me\Documents\x.rtf")]
    [InlineData("code.exe", @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", @"C:\Projects\MyApp")]
    [InlineData("calc", @"C:\Windows\System32\calc.exe", "")]
    public void Check_AnOrdinaryApplicationWithAFileArgument_IsAllowed(string executable, string resolved, string argument)
    {
        // Act - the blocklist must not creep into the programs a user asks an agent to open a file with
        var refusal = CreateSut().Check(executable, resolved, [argument]);

        // Assert
        refusal.Should().BeNull();
    }

    #endregion

    #region Packaged apps and Applications folder entries (roadmap M6.5)

    [Theory]
    [InlineData("WindowsTerminal.exe", "Microsoft.WindowsTerminal_8wekyb3d8bbwe")]
    [InlineData("ubuntu.exe", "CanonicalGroupLimited.UbuntuonWindows_79rhkp1fndgsc")]
    [InlineData("pwsh.exe", "Microsoft.PowerShell_8wekyb3d8bbwe")]
    [InlineData("python3.12.exe", "PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0")]
    [InlineData("SomeLauncher.exe", "PythonSoftwareFoundation.Python.3.13_qbz5n2kfra8p0")]
    [InlineData("SomeLauncher.exe", "CanonicalGroupLimited.Ubuntu24.04LTS_79rhkp1fndgsc")]
    [InlineData("debian.exe", "TheDebianProject.DebianGNULinux_76v4gfsz19hv4")]
    [InlineData("App.exe", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe")]
    [InlineData("AppInstallerCLI.exe", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe")] // winget (review M6.5 #9)
    public void CheckApp_AnInterpreterPackageWithArguments_IsRefused(string program, string family)
    {
        // Act
        var refusal = InterpreterGuard.CheckApp($"{family}!App", program, family, ["-c", "echo"]);

        // Assert
        refusal.Should().NotBeNull();
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        refusal.Message.Should().Contain("Nothing was started");
    }

    [Fact]
    public void CheckApp_AnAppWhoseProgramIsNotKnown_IsRefusedWithArguments_FailClosed()
    {
        // Act
        var refusal = InterpreterGuard.CheckApp("Vendor.App_8wekyb3d8bbwe!App", program: null, "Vendor.App_8wekyb3d8bbwe", ["file.txt"]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        refusal.Message.Should().Contain("cannot tell which program");
    }

    [Theory]
    [InlineData("Notepad.exe", "Microsoft.WindowsNotepad_8wekyb3d8bbwe", true)]
    [InlineData(null, "Vendor.App_8wekyb3d8bbwe", false)]
    [InlineData("pwsh.exe", "Microsoft.PowerShell_8wekyb3d8bbwe", false)]
    public void CheckApp_WithoutArguments_OrAnotherProgram_IsAllowed(string? program, string family, bool withArguments)
    {
        // Act
        var refusal = InterpreterGuard.CheckApp($"{family}!App", program, family, withArguments ? [@"C:\notes\todo.txt"] : []);

        // Assert
        refusal.Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\Vendor\setup.hta", true)]
    [InlineData(@"C:\Vendor\run.bat", false)]
    [InlineData(@"C:\Vendor\tool.ps1", true)]
    [InlineData(@"C:\Vendor\package.msi", false)]
    public void CheckApp_AnEntryWhoseProgramIsAScript_IsRefused_WithOrWithoutArguments(string program, bool withArguments)
    {
        // Review M6.5 #3: the shell would run the script with its interpreter
        var refusal = InterpreterGuard.CheckApp("Vendor.Setup", program, null, withArguments ? ["x"] : []);

        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        refusal.Message.Should().Contain("Nothing was started");
    }

    [Fact]
    public void CheckApp_ADesktopAppEntry_IsCheckedByItsProgram()
    {
        // Act
        var refused = InterpreterGuard.CheckApp("PowerShell7", @"C:\Program Files\PowerShell\7\pwsh.exe", null, ["-c", "x"]);
        var allowed = InterpreterGuard.CheckApp("MSEdge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", null, ["https://example.com"]);

        // Assert
        refused!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        allowed.Should().BeNull();
    }

    #endregion
}
