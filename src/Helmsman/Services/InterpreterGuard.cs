using System.Text.RegularExpressions;
using Helmsman.Models;
using Helmsman.Native;

namespace Helmsman.Services;

/// <summary>
/// Refuses the launches that would run a command or a script (roadmap M3.F8, the repo owner's choice for D-PAL-07): a
/// command interpreter, script host or other program that runs what its command line names, or a native tool whose
/// arguments destroy data, change the system, kill or control processes and services, or move data off the machine
/// (<see cref="Interpreters"/>), started with arguments; and a script, installer, shortcut or other file that Windows
/// hands to a program that runs, installs or opens what it names (<see cref="ScriptExtensions"/>), as the target or as
/// what explorer.exe is asked to open. The server does not run commands or scripts for the agent; the user runs them. A
/// program of the list started without arguments (an empty console, Registry Editor, Control Panel) is allowed. One file
/// type has an exception: Windows' own management consoles (services.msc, devmgmt.msc, eventvwr.msc), an existing .msc file
/// directly in System32, which only administrators can write to, open without arguments; any other .msc stays refused,
/// since a console file can make MMC run commands.
/// </summary>
/// <remarks>
/// Both the name the agent asked for and the file the resolver found are checked, so an App Paths entry that maps
/// another name to an interpreter counts. A name is read the way Windows reads it: quotes, a file: URI, forward slashes,
/// letter case, a missing .exe, trailing dots and spaces, an alternate data stream (cmd.exe::$DATA) and an 8.3 short name
/// (POWERS~1.EXE, expanded from the file system) all lead to the same program. For a shell, script host, runtime or
/// package manager a version after the name counts as the name (python3.12, ubuntu2204, pip3.12); every other program is
/// matched by its exact name, so SC2.exe is not sc, and a name that ends in digits itself (net1, psexec64, regedt32, nc64)
/// is listed as it is.
/// <para>
/// A shortcut (.lnk, .url, .pif) the agent names, or an App Paths entry points to, is refused as a file, since its target
/// is not read here. An installed app launched by its name or its shell:AppsFolder entry is not affected: the server
/// starts or checks the program its Start menu shortcut points to (PKEY_Link_TargetParsingPath), never the .lnk, so that
/// program gets these checks. One whose shortcut opens a refused file type is refused like the file; Windows Tools'
/// consoles, launched by their name, open the System32 .msc file their shortcut points to, as that file would by its path
/// (a shell:AppsFolder entry that opens a .msc stays refused, since the shell, not the server, picks the file it opens).
/// The file-system search for a bare name never finds a shortcut (ExecutableResolver tries .com,
/// .exe, .bat and .cmd); only an App Paths entry can resolve a bare name to one.
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
    // The programs that ship with a version after their name (python3.12, lua54, go1.22, ubuntu2204, pip3.12,
    // AutoHotkeyU64): matched by themselves or with digits and dots after them
    private static readonly string[] VersionedPrograms =
    [
        // Command shells, consoles and terminals (WSL distributions start a shell too; wslconfig /unregister deletes one)
        "cmd", "powershell", "powershell_ise", "pwsh", "bash", "sh", "wsl", "wslg", "conhost", "wt", "windowsterminal",
        "ubuntu", "debian", "kali", "zsh", "fish", "nu", "ksh", "dash", "tcsh", "csh", "busybox", "mintty", "git-bash",
        "git-cmd", "wslconfig",

        // Script hosts (AutoHotkey's and AutoIt's builds are named apart: AutoHotkeyU64 is autohotkeyu, AutoIt3_x64 itself)
        "wscript", "cscript", "mshta", "scriptrunner", "autohotkey", "autohotkeyu", "autohotkeya", "autohotkeyux", "autoit3",
        "autoit3_x64",

        // Language runtimes and interactive compilers
        "python", "pythonw", "py", "pyw", "node", "deno", "java", "javaw", "ruby", "rubyw", "perl", "php", "dotnet", "bun",
        "go", "cargo", "julia", "lua", "rscript", "jshell", "csi", "fsi",

        // Package managers, which install and run what their arguments name
        "winget", "choco", "pip", "pipx", "uv", "uvx", "conda", "pnpm"
    ];

    // The other programs, matched by their exact name only, so that SC2.exe or nc7.exe is not taken for sc or nc; a
    // 64-bit build is listed by its own name (psexec64, nc64)
    private static readonly string[] ExactPrograms =
    [
        // Containers
        "docker", "podman", "kubectl",

        // System programs that load or run the code their arguments name
        "rundll32", "regsvr32", "msiexec", "cmstp", "msbuild", "installutil", "regasm", "regsvcs", "forfiles", "wmic",
        "schtasks", "pcalua", "runas", "psexec", "psexec64", "paexec", "at", "winrs", "msdt", "infdefaultinstall", "odbcconf",
        "mavinject", "presentationhost", "syncappvpublishingserver", "appvlp", "atbroker", "pcwrun", "tttracer",
        "register-cimprovider", "wuauclt", "wlrmdr",

        // Programs that open the file their arguments name with the code inside it, so that naming them with a refused file
        // type (control x.cpl, mmc x.msc, hh or keyhh x.chm, mstsc or msrdc x.rdp, javaws x.jnlp, VSTOInstaller x.vsto,
        // AppInstaller x.msix, provtool x.ppkg) does not get around refusing the file; mstsc /v:host also hands the drives
        // to another computer
        "mmc", "control", "hh", "keyhh", "mstsc", "msrdc", "javaws", "vstoinstaller", "appinstaller", "provtool",

        // Power and sessions
        "shutdown", "logoff", "tsdiscon", "tscon", "rwinsta", "psshutdown", "psshutdown64",

        // Processes (kill_process has the protected-process list, the host guard, the target policy and confirmation)
        "taskkill", "tskill", "pskill", "pskill64", "pssuspend", "pssuspend64",

        // The registry
        "reg", "regedit", "regedt32", "regini",

        // Services, drivers, filters, boot, network, accounts, device join, policy and settings
        "sc", "net", "net1", "netsh", "route", "ipconfig", "pnputil", "fltmc", "bcdedit", "bcdboot", "reagentc", "dism", "wusa",
        "setx", "auditpol", "secedit", "powercfg", "cmdkey", "mpcmdrun", "dsregcmd",

        // Disks, files and permissions: wipe, format, convert, encrypt, delete, overwrite or take over
        "cipher", "format", "diskpart", "mountvol", "fsutil", "chkdsk", "convert", "mbr2gpt", "vssadmin", "wbadmin", "wevtutil",
        "manage-bde", "takeown", "icacls", "cacls", "robocopy", "xcopy", "sdelete", "sdelete64", "tar",

        // Download, upload and remote access (git runs commands from its arguments too: git -c alias.x=!cmd x)
        "curl", "wget", "certutil", "certreq", "bitsadmin", "ftp", "tftp", "finger", "esentutl", "expand", "extrac32", "replace",
        "cmdl32", "desktopimgdownldr", "ssh", "scp", "sftp", "sshd", "rsync", "plink", "pscp", "psftp", "nc", "nc64", "ncat",
        "netcat", "rclone", "azcopy", "aws", "gh", "git"
    ];

    /// <summary>
    /// The programs refused with arguments, by file name without .exe, in lower case: command shells and terminals, script
    /// hosts, language runtimes and package managers, programs that load, run or open the code or file their command line
    /// names, and native tools whose arguments destroy data, change the system, kill or control processes and services, or
    /// move data off the machine (shutdown, taskkill, reg, sc, net, cipher, curl, robocopy, ssh, git, ...). The name
    /// predates the tools: the rule is "its arguments can run code or do damage", whatever the program is called. Shells,
    /// script hosts, runtimes and package managers also match with a version after the name; the rest only exactly.
    /// </summary>
    public static readonly IReadOnlySet<string> Interpreters =
        new HashSet<string>(VersionedPrograms.Concat(ExactPrograms), StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Versioned = new(VersionedPrograms, StringComparer.OrdinalIgnoreCase);

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

    /// <summary>The management console type: refused, except Windows' own consoles (<see cref="IsSystemConsole"/>).</summary>
    private const string ConsoleExtension = ".msc";

    private readonly Func<string, string?> _longPath;
    private readonly Func<string, bool> _fileExists;
    private readonly string _systemDirectory;

    /// <summary>Expands 8.3 short names from, and looks for Windows' consoles in, the real file system and System32.</summary>
    public InterpreterGuard()
        : this(LongPathOf, File.Exists, Environment.SystemDirectory)
    {
    }

    /// <param name="longPath">The long form of a fully qualified path that holds a '~', or null when it cannot be read.</param>
    /// <param name="fileExists">Whether a file exists at a fully qualified path (for Windows' own consoles).</param>
    /// <param name="systemDirectory">The System32 folder (Environment.SystemDirectory), the only folder whose .msc consoles
    /// are opened: only administrators can write to it.</param>
    public InterpreterGuard(Func<string, string?> longPath, Func<string, bool> fileExists, string systemDirectory)
    {
        ArgumentNullException.ThrowIfNull(longPath);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDirectory);
        _longPath = longPath;
        _fileExists = fileExists;
        _systemDirectory = Path.TrimEndingDirectorySeparator(systemDirectory.Trim());
    }

    /// <summary>
    /// Why the launch must not be started, with its LaunchErrorCode name, or null when it may: the target is a script
    /// (ScriptFile), an interpreter given arguments (InterpreterWithArguments), or explorer.exe given a script (ScriptFile).
    /// A .msc target passes only when <paramref name="resolvedFileName"/>, the text ShellExecute is handed, is one of
    /// Windows' own consoles in its plain full form (<see cref="IsSystemConsole"/>) and there are no arguments.
    /// </summary>
    /// <param name="executable">The executable as the agent asked for it.</param>
    /// <param name="resolvedFileName">What the resolver will hand to Windows: the program's full path, or the input unchanged.</param>
    /// <param name="arguments">The arguments; null or empty means none.</param>
    public InterpreterRefusal? Check(string executable, string resolvedFileName, IReadOnlyList<string>? arguments) =>
        Check(executable, resolvedFileName, arguments, out _);

    /// <summary>
    /// <see cref="Check(string, string, IReadOnlyList{string}?)"/>, telling also whether the launch it allowed opens one of
    /// Windows' own consoles, so that the launch can start it in its own folder whatever working directory was asked for.
    /// </summary>
    /// <param name="systemConsole">True when the launch is allowed and opens a Windows console (<see cref="IsSystemConsole"/>);
    /// false when it is refused or opens anything else.</param>
    public InterpreterRefusal? Check(
        string executable, string resolvedFileName, IReadOnlyList<string>? arguments, out bool systemConsole)
    {
        systemConsole = false;
        var names = new[] { FileNameOf(executable), FileNameOf(resolvedFileName) }.OfType<string>().ToList();
        var shown = string.Equals(executable.Trim(), resolvedFileName, StringComparison.OrdinalIgnoreCase)
            ? $"'{resolvedFileName}'"
            : $"'{executable}' ({resolvedFileName})";

        var refusedTypes = names.Select(name => Path.GetExtension(name)).Where(ScriptExtensions.Contains).ToList();
        if (refusedTypes.Count > 0 && refusedTypes.All(type => type.Equals(ConsoleExtension, StringComparison.OrdinalIgnoreCase)))
        {
            if (!IsSystemConsole(resolvedFileName))
            {
                // A console name that App Paths maps to another file (x.msc to an .exe) is named as what it resolves to
                var resolvesToConsole = FileNameOf(resolvedFileName) is { } resolvedName &&
                                        Path.GetExtension(resolvedName).Equals(ConsoleExtension, StringComparison.OrdinalIgnoreCase);
                var what = resolvesToConsole
                    ? $"{shown} is a management console file (.msc) that is not one of Windows' own consoles"
                    : $"'{executable}' is named as a management console file (.msc) but resolves to '{resolvedFileName}', which " +
                      "is not one of Windows' own consoles";
                return new InterpreterRefusal(nameof(LaunchErrorCode.ScriptFile),
                    $"{what}, and a console file can make MMC run commands: {NoCommands} Only the consoles in the Windows " +
                    $"system folder ({_systemDirectory}) can be opened, by their name (services.msc, devmgmt.msc, eventvwr.msc) " +
                    "or full path. Ask the user to open any other. Nothing was started.");
            }

            if (arguments is { Count: > 0 })
            {
                return new InterpreterRefusal(nameof(LaunchErrorCode.ScriptFile),
                    $"{shown} is a Windows console, which launch_application opens only without arguments, since MMC's " +
                    $"arguments can load another console or snap-in: {NoCommands} Launch it without arguments. Nothing was started.");
            }

            // Windows' own console, without arguments: ShellExecute opens exactly this file (the resolved path) with MMC
            systemConsole = true;
            return null;
        }

        if (refusedTypes.FirstOrDefault() is { } extension)
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
                "user to run the command; do not type it into a console or terminal either. Started without arguments it opens " +
                "as usual." +
                (interpreter == "control" ? " For a Settings page, launch its ms-settings: URI instead (ms-settings:printers)." : "") +
                (interpreter == "mmc"
                    ? " To open a Windows console, launch its .msc file itself, by its name and without arguments (services.msc, " +
                      "devmgmt.msc, eventvwr.msc); only the consoles in the Windows system folder open."
                    : "") +
                " Nothing was started.");
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
    /// Whether <paramref name="path"/>, as ShellExecute will be handed it, is one of Windows' own consoles: an existing .msc
    /// file directly in the System32 folder, which only administrators can write to. The path must already be in its plain
    /// full form, so that the text judged is the file opened: a drive path whose folder is the System32 folder (in any letter
    /// case), not a sub-folder, SysWOW64 or a look-alike prefix (System32evil). Every other form is refused, even one that
    /// would lead to the same file: a relative path, '..' or '.' segments, doubled or forward slashes, a \\?\, \\.\ or UNC
    /// path, a file: URI, quotes, an 8.3 short name ('~'), an alternate data stream (':'), or a trailing dot or space.
    /// </summary>
    private bool IsSystemConsole(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path != path.Trim() || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.IndexOfAny(['/', '~', '"']) >= 0)
            return false;

        try
        {
            if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal))
                return false;

            var name = Path.GetFileName(path);
            if (name.Length == 0 || name.IndexOf(':') >= 0 || name.EndsWith('.') || name.EndsWith(' ') ||
                !Path.GetExtension(name).Equals(ConsoleExtension, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(path), _systemDirectory, StringComparison.OrdinalIgnoreCase))
                return false;

            return _fileExists(path);
        }
        catch (Exception)
        {
            // A path that cannot be read, or a file system that cannot be asked: not shown to be a system console
            return false;
        }
    }

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
    /// The entry of <see cref="Interpreters"/> a program name stands for, by itself (rundll32) or, for a shell, script host,
    /// runtime or package manager, without a version after it (python3.12, ubuntu2204); null for any other program (SC2 is
    /// not sc).
    /// </summary>
    private static string? InterpreterOf(string programName)
    {
        if (Interpreters.Contains(programName))
            return programName;

        var unversioned = programName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.');
        return unversioned.Length > 0 && Versioned.Contains(unversioned) ? unversioned : null;
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
