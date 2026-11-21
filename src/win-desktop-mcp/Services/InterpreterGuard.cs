using System.Text.RegularExpressions;
using WinDesktopMcp.Models;
using WinDesktopMcp.Native;

namespace WinDesktopMcp.Services;

/// <summary>
/// Refuses the launches that would run a command or a script (roadmap M3.F8, the repo owner's choice for D-PAL-07): a
/// command interpreter, script host or other program that runs what its command line names, or a native tool whose
/// arguments destroy data, change the system, kill or control processes and services, or move data off the machine
/// (<see cref="Interpreters"/>), started with arguments; and a script, installer, shortcut or other file that Windows
/// hands to a program that runs, installs or opens what it names (<see cref="ScriptExtensions"/>), as the target or as
/// what explorer.exe is asked to open. The server does not run commands or scripts for the agent; the user runs them. A
/// program of the list started without arguments (an empty console, Registry Editor, Control Panel) is allowed.
/// </summary>
/// <remarks>
/// Both the name the agent asked for and the file the resolver found are checked, so an App Paths entry that maps
/// another name to an interpreter counts. A name is read the way Windows reads it: quotes, a file: URI, forward slashes,
/// letter case, a missing .exe, trailing dots and spaces, an alternate data stream (cmd.exe::$DATA) and an 8.3 short name
/// (POWERS~1.EXE, expanded from the file system) all lead to the same program. A version after the name counts as the
/// name (python3.12, ubuntu2204, pip3.12); a name that ends in digits itself (net1, psexec64, regedt32) is listed as it is.
/// <para>
/// A shortcut (.lnk, .url, .pif) the agent names, or an App Paths entry points to, is refused as a file, since its target
/// is not read here. An installed app launched by its name or its shell:AppsFolder entry is not affected: the server
/// starts or checks the program its Start menu shortcut points to (PKEY_Link_TargetParsingPath), never the .lnk, so that
/// program gets these checks. One whose shortcut opens a refused file type (Windows Tools' consoles open .msc files) is
/// refused like the file. A bare name never resolves to a shortcut (ExecutableResolver tries .com, .exe, .bat and .cmd).
/// </para>
/// <para>
/// This is a list of names, not a sandbox. It does not catch: a renamed copy of a listed program; a program missing from the
/// list that runs code, deletes data or sends it from its arguments (there are many, and a third-party tool on PATH can be
/// one); a document type associated with an interpreter by the user; a document whose program runs its macros or
/// add-ins (an Office file); a URI (the protocol allowlist decides those); a disk image (.iso, .vhdx), which mounts but
/// runs nothing by itself; and what the agent types into a console, or changes through a program's window, after starting
/// it without arguments. Programs a user opens a file with (Notepad, Paint, a browser, Office, explorer with a folder) are
/// not on the list and get their arguments.
/// </para>
/// </remarks>
public sealed partial class InterpreterGuard
{
    /// <summary>
    /// The programs refused with arguments, by file name without .exe, in lower case: command shells and terminals, script
    /// hosts, language runtimes and package managers, programs that load, run or open the code or file their command line
    /// names, and native tools whose arguments destroy data, change the system, kill or control processes and services, or
    /// move data off the machine (shutdown, taskkill, reg, sc, net, cipher, curl, robocopy, ssh, git, ...). The name
    /// predates the tools: the rule is "its arguments can run code or do damage", whatever the program is called.
    /// </summary>
    public static readonly IReadOnlySet<string> Interpreters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Command shells, consoles and terminals (WSL distributions start a shell too; wslconfig /unregister deletes one)
        "cmd", "powershell", "powershell_ise", "pwsh", "bash", "sh", "wsl", "wslg", "conhost", "wt", "windowsterminal",
        "ubuntu", "debian", "kali", "zsh", "fish", "nu", "ksh", "dash", "tcsh", "csh", "busybox", "mintty", "git-bash",
        "git-cmd", "wslconfig",

        // Script hosts
        "wscript", "cscript", "mshta", "scriptrunner",

        // Language runtimes and interactive compilers
        "python", "pythonw", "py", "pyw", "node", "deno", "java", "javaw", "ruby", "rubyw", "perl", "php", "dotnet", "bun",
        "go", "cargo", "julia", "lua", "rscript", "jshell", "csi", "fsi",

        // Package managers and containers, which install and run what their arguments name
        "winget", "choco", "pip", "pipx", "uv", "uvx", "conda", "pnpm", "docker", "podman", "kubectl",

