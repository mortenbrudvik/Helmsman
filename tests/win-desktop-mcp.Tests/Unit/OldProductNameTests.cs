using System.Text.RegularExpressions;

namespace WinDesktopMcp.Tests.Unit;

/// <summary>
/// Release 4.0.0 renamed the product from Windows App Manager (windows-app-manager, WindowsAppManager) to
/// win-desktop-mcp. This keeps the old name from creeping back into what ships or what a user copies: the server's
/// source, the plugin manifests, the skill, the scripts, the workflows, the Claude Code commands and README.md. The
/// text files under those folders are read from disk (bin and obj folders left out), and every occurrence of an old
/// name must be one of the few the allow-list below names, each with its reason. History documents (PLANNING.md,
/// docs/AUTOMATION-ROADMAP.md, the changelog of docs/TOOLS.md) and the tests are not checked. These tests only read
/// files in the repository.
/// </summary>
public partial class OldProductNameTests
{
    [GeneratedRegex(@"windows-app-manager|windows app manager|windowsappmanager", RegexOptions.IgnoreCase)]
    private static partial Regex OldName();

    /// <summary>The folders (and root files) whose text files are checked, relative to the repository root.</summary>
    private static readonly string[] CheckedFolders = ["src", ".claude-plugin", "skills", "scripts", ".github", Path.Combine(".claude", "commands")];

    private static readonly string[] CheckedRootFiles = ["README.md"];

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".json", ".md", ".ps1", ".psm1", ".yml", ".yaml", ".txt", ".xml",
        ".manifest", ".config", ".editorconfig", ".resx",
    };

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj" };

    /// <summary>
    /// Text that may keep an old name, by file (a path relative to the repository root, '/' separated; null for every
    /// checked file) and pattern. An occurrence is allowed when it lies inside a match of an entry for its file.
    /// </summary>
    private static readonly AllowedOldName[] AllowList =
    [
        // The GitHub repository keeps its name, and its URLs and the owner/repo form must keep working.
        new(null, @"(https://github\.com/)?mortenbrudvik/WindowsAppManagerMcp",
            "the repository: its URLs and its owner/repo form (/plugin marketplace add)"),

        // Migration sources: 3.x kept presets in %LOCALAPPDATA%\WindowsAppManagerMcp\layouts and installed the plugin
        // as windows-app-manager@windows-app-manager-marketplace; the server copies presets from both.
        new("src/win-desktop-mcp/Services/LayoutStorageOptions.cs", @"PreviousAppDataFolderName = ""WindowsAppManagerMcp""",
            "the previous AppData folder, a migration source"),
        new("src/win-desktop-mcp/Services/LayoutStorageOptions.cs", @"PreviousPluginFolderName = ""windows-app-manager""",
            "the previous plugin cache folder, a migration source"),
        new("src/win-desktop-mcp/Services/LayoutStorageOptions.cs", @"PreviousMarketplaceFolderName = ""windows-app-manager-marketplace""",
            "the previous marketplace cache folder, a migration source"),
        new("src/win-desktop-mcp/Services/LayoutStorageOptions.cs", @"%LOCALAPPDATA%\\WindowsAppManagerMcp\\layouts",
            "the comment on the previous AppData folder"),
        new("src/win-desktop-mcp/Services/LayoutStorageOptions.cs", @"\\cache\\windows-app-manager-marketplace\\windows-app-manager\\",
            "the comment on the previous plugin cache folder"),

        // README: the upgrade notes must name the old ids a 3.x user has to remove, and the layout section names the
        // old AppData folder presets are copied from.
        new("README.md", @"(?ms)^### Upgrading from 3\.x\s*$.*?(?=^#{1,3} )", "the upgrade notes from 3.x, up to the next heading"),
        new("README.md", @"%LOCALAPPDATA%\\WindowsAppManagerMcp\\layouts", "where older versions' presets are copied from"),

        // /update-plugin sends someone who has only the 3.x plugin installed to the upgrade notes.
        new(".claude/commands/update-plugin.md", @"`windows-app-manager@windows-app-manager-marketplace`", "the 3.x plugin id"),
    ];

    [Fact]
    public void CheckedFiles_AreFound()
    {
        // Guards the guard: a wrong root or folder list would check nothing and pass.
        CheckedFiles().Should().Contain(new[]
        {
            "README.md", ".claude-plugin/plugin.json", "src/win-desktop-mcp/Infrastructure/ServerIdentity.cs",
            "skills/using-win-desktop-mcp/SKILL.md", "scripts/ReleaseCommon.ps1", ".claude/commands/update-plugin.md",
        });
        CheckedFiles().Should().Contain(f => f.StartsWith(".github/", StringComparison.Ordinal));
    }

    [Fact]
    public void ShippedAndCopiedText_DoesNotUseTheOldProductName()
    {
        var hits = new List<string>();
        foreach (var file in CheckedFiles())
        {
            var text = File.ReadAllText(RepositoryFiles.PathOf(file.Split('/')));
            var allowed = AllowList
                .Where(entry => entry.File == null || entry.File == file)
                .SelectMany(entry => Regex.Matches(text, entry.Pattern, RegexOptions.IgnoreCase))
                .Select(match => (Start: match.Index, End: match.Index + match.Length))
                .ToList();

            foreach (System.Text.RegularExpressions.Match hit in OldName().Matches(text))
            {
                if (allowed.Any(range => hit.Index >= range.Start && hit.Index + hit.Length <= range.End))
                    continue;

                var line = text[..hit.Index].Count(c => c == '\n') + 1;
                hits.Add($"{file}:{line}: {hit.Value}");
            }
        }

        hits.Should().BeEmpty(
            "the product is called win-desktop-mcp; keep an old name only where OldProductNameTests.AllowList says why");
    }

    [Fact]
    public void EveryAllowListEntry_IsStillNeeded()
    {
        // An entry that matches nothing allows nothing, and would hide a typo or a file that moved.
        var checkedFiles = CheckedFiles();
        var unused = AllowList
            .Where(entry => !checkedFiles
                .Where(file => entry.File == null || entry.File == file)
                .Any(file => Regex.IsMatch(File.ReadAllText(RepositoryFiles.PathOf(file.Split('/'))), entry.Pattern, RegexOptions.IgnoreCase)))
            .Select(entry => $"{entry.File ?? "*"}: {entry.Pattern} ({entry.Reason})");

        unused.Should().BeEmpty();
    }

    private static List<string> CheckedFiles()
    {
        var files = CheckedRootFiles.Where(f => File.Exists(RepositoryFiles.PathOf(f))).ToList();
        foreach (var folder in CheckedFolders)
        {
            var root = RepositoryFiles.PathOf(folder);
            if (Directory.Exists(root))
                files.AddRange(TextFilesUnder(root));
        }

        return files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
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
}
