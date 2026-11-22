using FluentAssertions.Execution;
using WinDesktopMcp.Infrastructure;

namespace WinDesktopMcp.Tests.Unit;

/// <summary>
/// Guards against version drift between Directory.Build.props (the single source of truth)
/// and the Claude Code plugin manifests. scripts/Set-Version.ps1 updates all three together.
/// These tests only read files in the repository.
/// </summary>
public class VersionConsistencyTests
{
    private const string PluginName = "win-desktop-mcp";

    [Fact]
    public void PluginJson_Version_MatchesDirectoryBuildProps()
    {
        var propsVersion = RepositoryFiles.ReadPropsVersion();
        using var plugin = RepositoryFiles.ReadJson(".claude-plugin", "plugin.json");

        plugin.RootElement.GetProperty("version").GetString().Should().Be(propsVersion,
            "run scripts/Set-Version.ps1 to change the version in every manifest at once");
    }

    [Fact]
    public void MarketplaceJson_PluginEntryVersion_MatchesDirectoryBuildProps()
    {
        var propsVersion = RepositoryFiles.ReadPropsVersion();
        using var marketplace = RepositoryFiles.ReadJson(".claude-plugin", "marketplace.json");

        var entry = marketplace.RootElement.GetProperty("plugins").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == PluginName);

        entry.GetProperty("version").GetString().Should().Be(propsVersion,
            "run scripts/Set-Version.ps1 to change the version in every manifest at once");
    }

    [Fact]
    public void PluginJson_RunsTheExeInRepositoryBin()
    {
        // bin/win-desktop-mcp.exe is the only binary the plugin runs, and the only one
        // scripts/Publish-Plugin.ps1 refreshes.
        using var plugin = RepositoryFiles.ReadJson(".claude-plugin", "plugin.json");

        var command = plugin.RootElement.GetProperty("mcpServers").GetProperty(PluginName)
            .GetProperty("command").GetString();

        command.Should().Be("${CLAUDE_PLUGIN_ROOT}/bin/win-desktop-mcp.exe");
    }

    /// <summary>
    /// The product has one id, win-desktop-mcp: the plugin, its marketplace entry, the plugin's only MCP server key, the
    /// MCP serverInfo.name, the plugin cache folder the layout migration looks for, the release scripts' plugin name and
    /// the skill folder (using-win-desktop-mcp) all use it, and the marketplace is named after it. A rename that misses
    /// one of them gives users two names for one product (and Set-Version.ps1 a marketplace entry it cannot find).
    /// </summary>
    [Fact]
    public void ProductIds_AllAgreeOnWinDesktopMcp()
    {
        using var plugin = RepositoryFiles.ReadJson(".claude-plugin", "plugin.json");
        using var marketplace = RepositoryFiles.ReadJson(".claude-plugin", "marketplace.json");
        var releaseCommon = File.ReadAllText(RepositoryFiles.PathOf("scripts", "ReleaseCommon.ps1"));

        using (new AssertionScope())
        {
            plugin.RootElement.GetProperty("name").GetString().Should().Be(PluginName);
            plugin.RootElement.GetProperty("mcpServers").EnumerateObject().Select(p => p.Name).Should().Equal(PluginName);
            marketplace.RootElement.GetProperty("name").GetString().Should().Be(PluginName + "-marketplace");
            marketplace.RootElement.GetProperty("plugins").EnumerateArray().Select(p => p.GetProperty("name").GetString())
                .Should().Equal(PluginName);
            ServerIdentity.Name.Should().Be(PluginName);
            LayoutStorageOptions.PluginFolderName.Should().Be(PluginName);
            releaseCommon.Should().Contain($"$script:PluginName = '{PluginName}'");
            Directory.EnumerateDirectories(RepositoryFiles.PathOf("skills")).Select(path => Path.GetFileName(path))
                .Should().Equal("using-" + PluginName);
        }
    }
}
