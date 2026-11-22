using System.Text.Json;

namespace Helmsman.Tests.Unit.Services;

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

        path.Should().Be(@"C:\Users\someone\AppData\Local\Helmsman\layouts");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveLayoutsPath_WithBlankOverride_UsesLocalAppDataFolder(string overridePath)
    {
        var path = LayoutStorageOptions.ResolveLayoutsPath(overridePath, @"C:\Local");

        path.Should().Be(@"C:\Local\Helmsman\layouts");
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
        LayoutStorageOptions.LayoutsPathEnvironmentVariable.Should().Be("HELMSMAN_LAYOUTS_PATH");
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
    [InlineData("helmsman-marketplace", "helmsman")]
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
    [InlineData("helmsman")]
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
    [InlineData("helmsman")]
    [InlineData("windows-app-manager")]
    public void FindLegacyLayoutFolders_PluginOfOurNameUnderAnUnrelatedMarketplace_IsNotScanned(string plugin)
    {
        // Arrange - a preset without authoredBy counts as the user's own and launches without asking, so another
        // marketplace that ships a plugin under one of our names must not be able to plant presets.
        var cache = Path.Combine(_root, "plugins", "cache");
        var currentBin = Path.Combine(cache, "helmsman-marketplace", "helmsman", "4.0.0", "bin");
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
        // Arrange - Helmsman is installed as helmsman@helmsman-marketplace; the 3.x versions, which may still
        // hold presets next to their exe, stay under windows-app-manager@windows-app-manager-marketplace.
        var cache = Path.Combine(_root, "plugins", "cache");
        var currentBin = Path.Combine(cache, "helmsman-marketplace", "helmsman", "4.0.0", "bin");
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
        var newPlugin = Path.Combine(cache, "helmsman-marketplace", "helmsman");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(newPlugin, "4.0.0", "bin"));

        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin);

        folders.Should().Contain(Path.Combine(newPlugin, "4.0.0", "bin", "layouts"));
    }

    [Fact]
    public void FindLegacyLayoutFolders_InPluginCache_SkipsOtherPluginsOfTheCache()
    {
        var cache = Path.Combine(_root, "plugins", "cache");
        var currentBin = Path.Combine(cache, "helmsman-marketplace", "helmsman", "4.0.0", "bin");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(cache, "helmsman-marketplace", "another-plugin", "1.0.0", "bin", "layouts"));
        Directory.CreateDirectory(Path.Combine(cache, "other-marketplace", "another-plugin", "1.0.0", "bin", "layouts"));

        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin);

        folders.Should().ContainSingle().Which.Should().Be(Path.Combine(currentBin, "layouts"));
    }

    [Theory]
    [InlineData("win-desktop-mcp-marketplace", "win-desktop-mcp")]
    [InlineData("helmsman-marketplace", "win-desktop-mcp")]
    [InlineData("win-desktop-mcp-marketplace", "helmsman")]
    public void FindLegacyLayoutFolders_TheNeverPublishedWinDesktopMcpPluginNames_AreNotScanned(string marketplace, string plugin)
    {
        // Arrange - the plugin was never published as win-desktop-mcp@win-desktop-mcp-marketplace (main went from
        // windows-app-manager straight to helmsman), so those folders are not this project's installs
        var cache = Path.Combine(_root, "plugins", "cache");
        var currentBin = Path.Combine(cache, "helmsman-marketplace", "helmsman", "4.0.0", "bin");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(cache, marketplace, plugin, "3.9.0", "bin", "layouts"));

        // Act
        var folders = LayoutStorageOptions.FindLegacyLayoutFolders(currentBin);

        // Assert
        folders.Should().Equal(Path.Combine(currentBin, "layouts"));
    }

    [Fact]
    public void PluginAndMarketplaceFolderNames_AreTheNewAndThePreviousNames()
    {
        LayoutStorageOptions.PluginFolderName.Should().Be("helmsman");
        LayoutStorageOptions.PreviousPluginFolderName.Should().Be("windows-app-manager");
        LayoutStorageOptions.MarketplaceFolderName.Should().Be("helmsman-marketplace");
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
        // Arrange - a dev checkout such as C:\code\Helmsman\bin next to other projects
        var currentBin = Path.Combine(_root, "code", "Helmsman", "bin");
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

        options.LayoutsPath.Should().Be(@"C:\Local\Helmsman\layouts");
        options.MigrationSources.Should().Contain(Path.Combine(baseDirectory, "layouts"));
        options.MigrationSources.Should().Contain(@"C:\Local\win-desktop-mcp\layouts");
        options.MigrationSources.Should().Contain(@"C:\Local\WindowsAppManagerMcp\layouts");
        options.Samples.Should().BeEquivalentTo(samples);
    }

    [Fact]
    public void Create_ListsBothEarlierAppDataFolders_TheNewerGenerationFirst()
    {
        // The store copies the most recently written of presets with the same name; on equal times the source listed
        // first wins, so the win-desktop-mcp folder (published on main) comes before the 3.x WindowsAppManagerMcp one
        var options = LayoutStorageOptions.Create(null, @"C:\Local", Path.Combine(_root, "app"), []);

        options.MigrationSources.Should().EndWith([@"C:\Local\win-desktop-mcp\layouts", @"C:\Local\WindowsAppManagerMcp\layouts"]);
    }

    [Fact]
    public void Create_WithOverride_CopiesFromNoAppDataFolder()
    {
        var baseDirectory = Path.Combine(_root, "app");

        var options = LayoutStorageOptions.Create(@"D:\Sync\layouts", @"C:\Local", baseDirectory, []);

        options.LayoutsPath.Should().Be(@"D:\Sync\layouts");
        options.MigrationSources.Should().Equal(Path.Combine(baseDirectory, "layouts"));
    }

    [Fact]
    public void Create_WithOverride_HasNoEarlierGeneration()
    {
        var options = LayoutStorageOptions.Create(@"D:\Sync\layouts", @"C:\Local", Path.Combine(_root, "app"), []);

        options.EarlierGenerations.Should().BeEmpty();
    }

    [Fact]
    public void Create_EarlierGenerations_AreTheWinDesktopMcpFolderThenThe3xOne_WithWhatEachAbsorbed()
    {
        // Arrange - Helmsman 4.1.0 next to an earlier Helmsman and two 3.x versions
        var cache = Path.Combine(_root, "plugins", "cache");
        var currentBin = Path.Combine(cache, "helmsman-marketplace", "helmsman", "4.1.0", "bin");
        var oldPlugin = Path.Combine(cache, "windows-app-manager-marketplace", "windows-app-manager");
        var earlierHelmsman = Path.Combine(cache, "helmsman-marketplace", "helmsman", "4.0.0");
        Directory.CreateDirectory(currentBin);
        Directory.CreateDirectory(Path.Combine(oldPlugin, "1.10.0", "bin"));
        Directory.CreateDirectory(Path.Combine(oldPlugin, "3.2.0", "bin"));
        Directory.CreateDirectory(Path.Combine(earlierHelmsman, "bin"));
        string[] threeXCache =
        [
            Path.Combine(oldPlugin, "1.10.0", "bin", "layouts"),
            Path.Combine(oldPlugin, "1.10.0", ".claude-plugin", "bin", "layouts"),
            Path.Combine(oldPlugin, "3.2.0", "bin", "layouts"),
            Path.Combine(oldPlugin, "3.2.0", ".claude-plugin", "bin", "layouts"),
        ];

        // Act
        var options = LayoutStorageOptions.Create(null, @"C:\Local", currentBin, []);

        // Assert - newest first; each folder is a migration source too
        var generations = options.EarlierGenerations!;
        generations.Select(g => g.LayoutsPath).Should().Equal(
            @"C:\Local\win-desktop-mcp\layouts", @"C:\Local\WindowsAppManagerMcp\layouts");
        options.MigrationSources.Should().Contain(generations.Select(g => g.LayoutsPath));
        generations[0].AbsorbedSources.Should().BeEquivalentTo([@"C:\Local\WindowsAppManagerMcp\layouts", .. threeXCache]);
        generations[1].AbsorbedSources.Should().BeEquivalentTo(threeXCache);
        generations.SelectMany(g => g.AbsorbedSources)
            .Should().NotContain(Path.Combine(currentBin, "layouts"))
            .And.NotContain(Path.Combine(earlierHelmsman, "bin", "layouts"));
    }

    [Fact]
    public void Create_RunningFromA3xVersionFolder_CountsItsOwnExeFolderAsAbsorbed()
    {
        // Arrange - a Helmsman exe placed under the 3.x install: both earlier generations scanned that version folder
        var currentBin = Path.Combine(_root, "plugins", "cache", "windows-app-manager-marketplace", "windows-app-manager", "3.2.0", "bin");
        Directory.CreateDirectory(currentBin);

        var options = LayoutStorageOptions.Create(null, @"C:\Local", currentBin, []);

        options.EarlierGenerations.Should().OnlyContain(g => g.AbsorbedSources.Contains(Path.Combine(currentBin, "layouts")));
    }

    [Fact]
    public void Create_OutsideThePluginCache_TheExeFolderIsNotAbsorbed()
    {
        var baseDirectory = Path.Combine(_root, "app");

        var options = LayoutStorageOptions.Create(null, @"C:\Local", baseDirectory, []);

        options.EarlierGenerations![0].AbsorbedSources.Should().Equal(@"C:\Local\WindowsAppManagerMcp\layouts");
        options.EarlierGenerations[1].AbsorbedSources.Should().BeEmpty();
    }

    [Fact]
    public void AppDataFolderNames_AreHelmsmanAndTheTwoEarlierNames()
    {
        LayoutStorageOptions.AppDataFolderName.Should().Be("Helmsman");
        LayoutStorageOptions.PreviousAppDataFolderName.Should().Be("win-desktop-mcp");
        LayoutStorageOptions.OriginalAppDataFolderName.Should().Be("WindowsAppManagerMcp");
    }

    [Fact]
    public void ForPath_DoesNotMigrateOrSeed()
    {
        var options = LayoutStorageOptions.ForPath(_root);

        options.LayoutsPath.Should().Be(_root);
        options.MigrationSources.Should().BeEmpty();
        options.Samples.Should().BeEmpty();
        options.EarlierGenerations.Should().BeNullOrEmpty();
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
