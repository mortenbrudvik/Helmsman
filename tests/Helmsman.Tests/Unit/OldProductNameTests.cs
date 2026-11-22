using System.Text.RegularExpressions;

namespace Helmsman.Tests.Unit;

/// <summary>
/// The product is Helmsman. It was called Windows App Manager (windows-app-manager, WindowsAppManager) up to release
/// 3.2.0, and win-desktop-mcp (WinDesktopMcp) on main after that. This keeps both old names from creeping back into
/// what ships or what a user copies: the server's source, the plugin manifests, the skill, the scripts, the workflows,
/// the Claude Code commands, the sample presets (config), Directory.Build.props, README.md, and docs/TOOLS.md above its
/// changelog. The text files are read from disk (bin and obj folders left out) as a whole, so a name broken over two
/// lines is found too, and every occurrence of an old name must be excused by an entry of that name's allow-list below,
/// each with its reason. History documents (PLANNING.md, docs/AUTOMATION-ROADMAP.md, the changelog of docs/TOOLS.md)
/// and the tests are not checked. The same holds for the names that came with the first one and went in release 4.0.0:
/// the settings' <c>WAM_</c> prefix (now <c>HELMSMAN_</c>; the server still reads an old name when the new one is not set,
/// SettingNames) and the resources' <c>wam://</c> scheme (now <c>helmsman://</c>). Those two are also kept out of
/// TESTING.md and the Error Codes section of docs/TOOLS.md. These tests only read files in the repository.
/// </summary>
public partial class OldProductNameTests
{
    /// <summary>Any spelling of the first name: Windows App Manager, windows-app-manager, WindowsAppManager, windows_app_manager, across a line break.</summary>
    [GeneratedRegex(@"windows[\s_-]*app[\s_-]*manager", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsAppManager();

    /// <summary>Any spelling of the second name: win-desktop-mcp, WinDesktopMcp, win_desktop_mcp, Win Desktop MCP, across a line break.</summary>
    [GeneratedRegex(@"win[\s_-]*desktop[\s_-]*mcp", RegexOptions.IgnoreCase)]
    private static partial Regex WinDesktopMcp();

    /// <summary>
    /// A setting's name before 4.0.0: WAM_ and a letter (WAM_TOOLSETS), in any case, since Windows reads environment names
    /// case-insensitively ($env:wam_read_only works), and with the underscore escaped as Markdown writes it (WAM\_TOOLSETS);
    /// not inside another word (swam_, a WAMP_ name) and not the WamFixtureApp test names.
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])wam\\?_[a-z]", RegexOptions.IgnoreCase)]
    private static partial Regex OldSettingName();

    /// <summary>The resources' scheme before 4.0.0.</summary>
    [GeneratedRegex(@"wam://", RegexOptions.IgnoreCase)]
    private static partial Regex OldResourceScheme();

    /// <summary>The folders whose text files are checked, relative to the repository root.</summary>
    private static readonly string[] CheckedFolders =
        ["src", ".claude-plugin", "skills", "scripts", ".github", Path.Combine(".claude", "commands"), "config"];

    /// <summary>Single files checked whole.</summary>
    private static readonly string[] CheckedRootFiles = ["README.md", "Directory.Build.props"];

    /// <summary>docs/TOOLS.md is checked up to this heading; the changelog below it records what was true at the time.</summary>
    private const string ToolsReference = "docs/TOOLS.md";

    private const string ToolsChangelogHeading = "## Changes for existing clients";

    /// <summary>The section of docs/TOOLS.md after its changelog that is current text; checked for the 4.0.0 names only.</summary>
    private const string ToolsErrorCodesHeading = "## Error Codes";

    /// <summary>The key of the Error Codes section of docs/TOOLS.md among the checked texts.</summary>
    private const string ToolsErrorCodes = "docs/TOOLS.md#error-codes";

