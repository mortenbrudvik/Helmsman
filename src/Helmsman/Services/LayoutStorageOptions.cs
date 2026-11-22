namespace Helmsman.Services;

/// <summary>
/// A bundled sample layout preset that is copied into the layouts folder on first use.
/// </summary>
/// <param name="FileName">File name to create in the layouts folder (no directories).</param>
/// <param name="Json">Preset JSON content.</param>
public sealed record SampleLayout(string FileName, string Json);

/// <summary>
/// Where layout presets are stored and how the folder is prepared the first time it is used.
/// </summary>
/// <remarks>
/// Presets used to live in <c>&lt;exe folder&gt;\layouts</c>. For the Claude Code plugin that is inside a
/// per-version cache folder, so every plugin update started with an empty folder. Presets now live in
/// <c>%LOCALAPPDATA%\Helmsman\layouts</c> (or <see cref="LayoutsPathEnvironmentVariable"/>).
/// Presets saved under the earlier folder names, <c>%LOCALAPPDATA%\win-desktop-mcp\layouts</c> and
/// <c>%LOCALAPPDATA%\WindowsAppManagerMcp\layouts</c>, are copied once into the new folder.
/// </remarks>
/// <param name="LayoutsPath">Folder that holds the preset JSON files.</param>
/// <param name="MigrationSources">
/// Folders used by older versions. On first use, their presets are copied (never moved, never
/// overwriting) into <paramref name="LayoutsPath"/>.
/// </param>
/// <param name="Samples">Sample presets seeded on first use, without overwriting existing presets.</param>
public sealed record LayoutStorageOptions(
    string LayoutsPath,
    IReadOnlyList<string> MigrationSources,
    IReadOnlyList<SampleLayout> Samples)
{
    /// <summary>
    /// Environment variable that overrides the layouts folder. It should be an absolute path;
    /// environment variables inside it (e.g. <c>%USERPROFILE%</c>) are expanded.
    /// </summary>
    public const string LayoutsPathEnvironmentVariable = "WAM_LAYOUTS_PATH";

    /// <summary>
    /// Folder name under %LOCALAPPDATA% used by this server.
    /// </summary>
    public const string AppDataFolderName = "Helmsman";

    /// <summary>
    /// Folder name under %LOCALAPPDATA% of the server published on main before the rename to Helmsman: the
    /// windows-app-manager 3.2.0 plugin whose exe was win-desktop-mcp.exe. Layout presets there are copied once;
    /// they are not moved.
    /// </summary>
    public const string PreviousAppDataFolderName = "win-desktop-mcp";

    /// <summary>
    /// Folder name under %LOCALAPPDATA% of release 3.2.0 and older (WindowsAppManagerMcp.exe). Layout presets there
    /// are copied once; they are not moved.
    /// </summary>
    public const string OriginalAppDataFolderName = "WindowsAppManagerMcp";

    /// <summary>
    /// The earlier %LOCALAPPDATA% folders whose layouts are copied on first use, newest generation first. When both
    /// hold a preset of the same name, LayoutPresetStore's rule for several sources applies: the most recently
    /// written file wins. On equal write times the folder listed first wins, so the newer generation's copy is kept.
    /// </summary>
    private static readonly string[] PreviousAppDataFolderNames = [PreviousAppDataFolderName, OriginalAppDataFolderName];

    /// <summary>
    /// Folder name of this plugin in the Claude Code plugin cache (<c>...\cache\&lt;marketplace&gt;\helmsman\&lt;version&gt;</c>):
    /// the plugin's name. VersionConsistencyTests keeps it equal to the name in .claude-plugin/plugin.json.
    /// </summary>
    public const string PluginFolderName = "helmsman";

    /// <summary>
    /// Folder name of the plugin in the cache before release 4.0.0 renamed it to <see cref="PluginFolderName"/>
    /// (<c>...\cache\windows-app-manager-marketplace\windows-app-manager\&lt;version&gt;</c>). Old versions that kept
    /// presets next to their exe live there, so both names are recognised and scanned.
    /// </summary>
    public const string PreviousPluginFolderName = "windows-app-manager";

    /// <summary>
    /// Folder name of this plugin's marketplace in the cache: the marketplace's name. VersionConsistencyTests keeps it
    /// equal to the name in .claude-plugin/marketplace.json.
    /// </summary>
    public const string MarketplaceFolderName = "helmsman-marketplace";

    /// <summary>The marketplace's folder name in the cache before release 4.0.0 renamed it to <see cref="MarketplaceFolderName"/>.</summary>
    public const string PreviousMarketplaceFolderName = "windows-app-manager-marketplace";

    private const string LayoutsFolderName = "layouts";
    private const string PluginCacheFolderName = "cache";
    private static readonly string[] PluginFolderNames = [PluginFolderName, PreviousPluginFolderName];
    private static readonly string[] KnownMarketplaceFolderNames = [MarketplaceFolderName, PreviousMarketplaceFolderName];
    private const string SampleResourcePrefix = "Helmsman.SampleLayouts.";

    /// <summary>
    /// Options for a plain folder: no migration and no sample seeding.
    /// </summary>
    public static LayoutStorageOptions ForPath(string layoutsPath) => new(layoutsPath, [], []);

    /// <summary>
    /// Options for the running server: the folder from <see cref="LayoutsPathEnvironmentVariable"/> or
    /// %LOCALAPPDATA%, legacy folders next to this and other installed plugin versions, and the bundled samples.
    /// </summary>
    public static LayoutStorageOptions FromEnvironment() => Create(
        Environment.GetEnvironmentVariable(LayoutsPathEnvironmentVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppContext.BaseDirectory,
        LoadEmbeddedSamples());

    public static LayoutStorageOptions Create(
        string? overridePath,
        string localAppDataPath,
        string baseDirectory,
        IReadOnlyList<SampleLayout> samples)
    {
        var sources = FindLegacyLayoutFolders(baseDirectory).ToList();
        if (string.IsNullOrWhiteSpace(overridePath))
        {
            sources.AddRange(PreviousAppDataFolderNames.Select(name => Path.Combine(localAppDataPath, name, LayoutsFolderName)));
        }

        return new(ResolveLayoutsPath(overridePath, localAppDataPath), sources, samples);
    }

    /// <summary>
    /// Returns the override path (quotes stripped, environment variables expanded) when it is set,
    /// otherwise <c>&lt;localAppDataPath&gt;\Helmsman\layouts</c>.
    /// </summary>
    public static string ResolveLayoutsPath(string? overridePath, string localAppDataPath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Environment.ExpandEnvironmentVariables(overridePath.Trim().Trim('"'));
        }

        return Path.Combine(localAppDataPath ?? string.Empty, AppDataFolderName, LayoutsFolderName);
    }

    /// <summary>
    /// Folders older versions stored presets in: <c>&lt;baseDirectory&gt;\layouts</c> and, when running from the
    /// Claude Code plugin cache (<c>...\cache\&lt;marketplace&gt;\&lt;plugin&gt;\&lt;version&gt;\...</c>, where the
    /// plugin folder is <see cref="PluginFolderName"/> or <see cref="PreviousPluginFolderName"/>), the matching
    /// folders of every other installed version under either plugin name in the marketplace this exe is installed
    /// from and in this project's own marketplaces (<see cref="MarketplaceFolderName"/>,
    /// <see cref="PreviousMarketplaceFolderName"/>): the rename moved the plugin to a new marketplace and plugin
    /// folder, so a 4.x install is not a sibling of the 3.x ones. Other marketplaces are never scanned, even for a
    /// plugin of the same name: a preset without authoredBy counts as the user's own and starts its programs without
    /// asking, so only folders this project's installs wrote are trusted. Nothing is scanned next to an arbitrary
    /// install location.
    /// </summary>
    public static IReadOnlyList<string> FindLegacyLayoutFolders(string baseDirectory)
    {
        var folders = new List<string>();
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return folders;

        string baseDir;
        try
        {
            baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return folders;
        }

        folders.Add(Path.Combine(baseDir, LayoutsFolderName));

        var versionDir = FindPluginVersionFolder(baseDir);
        var ownMarketplaceDir = versionDir?.Parent?.Parent;
        var cacheDir = ownMarketplaceDir?.Parent;
        if (versionDir == null || ownMarketplaceDir == null || cacheDir == null)
            return folders;

        // The exe may sit in <version>\bin or <version>\.claude-plugin\bin depending on how the
        // plugin was installed, so look in both for every other installed version.
        var relativeExeFolders = new[]
            {
                Path.GetRelativePath(versionDir.FullName, baseDir),
                "bin",
                Path.Combine(".claude-plugin", "bin")
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var otherVersion in FindInstalledPluginVersions(cacheDir, ownMarketplaceDir.Name))
        {
            if (string.Equals(otherVersion.FullName, versionDir.FullName, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var relative in relativeExeFolders)
            {
                folders.Add(Path.Combine(otherVersion.FullName, relative, LayoutsFolderName));
            }
        }

        return folders;
    }

    /// <summary>
    /// The version folders of this plugin, under its current or previous name, in the marketplace this exe is
    /// installed from and in this project's current and previous marketplace:
    /// <c>&lt;cacheDir&gt;\&lt;marketplace&gt;\&lt;plugin&gt;\&lt;version&gt;</c>. A folder that is missing or cannot
    /// be listed is skipped.
    /// </summary>
    private static List<DirectoryInfo> FindInstalledPluginVersions(DirectoryInfo cacheDir, string ownMarketplaceName)
    {
        var versions = new List<DirectoryInfo>();
        var marketplaces = new[] { ownMarketplaceName }.Concat(KnownMarketplaceFolderNames)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var marketplace in marketplaces)
        {
            foreach (var plugin in PluginFolderNames)
            {
                var pluginDir = new DirectoryInfo(Path.Combine(cacheDir.FullName, marketplace, plugin));
                if (pluginDir.Exists)
                    versions.AddRange(EnumerateDirectoriesOrEmpty(pluginDir));
            }
        }

        return versions;
    }

    private static List<DirectoryInfo> EnumerateDirectoriesOrEmpty(DirectoryInfo folder)
    {
        try
        {
            return folder.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Best effort: migration from other versions is optional.
            return [];
        }
    }

    private static bool IsPluginFolderName(string name) =>
        PluginFolderNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sample presets compiled into the assembly from config/layouts/*.json.
    /// </summary>
    public static IReadOnlyList<SampleLayout> LoadEmbeddedSamples()
    {
        var assembly = typeof(LayoutStorageOptions).Assembly;
        var samples = new List<SampleLayout>();

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(SampleResourcePrefix, StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                continue;

            using var reader = new StreamReader(stream);
            samples.Add(new SampleLayout(resourceName[SampleResourcePrefix.Length..], reader.ReadToEnd()));
        }

        return samples;
    }

    /// <summary>
    /// Walks up from the exe folder to the <c>&lt;version&gt;</c> folder of a plugin cache install
    /// (<c>...\cache\&lt;marketplace&gt;\&lt;plugin&gt;\&lt;version&gt;</c>, the plugin folder under its current or
    /// previous name), or returns null.
    /// </summary>
    private static DirectoryInfo? FindPluginVersionFolder(string baseDir)
    {
        // The exe is at most two levels below the version folder (bin or .claude-plugin\bin).
        var candidate = new DirectoryInfo(baseDir);
        for (var depth = 0; depth < 3 && candidate != null; depth++, candidate = candidate.Parent)
        {
            var pluginDir = candidate.Parent;
            var cacheDir = pluginDir?.Parent?.Parent;
            if (pluginDir != null && cacheDir != null &&
                IsPluginFolderName(pluginDir.Name) &&
                cacheDir.Name.Equals(PluginCacheFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}
