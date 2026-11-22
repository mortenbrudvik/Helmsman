using System.Text.RegularExpressions;

namespace Helmsman.Tests.Unit;

/// <summary>
/// Release 4.0.0 renamed the product from Windows App Manager (windows-app-manager, WindowsAppManager) to
/// win-desktop-mcp. This keeps the old name from creeping back into what ships or what a user copies: the server's
/// source, the plugin manifests, the skill, the scripts, the workflows, the Claude Code commands, the sample presets
/// (config), Directory.Build.props, README.md, and docs/TOOLS.md above its changelog. The text files are read from
/// disk (bin and obj folders left out) as a whole, so a name broken over two lines is found too, and every occurrence
/// of an old name must be excused by an entry of the allow-list below, each with its reason. History documents
/// (PLANNING.md, docs/AUTOMATION-ROADMAP.md, the changelog of docs/TOOLS.md) and the tests are not checked. These tests
/// only read files in the repository.
/// </summary>
public partial class OldProductNameTests
{
    /// <summary>Any spelling of the old name: Windows App Manager, windows-app-manager, WindowsAppManager, windows_app_manager, across a line break.</summary>
    [GeneratedRegex(@"windows[\s_-]*app[\s_-]*manager", RegexOptions.IgnoreCase)]
    private static partial Regex OldName();

    /// <summary>The folders whose text files are checked, relative to the repository root.</summary>
    private static readonly string[] CheckedFolders =
        ["src", ".claude-plugin", "skills", "scripts", ".github", Path.Combine(".claude", "commands"), "config"];

    /// <summary>Single files checked whole.</summary>
    private static readonly string[] CheckedRootFiles = ["README.md", "Directory.Build.props"];

    /// <summary>docs/TOOLS.md is checked up to this heading; the changelog below it records what was true at the time.</summary>
    private const string ToolsReference = "docs/TOOLS.md";