    /// <summary>The test guide, checked whole for the 4.0.0 names only (it still tells of the product's earlier names).</summary>
    private const string TestingGuide = "TESTING.md";

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".json", ".md", ".ps1", ".psm1", ".yml", ".yaml", ".txt", ".xml",
        ".manifest", ".config", ".editorconfig", ".resx",
    };

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj" };

    private const string LayoutStorageOptionsFile = "src/Helmsman/Services/LayoutStorageOptions.cs";

    /// <summary>
    /// Text that may keep the first name, by file (a path relative to the repository root, '/' separated; null for every
    /// checked file) and pattern. An occurrence is excused when it lies inside a match of an entry for its file. The
    /// entries name the old forms with the words around them, so the same old id written where the new one belongs
    /// still fails.
    /// </summary>
    private static readonly AllowedOldName[] WindowsAppManagerAllowList =
    [
        // Migration sources: 3.x kept presets in %LOCALAPPDATA%\WindowsAppManagerMcp\layouts and installed the plugin
        // as windows-app-manager@windows-app-manager-marketplace; the server copies presets from both.
        new(LayoutStorageOptionsFile, @"OriginalAppDataFolderName = ""WindowsAppManagerMcp""",
            "the 3.x AppData folder, a migration source"),
        new(LayoutStorageOptionsFile, @"PreviousPluginFolderName = ""windows-app-manager""",
            "the 3.x plugin cache folder, a migration source"),
        new(LayoutStorageOptionsFile, @"PreviousMarketplaceFolderName = ""windows-app-manager-marketplace""",
            "the 3.x marketplace cache folder, a migration source"),
        new(LayoutStorageOptionsFile, @"<c>%LOCALAPPDATA%\\WindowsAppManagerMcp\\layouts</c>, are copied once",
            "the comment on the earlier AppData folders"),
        new(LayoutStorageOptionsFile, @"of release 3\.2\.0 and older \(WindowsAppManagerMcp\.exe\)",
            "the comment on the 3.x AppData folder"),
        new(LayoutStorageOptionsFile, @"windows-app-manager 3\.2\.0 plugin whose exe was",
            "the comment on the AppData folder of the build published on main, which was still the 3.x plugin"),
        new(LayoutStorageOptionsFile,
            @"renamed it to <see cref=""PluginFolderName""/>\s+///\s+\(<c>\.\.\.\\cache\\windows-app-manager-marketplace\\windows-app-manager\\&lt;version&gt;</c>\)",
            "the comment on the 3.x plugin cache folder"),

        // README's "Upgrading from 3.x" must name the old ids a 3.x user has to remove or replace, and the folders and
        // exe they have on disk; the layout section names the old AppData folder presets are copied from.
        new("README.md", @"renamed the product from Windows App Manager to Helmsman", "the upgrade notes' first line"),
        new("README.md", @"plugin uninstall windows-app-manager@windows-app-manager-marketplace`",
            "the upgrade notes: uninstall the 3.x plugin (in a session and from the shell)"),
        new("README.md", @"plugin marketplace remove windows-app-manager-marketplace`",
            "the upgrade notes: remove the 3.x marketplace (in a session and from the shell)"),
        new("README.md", @"so `mcp__plugin_windows-app-manager_windows-app-manager__find_windows` is now",
            "the upgrade notes: the old tool prefix, before the new one"),
        new("README.md", @"names a\s+`mcp__plugin_windows-app-manager_windows-app-manager__\.\.\.` tool",
            "the upgrade notes: the saved references to update"),
        new("README.md", @"not `using-windows-app-manager`", "the upgrade notes: the 3.x skill name"),
        new("README.md", @"as `helmsman` instead of `windows-app-manager`", "the upgrade notes: the 3.x serverInfo.name"),
        new("README.md", @"3\.x shipped `WindowsAppManagerMcp\.exe`", "the upgrade notes: the 3.x exe a hand-written config points at"),
        new("README.md", @"A preset changed or added in `%LOCALAPPDATA%\\WindowsAppManagerMcp\\layouts` after\s+that build's first run",
            "the upgrade notes: the 3.x AppData folder, whose later changes are not copied when the build from main's folder is used"),
        new("README.md", @"From 3\.x: otherwise, when `%LOCALAPPDATA%\\WindowsAppManagerMcp\\layouts` has been used",
            "the upgrade notes: the 3.x AppData folder presets are copied from"),
        new("README.md", @"\(in\s+`%LOCALAPPDATA%\\WindowsAppManagerMcp` and `%TEMP%\\WindowsAppManagerMcp` for 3\.x,",
            "the upgrade notes: the 3.x folders that keep what is not carried over"),
        new("README.md", @"still starts\s+`WindowsAppManagerMcp\.exe` or `win-desktop-mcp\.exe`",
            "the upgrade notes: the 3.x exe a client that would not share the pause still starts"),
        new("README.md", @"`%LOCALAPPDATA%\\WindowsAppManagerMcp\\layouts`\) are copied there and the originals are left in place",
            "the layout presets: the 3.x AppData folder presets are copied from"),

        // /update-plugin sends someone who has only the 3.x plugin installed to the upgrade notes.
        new(".claude/commands/update-plugin.md", @"If only the 3\.x plugin, `windows-app-manager@windows-app-manager-marketplace`, is",
            "the 3.x plugin id"),
    ];

    /// <summary>
    /// Text that may keep the second name, as <see cref="WindowsAppManagerAllowList"/>. The plugin was never published
    /// as win-desktop-mcp@win-desktop-mcp-marketplace, so only the exe and the AppData and temporary folders of the build
    /// published on main are left, which a user of that build has on disk.
    /// </summary>
    private static readonly AllowedOldName[] WinDesktopMcpAllowList =
    [
        // Migration source: the build published on main kept presets in %LOCALAPPDATA%\win-desktop-mcp\layouts.
        new(LayoutStorageOptionsFile, @"PreviousAppDataFolderName = ""win-desktop-mcp""",
            "the AppData folder of the build published on main, a migration source"),
        new(LayoutStorageOptionsFile, @"Presets saved under the earlier folder names, <c>%LOCALAPPDATA%\\win-desktop-mcp\\layouts</c> and",
            "the comment on the earlier AppData folders"),
        new(LayoutStorageOptionsFile, @"plugin whose exe was win-desktop-mcp\.exe\.",
            "the comment on the AppData folder of the build published on main"),

        // The layout section names the earlier AppData folders presets are copied from.
        new("README.md", @"or in `%LOCALAPPDATA%\\win-desktop-mcp\\layouts` or `%LOCALAPPDATA%\\WindowsAppManagerMcp\\layouts`\) " +
                         @"are copied there and the originals are left in place",
            "the layout presets: the AppData folder of the build published on main, which presets are copied from"),

        // README's "Upgrading from 3.x" tells a user of that build which exe and folders on their disk are its.
        new("README.md", @"whose exe was\s+`win-desktop-mcp\.exe`: its plugin, marketplace",
            "the upgrade notes: the exe of the build published on main, whose plugin had the 3.x names"),
        new("README.md", @"and a build from main after 3\.2\.0 was `win-desktop-mcp\.exe`",
            "the upgrade notes: the exe a hand-written config of that build points at"),
        new("README.md", @"From a build from main: when `%LOCALAPPDATA%\\win-desktop-mcp\\layouts` has been used",
            "the upgrade notes: the AppData folder of that build, which presets are copied from"),
        new("README.md", @"in `%LOCALAPPDATA%\\win-desktop-mcp`\s+and `%TEMP%\\win-desktop-mcp` for a build from main",
            "the upgrade notes: the folders of that build that keep what is not carried over"),
        new("README.md", @"still starts\s+`WindowsAppManagerMcp\.exe` or `win-desktop-mcp\.exe`",
            "the upgrade notes: the exe of that build, which a client that would not share the pause still starts"),
    ];

    /// <summary>
    /// Text that may keep a setting's name from before 4.0.0, as <see cref="WindowsAppManagerAllowList"/>. The server's source
    /// needs none: SettingNames builds each old name from its prefix.
    /// </summary>
    private static readonly AllowedOldName[] OldSettingNameAllowList =
    [
        // What a 3.x user reads: the rule, and the upgrade notes, which name one old variable as the example of the
        // rename and the one that still picks the layouts folder
        new("README.md", @"`WAM_` and the same suffix \(such as `WAM_DENY_PROCESSES`\)",
            "the configuration section's paragraph on the names from before 4.0.0"),
        new("README.md", @"`WAM_DENY_PROCESSES` is now `HELMSMAN_DENY_PROCESSES`",
            "the upgrade notes: an old name beside its new one, as the example of the rename"),
        new("README.md", @"\(or still `WAM_LAYOUTS_PATH`\) set, that folder is used as before",
            "the upgrade notes: the old name still picks the layouts folder while the new one is not set"),

        // Clearing a variable must clear its old name too, which would be read in its place
        new("scripts/Test-ToolsSnapshot.ps1", @"'WAM_EXCLUDE_TOOLS', 'WAM_READ_ONLY'",
            "the exe that lists its tools must serve every tool, whatever the caller's settings under either name"),
        new(".github/workflows/test.yml", @"'WAM_ALLOW_DESKTOP_TESTS', 'WAM_ALLOW_DESKTOP_NOINPUT_TESTS'",
            "the check that desktop tests are skipped unsets the gate's opt-ins under both names"),
        new(TestingGuide, @"`WAM_ALLOW_DESKTOP_TESTS` or `WAM_ALLOW_DESKTOP_NOINPUT_TESTS` in its place",
            "the desktop test gate still reads its opt-ins' names from before 4.0.0"),
        new(TestingGuide, @"`WAM_UIA_ENGINE`, its name before 4\.0\.0, unset too",
            "the release gate's default-engine run unsets the engine under both names"),
        new(TestingGuide, @"-u WAM_UIA_ENGINE HELMSMAN_ALLOW_DESKTOP_NOINPUT_TESTS=1",
            "the release gate's default-engine run unsets the engine under both names (Git Bash)"),
        new(TestingGuide, @"WAM_UIA_ENGINE, its name before 4\.0\.0, is read when the new one is not set",
            "the release gate's default-engine run unsets the engine under both names (PowerShell comment)"),
        new(TestingGuide, @"Env:WAM_UIA_ENGINE -ErrorAction SilentlyContinue",
            "the release gate's default-engine run unsets the engine under both names (PowerShell)"),
    ];

    /// <summary>Text that may keep the resources' scheme from before 4.0.0.</summary>
    private static readonly AllowedOldName[] OldResourceSchemeAllowList =
    [
        new("README.md", @"A `wam://` URI is\s+no longer served", "the README's upgrade notes, for a 3.x user's saved resource URIs"),
    ];

    /// <summary>Each old name: its pattern, its allow-list, and whether TESTING.md and the Error Codes of docs/TOOLS.md are checked too.</summary>
    private static readonly Dictionary<string, (Regex Pattern, AllowedOldName[] AllowList, bool AlsoCurrentGuides)> OldNames =
        new(StringComparer.Ordinal)
        {
            ["windows-app-manager"] = (WindowsAppManager(), WindowsAppManagerAllowList, false),
            ["win-desktop-mcp"] = (WinDesktopMcp(), WinDesktopMcpAllowList, false),
            ["WAM_"] = (OldSettingName(), OldSettingNameAllowList, true),
            ["wam://"] = (OldResourceScheme(), OldResourceSchemeAllowList, true),
        };

    public static TheoryData<string> OldNameKeys() => new(OldNames.Keys);

    [Fact]
    public void CheckedFiles_AreFound()
    {
        // Guards the guard: a wrong root or folder list would check nothing and pass.
        CheckedFiles().Keys.Should().Contain(new[]
        {
            "README.md", "Directory.Build.props", ToolsReference, ".claude-plugin/plugin.json",
            "src/Helmsman/Infrastructure/ServerIdentity.cs", "skills/using-helmsman/SKILL.md",
            "scripts/ReleaseCommon.ps1", ".claude/commands/update-plugin.md", "config/layouts/work-setup.json",
        });
        CheckedFiles().Keys.Should().Contain(f => f.StartsWith(".github/", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCurrentGuides_AreFound_ForTheNamesOf4_0_0()
    {
        var guides = CurrentGuides();

        guides.Keys.Should().BeEquivalentTo(TestingGuide, ToolsErrorCodes);
        guides[ToolsErrorCodes].Should().StartWith(ToolsErrorCodesHeading).And.Contain("TargetDeniedByPolicy");
        guides[TestingGuide].Should().Contain("[DesktopNoInputFact]");
    }

    [Theory]
    [InlineData("WamFixtureApp")]
    [InlineData("WAM Fixture Main")]
    [InlineData(@"Local\WamTests.Gate")]
    [InlineData("WAM_ and the same suffix")]
    [InlineData("HELMSMAN_TOOLSETS")]
    [InlineData("swam_toolsets")]
    [InlineData("WAMP_TOOLSETS")]
    [InlineData("SWAM_READ_ONLY")]
    [InlineData("2wam_x")]
    public void TheOldSettingPattern_LeavesOtherNamesAlone(string text)
    {
        OldNames["WAM_"].Pattern.IsMatch(text).Should().BeFalse();
    }

    [Fact]
    public void ToolsReference_IsCheckedUpToItsChangelogOnly()
    {
        var full = File.ReadAllText(RepositoryFiles.PathOf("docs", "TOOLS.md"));
        var checkedPart = CheckedFiles()[ToolsReference];

        checkedPart.Length.Should().BeLessThan(full.Length);
        full[checkedPart.Length..].Should().StartWith(ToolsChangelogHeading);
    }

    [Theory]
    [InlineData("windows-app-manager", "Windows App Manager")]
    [InlineData("windows-app-manager", "windows_app_manager-marketplace")]
    [InlineData("windows-app-manager", "WindowsAppManagerMcp")]
    [InlineData("win-desktop-mcp", "win-desktop-mcp")]
    [InlineData("win-desktop-mcp", "WinDesktopMcp.Services")]
    [InlineData("win-desktop-mcp", "win_desktop_mcp")]
    [InlineData("win-desktop-mcp", "Win Desktop\r\nMCP")]
    [InlineData("WAM_", "WAM_TOOLSETS")]
    [InlineData("WAM_", "$env:WAM_ALLOW_DESKTOP_TESTS")]
    [InlineData("WAM_", "$env:wam_read_only")]
    [InlineData("WAM_", "%Wam_Toolsets%")]
    [InlineData("WAM_", @"WAM\_TOOLSETS")]
    [InlineData("WAM_", "x_WAM_TOOLSETS")]
    [InlineData("wam://", "wam://monitors")]
    [InlineData("wam://", "WAM://layouts/{name}")]
    public void OldNamePatterns_FindEverySpelling(string oldName, string text)
    {
        OldNames[oldName].Pattern.IsMatch(text).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(OldNameKeys))]
    public void ShippedAndCopiedText_DoesNotUseTheOldProductName(string oldName)
    {
        var unexcused = Hits(oldName).Where(hit => hit.ExcusedBy.Count == 0).Select(hit => hit.ToString());

        unexcused.Should().BeEmpty(
            "the product is called Helmsman; keep the old name {0} only where its allow-list in OldProductNameTests says why",
            oldName);
    }

    [Theory]
    [MemberData(nameof(OldNameKeys))]
    public void EveryAllowListEntry_ExcusesAnOldNameHit(string oldName)
    {
        // An entry that excuses no hit is dead: it would hide a typo, a file that moved, or text that changed.
        var used = Hits(oldName).SelectMany(hit => hit.ExcusedBy).ToHashSet();
        var unused = OldNames[oldName].AllowList.Where(entry => !used.Contains(entry))
            .Select(entry => $"{entry.File ?? "*"}: {entry.Pattern} ({entry.Reason})");

        unused.Should().BeEmpty();
    }

    /// <summary>Every occurrence of an old name in the checked text, with the entries of its allow-list that excuse it.</summary>
    private static List<OldNameHit> Hits(string oldName)
    {
        var (pattern, allowList, alsoCurrentGuides) = OldNames[oldName];
        var hits = new List<OldNameHit>();
        var texts = alsoCurrentGuides ? CheckedFiles().Concat(CurrentGuides()) : CheckedFiles();
        foreach (var (file, text) in texts)
        {
            var allowed = allowList
                .Where(entry => entry.File == null || entry.File == file)
                .SelectMany(entry => Regex.Matches(text, entry.Pattern, RegexOptions.IgnoreCase)
                    .Select(match => (Entry: entry, Start: match.Index, End: match.Index + match.Length)))
                .ToList();

            foreach (var hit in pattern.Matches(text).AsEnumerable())
            {
                var excusedBy = allowed
                    .Where(range => hit.Index >= range.Start && hit.Index + hit.Length <= range.End)
                    .Select(range => range.Entry)
                    .ToList();
                var line = text[..hit.Index].Count(c => c == '\n') + 1;
                hits.Add(new OldNameHit(file, line, hit.Value, excusedBy));
            }
        }

        return hits;
    }

    /// <summary>The checked files by path ('/' separated, relative to the root), with the text that is checked.</summary>
    private static Dictionary<string, string> CheckedFiles()
    {
        var paths = CheckedRootFiles.Where(f => File.Exists(RepositoryFiles.PathOf(f))).ToList();
        foreach (var folder in CheckedFolders)
        {
            var root = RepositoryFiles.PathOf(folder);
            if (Directory.Exists(root))
                paths.AddRange(TextFilesUnder(root));
        }

        var files = paths.Distinct(StringComparer.Ordinal)
            .ToDictionary(path => path, path => File.ReadAllText(RepositoryFiles.PathOf(path.Split('/'))), StringComparer.Ordinal);
        files[ToolsReference] = ToolsReferenceAboveChangelog();
        return files;
    }

    /// <summary>TESTING.md and the Error Codes section of docs/TOOLS.md (from its heading to the end), checked for the 4.0.0 names.</summary>
    private static Dictionary<string, string> CurrentGuides()
    {
        var tools = File.ReadAllText(RepositoryFiles.PathOf(ToolsReference.Split('/')));
        var heading = Regex.Match(tools, "^" + Regex.Escape(ToolsErrorCodesHeading) + @"\r?$", RegexOptions.Multiline);
        heading.Success.Should().BeTrue($"{ToolsReference} must have the heading '{ToolsErrorCodesHeading}'");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TestingGuide] = File.ReadAllText(RepositoryFiles.PathOf(TestingGuide)),
            [ToolsErrorCodes] = tools[heading.Index..],
        };
    }

    private static string ToolsReferenceAboveChangelog()
    {
        var text = File.ReadAllText(RepositoryFiles.PathOf(ToolsReference.Split('/')));
        var heading = Regex.Match(text, "^" + Regex.Escape(ToolsChangelogHeading) + @"\r?$", RegexOptions.Multiline);
        heading.Success.Should().BeTrue(
            $"{ToolsReference} must have the heading '{ToolsChangelogHeading}': the check stops there, and without it would check the history too or nothing");
        return text[..heading.Index];
    }

    private static IEnumerable<string> TextFilesUnder(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (TextExtensions.Contains(Path.GetExtension(file)))
                yield return Path.GetRelativePath(RepositoryFiles.Root, file).Replace('\\', '/');
        }

        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            if (SkippedFolders.Contains(Path.GetFileName(sub)))
                continue;

            foreach (var file in TextFilesUnder(sub))
                yield return file;
        }
    }

    private sealed record AllowedOldName(string? File, string Pattern, string Reason);

    private sealed record OldNameHit(string File, int Line, string Text, List<AllowedOldName> ExcusedBy)
    {
        public override string ToString() => $"{File}:{Line}: {Text.ReplaceLineEndings(" ")}";
    }
}