        // System programs that load or run the code their arguments name
        "rundll32", "regsvr32", "msiexec", "cmstp", "msbuild", "installutil", "regasm", "regsvcs", "forfiles", "wmic",
        "schtasks", "pcalua", "runas", "psexec", "psexec64", "paexec", "at", "winrs", "msdt", "infdefaultinstall", "odbcconf",
        "mavinject", "presentationhost", "syncappvpublishingserver", "appvlp", "atbroker", "pcwrun", "tttracer",
        "register-cimprovider", "wuauclt",

        // Programs that open the file their arguments name (a .cpl, .msc, .chm or .reg) with the code inside it
        "mmc", "control", "hh",

        // Power and sessions
        "shutdown", "logoff", "tsdiscon", "rwinsta", "psshutdown",

        // Processes (kill_process has the protected-process list, the host guard, the target policy and confirmation)
        "taskkill", "tskill", "pskill", "pssuspend",

        // The registry
        "reg", "regedit", "regedt32", "regini",

        // Services, drivers, boot, network, accounts, policy and settings
        "sc", "net", "net1", "netsh", "route", "ipconfig", "pnputil", "bcdedit", "bcdboot", "reagentc", "dism", "wusa", "setx",
        "auditpol", "secedit", "powercfg", "cmdkey", "mpcmdrun",

        // Disks, files and permissions: wipe, format, encrypt, delete, overwrite or take over
        "cipher", "format", "diskpart", "mountvol", "fsutil", "vssadmin", "wbadmin", "wevtutil", "manage-bde", "takeown",
        "icacls", "cacls", "robocopy", "xcopy", "sdelete", "tar",

