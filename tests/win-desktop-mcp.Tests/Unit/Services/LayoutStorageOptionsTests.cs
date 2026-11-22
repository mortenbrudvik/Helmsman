using System.Text.Json;

namespace WinDesktopMcp.Tests.Unit.Services;

/// <summary>
/// Tests for resolving where layout presets are stored, which legacy folders are
/// migrated, and which sample presets ship with the server.
/// </summary>
public class LayoutStorageOptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"LayoutStorageOptionsTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    #region ResolveLayoutsPath

    [Fact]
    public void ResolveLayoutsPath_WithoutOverride_UsesLocalAppDataFolder()
    {
        var path = LayoutStorageOptions.ResolveLayoutsPath(null, @"C:\Users\someone\AppData\Local");

        path.Should().Be(@"C:\Users\someone\AppData\Local\win-desktop-mcp\layouts");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveLayoutsPath_WithBlankOverride_UsesLocalAppDataFolder(string overridePath)
    {
        var path = LayoutStorageOptions.ResolveLayoutsPath(overridePath, @"C:\Local");

        path.Should().Be(@"C:\Local\win-desktop-mcp\layouts");
    }

    [Fact]
    public void ResolveLayoutsPath_WithOverride_UsesOverride()
    {
        var path = LayoutStorageOptions.ResolveLayoutsPath(@"D:\Sync\layouts", @"C:\Local");

        path.Should().Be(@"D:\Sync\layouts");
    }

    [Fact]
    public void ResolveLayoutsPath_WithQuotedOverride_StripsQuotes()
    {
        var path = LayoutStorageOptions.ResolveLayoutsPath("\"D:\\My Layouts\"", @"C:\Local");

        path.Should().Be(@"D:\My Layouts");
    }

    [Fact]
    public void ResolveLayoutsPath_WithEnvironmentVariableInOverride_ExpandsIt()
    {
        var path = LayoutStorageOptions.ResolveLayoutsPath(@"%SystemRoot%\layouts", @"C:\Local");

        path.Should().Be(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "layouts"));
    }

    [Fact]
    public void LayoutsPathEnvironmentVariable_IsWamLayoutsPath()
    {
        LayoutStorageOptions.LayoutsPathEnvironmentVariable.Should().Be("WAM_LAYOUTS_PATH");
    }

    #endregion

    #region FindLegacyLayoutFolders

    [Fact]
    public void FindLegacyLayoutFolders_IncludesLayoutsFolderNextToExecutable()
    {
        var baseDirectory = Path.Combine(_root, "app") + Path.DirectorySeparatorChar;

        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(baseDirectory);

        folders.Should().ContainSingle()
            .Which.Should().Be(Path.Combine(_root, "app", "layouts"));
    }

    [Theory]
    [InlineData("win-desktop-mcp-marketplace", "win-desktop-mcp")]
    [InlineData("windows-app-manager-marketplace", "windows-app-manager")]
    public void FindLegacyLayoutFolders_InPluginCache_UnderEitherPluginName_IncludesOtherInstalledVersions(
        string marketplace, string plugin)
    {
        // Arrange - ...\plugins\cache\<marketplace>\<plugin>\<version>\bin
        var pluginDir = Path.Combine(_root, "plugins", "cache", marketplace, plugin);
        var currentBin = Path.Combine(pluginDir, "4.0.0", "bin");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(pluginDir, "3.9.0", "bin", "layouts"));

        // Act
        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin);

        // Assert
        folders.Should().Equal(
            Path.Combine(currentBin, "layouts"),
            Path.Combine(pluginDir, "3.9.0", "bin", "layouts"),
            Path.Combine(pluginDir, "3.9.0", ".claude-plugin", "bin", "layouts"));
    }

    [Theory]
    [InlineData("win-desktop-mcp")]
    [InlineData("windows-app-manager")]
    public void FindLegacyLayoutFolders_UnderAnArbitraryMarketplace_RecognisesItselfAndItsOwnSiblings(string plugin)
    {
        // Arrange - installed from a marketplace of another name (a fork, a local marketplace): its own folder counts
        var pluginDir = Path.Combine(_root, "plugins", "cache", "some-marketplace", plugin);
        var currentBin = Path.Combine(pluginDir, "4.0.0", "bin");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(pluginDir, "3.9.0", "bin", "layouts"));

        // Act
        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin);

        // Assert
        folders.Should().Equal(
            Path.Combine(currentBin, "layouts"),
            Path.Combine(pluginDir, "3.9.0", "bin", "layouts"),
            Path.Combine(pluginDir, "3.9.0", ".claude-plugin", "bin", "layouts"));
    }

    [Theory]
    [InlineData("win-desktop-mcp")]
    [InlineData("windows-app-manager")]
    public void FindLegacyLayoutFolders_PluginOfOurNameUnderAnUnrelatedMarketplace_IsNotScanned(string plugin)
    {
        // Arrange - a preset without authoredBy counts as the user's own and launches without asking, so another
        // marketplace that ships a plugin under one of our names must not be able to plant presets.
        var cache = Path.Combine(_root, "plugins", "cache");
        var currentBin = Path.Combine(cache, "win-desktop-mcp-marketplace", "win-desktop-mcp", "4.0.0", "bin");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(cache, "unrelated-marketplace", plugin, "9.9.9", "bin", "layouts"));

        // Act
        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin);

        // Assert
        folders.Should().Equal(Path.Combine(currentBin, "layouts"));
    }

    [Fact]
    public void FindLegacyLayoutFolders_AfterTheRename_IncludesTheVersionsInstalledUnderTheOldPluginName()
    {
        // Arrange - 4.x is installed as win-desktop-mcp@win-desktop-mcp-marketplace; the 3.x versions, which may still
        // hold presets next to their exe, stay under windows-app-manager@windows-app-manager-marketplace.
        var cache = Path.Combine(_root, "plugins", "cache");
        var currentBin = Path.Combine(cache, "win-desktop-mcp-marketplace", "win-desktop-mcp", "4.0.0", "bin");
        var oldPlugin = Path.Combine(cache, "windows-app-manager-marketplace", "windows-app-manager");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(oldPlugin, "1.10.0", "bin", "layouts"));
        Directory.CreateDirectory(Path.Combine(oldPlugin, "3.2.0", ".claude-plugin", "bin", "layouts"));

        // Act
        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin);

        // Assert
        folders.Should().Contain(Path.Combine(oldPlugin, "1.10.0", "bin", "layouts"));
        folders.Should().Contain(Path.Combine(oldPlugin, "3.2.0", ".claude-plugin", "bin", "layouts"));
    }

    [Fact]
    public void FindLegacyLayoutFolders_UnderTheOldPluginName_IncludesTheVersionsInstalledUnderTheNewOne()
    {
        // Arrange - this code running from the old plugin folder name (a 4.x exe placed under the 3.x install, as
        // /update-plugin did before the rename) also finds the versions under the new name.
        var cache = Path.Combine(_root, "plugins", "cache");
        var currentBin = Path.Combine(cache, "windows-app-manager-marketplace", "windows-app-manager", "3.2.0", "bin");
        var newPlugin = Path.Combine(cache, "win-desktop-mcp-marketplace", "win-desktop-mcp");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(newPlugin, "4.0.0", "bin"));

        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin);

        folders.Should().Contain(Path.Combine(newPlugin, "4.0.0", "bin", "layouts"));
    }

    [Fact]
    public void FindLegacyLayoutFolders_InPluginCache_SkipsOtherPluginsOfTheCache()
    {
        var cache = Path.Combine(_root, "plugins", "cache");
        var currentBin = Path.Combine(cache, "win-desktop-mcp-marketplace", "win-desktop-mcp", "4.0.0", "bin");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(cache, "win-desktop-mcp-marketplace", "another-plugin", "1.0.0", "bin", "layouts"));
        Directory.CreateDirectory(Path.Combine(cache, "other-marketplace", "another-plugin", "1.0.0", "bin", "layouts"));

        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin);

        folders.Should().ContainSingle().Which.Should().Be(Path.Combine(currentBin, "layouts"));
    }

    [Fact]
    public void PluginAndMarketplaceFolderNames_AreTheNewAndThePreviousNames()
    {
        LayoutStorageOptions.PluginFolderName.Should().Be("win-desktop-mcp");
        LayoutStorageOptions.PreviousPluginFolderName.Should().Be("windows-app-manager");
        LayoutStorageOptions.MarketplaceFolderName.Should().Be("win-desktop-mcp-marketplace");
        LayoutStorageOptions.PreviousMarketplaceFolderName.Should().Be("windows-app-manager-marketplace");
    }

    [Fact]
    public void FindLegacyLayoutFolders_InPluginCache_IncludesOtherInstalledVersions()
    {
        // Arrange - ...\plugins\cache\<marketplace>\windows-app-manager\<version>\bin (the layout of a 3.x install)
        var pluginDir = Path.Combine(_root, "plugins", "cache", "some-marketplace", "windows-app-manager");
        var currentBin = Path.Combine(pluginDir, "1.12.0", "bin");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(pluginDir, "1.10.0", "bin", "layouts"));
        Directory.CreateDirectory(Path.Combine(pluginDir, "1.11.0", "bin"));

        // Act
        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin + Path.DirectorySeparatorChar);

        // Assert
        folders.Should().Contain(Path.Combine(currentBin, "layouts"));
        folders.Should().Contain(Path.Combine(pluginDir, "1.10.0", "bin", "layouts"));
        folders.Should().Contain(Path.Combine(pluginDir, "1.11.0", "bin", "layouts"));
    }

    [Fact]
    public void FindLegacyLayoutFolders_OutsidePluginCache_DoesNotScanSiblingFolders()
    {
        // Arrange - a dev checkout such as C:\code\win-desktop-mcp\bin next to other projects
        var currentBin = Path.Combine(_root, "code", "win-desktop-mcp", "bin");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(_root, "code", "OtherProject", "bin", "layouts"));

        // Act
        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin);

        // Assert
        folders.Should().ContainSingle()
            .Which.Should().Be(Path.Combine(currentBin, "layouts"));
    }

    [Fact]
    public void FindLegacyLayoutFolders_ForDifferentPluginName_DoesNotScanSiblingFolders()
    {
        var pluginDir = Path.Combine(_root, "plugins", "cache", "some-marketplace", "another-plugin");
        var currentBin = Path.Combine(pluginDir, "2.0.0", "bin");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(pluginDir, "1.0.0", "bin", "layouts"));

        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin);

        folders.Should().ContainSingle();
    }

    #endregion

    #region Create / ForPath

    [Fact]
    public void Create_CombinesResolvedPathLegacyFoldersAndSamples()
    {
        var samples = new List<SampleLayout> { new("a.json", "{}") };
        var baseDirectory = Path.Combine(_root, "app");

        var options = LayoutStorageOptions.Create(null, @"C:\Local", baseDirectory, samples);

        options.LayoutsPath.Should().Be(@"C:\Local\win-desktop-mcp\layouts");
        options.MigrationSources.Should().Contain(Path.Combine(baseDirectory, "layouts"));
        options.MigrationSources.Should().Contain(@"C:\Local\WindowsAppManagerMcp\layouts");
        options.Samples.Should().BeEquivalentTo(samples);
    }

    [Fact]
    public void ForPath_DoesNotMigrateOrSeed()
    {
        var options = LayoutStorageOptions.ForPath(_root);

        options.LayoutsPath.Should().Be(_root);
        options.MigrationSources.Should().BeEmpty();
        options.Samples.Should().BeEmpty();
    }

    #endregion

    #region Embedded samples

    [Fact]
    public void LoadEmbeddedSamples_ContainsTheBundledSamplePresets()
    {
        var samples = LayoutStorageOptions.LoadEmbeddedSamples();

        samples.Select(s => s.FileName).Should().BeEquivalentTo(
            "work-setup.json", "dual-monitor.json", "focus-mode.json");
    }

    [Fact]
    public void LoadEmbeddedSamples_AreValidPresetsNamedAfterTheirFiles()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        foreach (var sample in LayoutStorageOptions.LoadEmbeddedSamples())
        {
            var preset = JsonSerializer.Deserialize<LayoutPreset>(sample.Json, options);

            preset.Should().NotBeNull();
            preset!.Name.Should().Be(Path.GetFileNameWithoutExtension(sample.FileName));
            preset.Placements.Should().NotBeEmpty();
        }
    }

    #endregion
}