    private const string ToolsChangelogHeading = "## Changes for existing clients";

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".json", ".md", ".ps1", ".psm1", ".yml", ".yaml", ".txt", ".xml",
        ".manifest", ".config", ".editorconfig", ".resx",
    };

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj" };

    /// <summary>
    /// Text that may keep an old name, by file (a path relative to the repository root, '/' separated; null for every
    /// checked file) and pattern. An occurrence is excused when it lies inside a match of an entry for its file. The
    /// entries name the old forms with the words around them, so the same old id written where the new one belongs
    /// still fails.
    /// </summary>
    private static readonly AllowedOldName[] AllowList =
    [
        // The GitHub repository keeps its name, and its URLs and the owner/repo form must keep working.
        new(null, @"(https://github\.com/)?mortenbrudvik/WindowsAppManagerMcp",
            "the repository: its URLs and its owner/repo form (/plugin marketplace add)"),

        // Migration sources: 3.x kept presets in %LOCALAPPDATA%\WindowsAppManagerMcp\layouts and installed the plugin
        // as windows-app-manager@windows-app-manager-marketplace; the server copies presets from both.
        new(LayoutStorageOptionsFile, @"PreviousAppDataFolderName = ""WindowsAppManagerMcp""",
            "the previous AppData folder, a migration source"),
        new(LayoutStorageOptionsFile, @"PreviousPluginFolderName = ""windows-app-manager""",
            "the previous plugin cache folder, a migration source"),
        new(LayoutStorageOptionsFile, @"PreviousMarketplaceFolderName = ""windows-app-manager-marketplace""",
            "the previous marketplace cache folder, a migration source"),
        new(LayoutStorageOptionsFile, @"%LOCALAPPDATA%\\WindowsAppManagerMcp\\layouts",
            "the comment on the previous AppData folder"),
        new(LayoutStorageOptionsFile, @"\\cache\\windows-app-manager-marketplace\\windows-app-manager\\",
            "the comment on the previous plugin cache folder"),

        // README's "Upgrading from 3.x" must name the old ids a 3.x user has to remove or replace, and the layout
        // section names the old AppData folder presets are copied from.
        new("README.md", @"renamed the product from Windows App Manager to win-desktop-mcp", "the upgrade notes' first line"),
        new("README.md", @"plugin uninstall windows-app-manager@windows-app-manager-marketplace`",
            "the upgrade notes: uninstall the 3.x plugin (in a session and from the shell)"),
        new("README.md", @"`/plugin marketplace remove windows-app-manager-marketplace`", "the upgrade notes: remove the 3.x marketplace"),
        new("README.md", @"so `mcp__plugin_windows-app-manager_windows-app-manager__find_windows` is now",
            "the upgrade notes: the old tool prefix, before the new one"),
        new("README.md", @"names a\s+`mcp__plugin_windows-app-manager_windows-app-manager__\.\.\.` tool",
            "the upgrade notes: the saved references to update"),
        new("README.md", @"not `using-windows-app-manager`", "the upgrade notes: the 3.x skill name"),
        new("README.md", @"as `win-desktop-mcp` instead of `windows-app-manager`", "the upgrade notes: the 3.x serverInfo.name"),
        new("README.md", @"3\.x shipped `WindowsAppManagerMcp\.exe`", "the upgrade notes: the 3.x exe name"),
        new("README.md", @"%LOCALAPPDATA%\\WindowsAppManagerMcp\\(layouts|audit)",
            "the 3.x AppData folders presets are copied from and old audit files stay in"),
        // The upgrade notes tell 3.x users where their named debug-browser profiles stay, since they are not copied.
        new("README.md", @"stays in\s+`%LOCALAPPDATA%\\WindowsAppManagerMcp\\browser-profiles\\<name>`",
            "the upgrade notes: where 3.x kept named debug-browser profiles, which are not copied"),
        // The upgrade notes tell 3.x users which temporary folder 4.0.0 does not clean up.
        new("README.md", @"What 3\.x left in\s+`%TEMP%\\WindowsAppManagerMcp`",
            "the upgrade notes: the 3.x temporary folder, left for the user to delete"),

        // /update-plugin sends someone who has only the 3.x plugin installed to the upgrade notes.
        new(".claude/commands/update-plugin.md", @"If only the 3\.x plugin, `windows-app-manager@windows-app-manager-marketplace`, is",
            "the 3.x plugin id"),
    ];

    private const string LayoutStorageOptionsFile = "src/Helmsman/Services/LayoutStorageOptions.cs";

    [Fact]
    public void CheckedFiles_AreFound()
    {
        // Guards the guard: a wrong root or folder list would check nothing and pass.
        CheckedFiles().Keys.Should().Contain(new[]
        {
            "README.md", "Directory.Build.props", ToolsReference, ".claude-plugin/plugin.json",
            "src/Helmsman/Infrastructure/ServerIdentity.cs", "skills/using-win-desktop-mcp/SKILL.md",
            "scripts/ReleaseCommon.ps1", ".claude/commands/update-plugin.md", "config/layouts/work-setup.json",
        });
        CheckedFiles().Keys.Should().Contain(f => f.StartsWith(".github/", StringComparison.Ordinal));
    }

    [Fact]
    public void ToolsReference_IsCheckedUpToItsChangelogOnly()
    {
        var full = File.ReadAllText(RepositoryFiles.PathOf("docs", "TOOLS.md"));
        var checkedPart = CheckedFiles()[ToolsReference];

        checkedPart.Length.Should().BeLessThan(full.Length);
        full[checkedPart.Length..].Should().StartWith(ToolsChangelogHeading);
    }

    [Fact]
    public void ShippedAndCopiedText_DoesNotUseTheOldProductName()
    {
        var unexcused = Hits().Where(hit => hit.ExcusedBy.Count == 0).Select(hit => hit.ToString());

        unexcused.Should().BeEmpty(
            "the product is called win-desktop-mcp; keep an old name only where OldProductNameTests.AllowList says why");
    }

    [Fact]
    public void EveryAllowListEntry_ExcusesAnOldNameHit()
    {
        // An entry that excuses no hit is dead: it would hide a typo, a file that moved, or text that changed.
        var used = Hits().SelectMany(hit => hit.ExcusedBy).ToHashSet();
        var unused = AllowList.Where(entry => !used.Contains(entry)).Select(entry => $"{entry.File ?? "*"}: {entry.Pattern} ({entry.Reason})");

        unused.Should().BeEmpty();
    }

    /// <summary>Every old-name occurrence in the checked text, with the allow-list entries that excuse it.</summary>
    private static List<OldNameHit> Hits()
    {
        var hits = new List<OldNameHit>();
        foreach (var (file, text) in CheckedFiles())
        {
            var allowed = AllowList
                .Where(entry => entry.File == null || entry.File == file)
                .SelectMany(entry => Regex.Matches(text, entry.Pattern, RegexOptions.IgnoreCase)
                    .Select(match => (Entry: entry, Start: match.Index, End: match.Index + match.Length)))
                .ToList();

            foreach (var hit in OldName().Matches(text).AsEnumerable())
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