        // Download, upload and remote access (git runs commands from its arguments too: git -c alias.x=!cmd x)
        "curl", "wget", "certutil", "certreq", "bitsadmin", "ftp", "tftp", "finger", "esentutl", "expand", "extrac32", "replace",
        "cmdl32", "desktopimgdownldr", "ssh", "scp", "sftp", "sshd", "rsync", "plink", "pscp", "psftp", "nc", "ncat", "netcat",
        "rclone", "azcopy", "aws", "gh", "git"
    };

    /// <summary>
    /// The file types refused as a launch target, because Windows hands them to a program that runs, installs or opens what
    /// they name: batch files, PowerShell, Windows Script Host and HTML applications, Python, Java archives, the scripts of
    /// other runtimes; installers and packages (Windows Installer, MSIX and AppX, App Installer, ClickOnce, Office add-ins,
    /// updates, provisioning packages); shortcuts whose target the guard cannot see (.lnk, .url, .pif, settings shortcuts);
    /// registry files; Control Panel items, management consoles, compiled help, screen savers, troubleshooters and Remote
    /// Desktop connections.
    /// </summary>
    public static readonly IReadOnlySet<string> ScriptExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Scripts
        ".bat", ".cmd", ".ps1", ".psm1", ".psc1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".wsc", ".sct", ".hta", ".py",
        ".pyw", ".pyz", ".pyzw", ".jar", ".jnlp", ".sh", ".rb", ".pl", ".php", ".ahk", ".au3",

        // Installers and packages
        ".msi", ".msp", ".msu", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".application", ".appref-ms",
        ".vsto", ".ppkg", ".gadget", ".xll",

        // Shortcuts, whose target is not checked
        ".lnk", ".url", ".website", ".pif", ".settingcontent-ms",

        // Files a system program runs or applies: registry files, Control Panel items, consoles, help, screen savers,
        // troubleshooters, Remote Desktop connections (which can hand the drives to the remote computer)
        ".reg", ".cpl", ".msc", ".chm", ".scr", ".diagcab", ".rdp"
    };

    // Programs that open what their arguments name with the program registered for it, which runs a script. The other
    // openers (control, mmc, hh, regedit, rundll32) are in Interpreters, refused with any arguments
    private static readonly string[] ScriptOpeners = ["explorer"];

    private readonly Func<string, string?> _longPath;

    /// <summary>Expands 8.3 short names from the real file system.</summary>
    public InterpreterGuard()
        : this(LongPathOf)
    {
    }

    /// <param name="longPath">The long form of a fully qualified path that holds a '~', or null when it cannot be read.</param>
    public InterpreterGuard(Func<string, string?> longPath)
    {
        _longPath = longPath;
    }

    /// <summary>
    /// Why the launch must not be started, with its LaunchErrorCode name, or null when it may: the target is a script
    /// (ScriptFile), an interpreter given arguments (InterpreterWithArguments), or explorer.exe given a script (ScriptFile).
    /// </summary>
    /// <param name="executable">The executable as the agent asked for it.</param>
    /// <param name="resolvedFileName">What the resolver will hand to Windows: the program's full path, or the input unchanged.</param>
    /// <param name="arguments">The arguments; null or empty means none.</param>
    public InterpreterRefusal? Check(string executable, string resolvedFileName, IReadOnlyList<string>? arguments)
    {
        var names = new[] { FileNameOf(executable), FileNameOf(resolvedFileName) }.OfType<string>().ToList();
        var shown = string.Equals(executable.Trim(), resolvedFileName, StringComparison.OrdinalIgnoreCase)
            ? $"'{resolvedFileName}'"
            : $"'{executable}' ({resolvedFileName})";

        if (names.Select(name => Path.GetExtension(name)).FirstOrDefault(ScriptExtensions.Contains) is { } extension)
        {
            var batchHint = extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                ? " A batch file that only starts a program, such as VS Code's code.cmd, can be replaced by the program's .exe " +
                  "(for VS Code code.exe, which App Paths resolves, or its full path)."
                : string.Empty;
            return new InterpreterRefusal(nameof(LaunchErrorCode.ScriptFile),
                $"{shown} is a script, installer, shortcut or other file ({extension.ToLowerInvariant()}) that Windows would " +
                $"hand to a program that runs, installs or opens what it names, and launch_application does not run scripts: " +
                $"{NoCommands} Ask the user to run it.{batchHint} Nothing was started.");
        }

        if (arguments is not { Count: > 0 })
        {
            return null;
        }

        var programs = names.Select(ProgramName).ToList();
        if (programs.Select(InterpreterOf).OfType<string>().FirstOrDefault() is { } interpreter)
        {
            return new InterpreterRefusal(nameof(LaunchErrorCode.InterpreterWithArguments),
                $"{shown} is a command interpreter, script host or system tool whose arguments run code, change the system or " +
                $"send data ({interpreter}), and launch_application does not start one with arguments: {NoCommands} Ask the " +
                "user to run the command; do not type it into a console or terminal " +
                "either. Started without arguments it opens as usual. Nothing was started.");
        }

        if (programs.Any(program => ScriptOpeners.Contains(program)))
        {
            foreach (var argument in arguments)
            {
                if (FileNameOf(argument) is { } name && ScriptExtensions.Contains(Path.GetExtension(name)))
                {
                    return new InterpreterRefusal(nameof(LaunchErrorCode.ScriptFile),
                        $"{shown} would open '{argument}', a script, installer, shortcut or other file " +
                        $"({Path.GetExtension(name).ToLowerInvariant()}), with the program registered for it, which runs, " +
                        $"installs or opens what it names, and launch_application does not run scripts: {NoCommands} " +
                        "Ask the user to run it. Nothing was started.");
                }
            }
        }

        return null;
    }

    private const string NoCommands = "the server does not run commands or scripts for the agent.";

    /// <summary>
    /// The package families whose apps are shells, terminals or language runtimes (roadmap M6.5): PowerShell, Python, the
    /// WSL distributions and Windows Terminal from the Store, matched by the start of the package family name. A backstop for
    /// <see cref="Interpreters"/>, which the app's program is checked against first.
    /// </summary>
    public static readonly IReadOnlyList<string> InterpreterPackagePrefixes =
    [
        "Microsoft.PowerShell", "PythonSoftwareFoundation.Python.", "CanonicalGroupLimited.", "TheDebianProject.", "KaliLinux.",
        "46932SUSE.", "WhitewaterFoundryLtd.", "Microsoft.WindowsTerminal", "Microsoft.DesktopAppInstaller",
        "MicrosoftCorporationII.WindowsSubsystemForLinux"
    ];

    /// <summary>
    /// Why an app of the Applications folder must not be started with these arguments (roadmap M6.5), or null when it may:
    /// a packaged app activated by its AppUserModelID, or a shell:AppsFolder entry, given arguments, whose program is an
    /// interpreter (<see cref="Interpreters"/>), whose package is one (<see cref="InterpreterPackagePrefixes"/>), or whose
    /// program cannot be told (fail closed: the server cannot rule out an interpreter). Without arguments any app starts.
    /// </summary>
    /// <param name="appId">The AppUserModelID, as the refusal shows it.</param>
    /// <param name="program">The app's program: a packaged app's executable file name from its manifest, or a desktop app's
    /// path; null when it is not known.</param>
    /// <param name="packageFamilyName">The package family of a packaged app; null for a desktop app.</param>
    /// <param name="arguments">The arguments; null or empty means none.</param>
    public static InterpreterRefusal? CheckApp(string appId, string? program, string? packageFamilyName, IReadOnlyList<string>? arguments)
    {
        ArgumentNullException.ThrowIfNull(appId);

        // Review M6.5 #3: an entry whose shortcut starts a script runs it, with or without arguments
        if (!string.IsNullOrWhiteSpace(program) && ScriptExtensions.Contains(Path.GetExtension(program.Trim().TrimEnd('.', ' '))))
        {
            var extension = Path.GetExtension(program.Trim().TrimEnd('.', ' ')).ToLowerInvariant();
            return new InterpreterRefusal(nameof(LaunchErrorCode.ScriptFile),
                $"The app '{appId}' starts a script, installer, shortcut or other file ({extension}) that Windows would hand " +
                "to a program that runs, installs or opens what it names, and " +
                $"launch_application does not run scripts: {NoCommands} Ask the user to run it. Nothing was started.");
        }

        if (arguments is not { Count: > 0 })
            return null;

        if (string.IsNullOrWhiteSpace(program))
        {
            return new InterpreterRefusal(nameof(LaunchErrorCode.InterpreterWithArguments),
                $"The server cannot tell which program the app '{appId}' runs, so it cannot rule out a command interpreter, and " +
                $"launch_application does not pass arguments to it: {NoCommands} Launch it without arguments, or launch the " +
                "program's .exe by its path. Nothing was started.");
        }

        var name = program.Replace('/', '\\');
        name = name[(name.LastIndexOf('\\') + 1)..];
        var interpreter = InterpreterOf(ProgramName(name)) ??
                          (packageFamilyName != null &&
                           InterpreterPackagePrefixes.Any(prefix => packageFamilyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                              ? packageFamilyName
                              : null);
        return interpreter == null
            ? null
            : new InterpreterRefusal(nameof(LaunchErrorCode.InterpreterWithArguments),
                $"The app '{appId}' is a command interpreter, terminal, language runtime or system tool whose arguments run " +
                $"code, change the system or send data ({interpreter}), and launch_application does not start one with arguments: {NoCommands} Ask the user to run the command; do not type it into a console " +
                "or terminal either. Started without arguments it opens as usual. Nothing was started.");
    }

    /// <summary>
    /// The file name a path, a quoted path or a file: URI ends in, as Windows reads it (8.3 short names expanded, an
    /// alternate data stream and trailing dots and spaces left out); null for another URI or an empty value.
    /// </summary>
    private string? FileNameOf(string value)
    {
        var text = value.Trim().Trim('"').Trim();
        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsFile)
                    return null;
                text = uri.LocalPath;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
        else if (UriScheme().IsMatch(text))
        {
            return null;
        }

        text = text.Replace('/', '\\');
        if (text.Contains('~') && IsFullyQualified(text))
        {
            try
            {
                text = _longPath(text) ?? text;
            }
            catch (Exception)
            {
                // Unreadable: keep the short name, which a missing file also keeps from starting
            }
        }

        // A drive without a separator (C:cmd.exe) is not an alternate data stream
        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':')
            text = text[2..];

        var name = text[(text.LastIndexOf('\\') + 1)..];
        var stream = name.IndexOf(':');
        if (stream >= 0)
            name = name[..stream];

        name = name.TrimEnd('.', ' ');
        return name.Length == 0 ? null : name;
    }

    /// <summary>A file name without .exe or .com, in lower case.</summary>
    private static string ProgramName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var name = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".com", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^extension.Length]
            : fileName;
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// The entry of <see cref="Interpreters"/> a program name stands for, by itself (rundll32) or without a version after
    /// it (python3.12, ubuntu2204); null for any other program.
    /// </summary>
    private static string? InterpreterOf(string programName)
    {
        if (Interpreters.Contains(programName))
            return programName;

        var unversioned = programName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.');
        return unversioned.Length > 0 && Interpreters.Contains(unversioned) ? unversioned : null;
    }

    private static bool IsFullyQualified(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The long form of <paramref name="path"/> from the file system (GetLongPathNameW), or null when it cannot be read (the
    /// path does not exist). <see cref="ChromiumSwitchGuard"/> expands a --user-data-dir's 8.3 short names with it too.
    /// </summary>
    internal static string? LongPathOf(string path)
    {
        var buffer = new char[260];
        var length = NativeMethods.Kernel32.GetLongPathNameW(path, buffer, (uint)buffer.Length);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = NativeMethods.Kernel32.GetLongPathNameW(path, buffer, (uint)buffer.Length);
        }

        return length == 0 || length > buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    // A URI scheme of two or more characters, so that a drive letter (C:) is not one
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]+:")]
    private static partial Regex UriScheme();
}

/// <summary>Why a launch was refused by <see cref="InterpreterGuard"/> (roadmap M3.F8).</summary>
/// <param name="ErrorCode">The LaunchErrorCode name: InterpreterWithArguments or ScriptFile.</param>
/// <param name="Message">What was refused, why, and what to do instead.</param>
public sealed record InterpreterRefusal(string ErrorCode, string Message);
