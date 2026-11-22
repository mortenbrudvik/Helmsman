using System.Reflection;
using System.Text.RegularExpressions;
using static Helmsman.Tests.Fixtures.IlCalls;

namespace Helmsman.Tests.Unit.Architecture;

/// <summary>
/// Keeps every setting on SettingNames' rule (a HELMSMAN_ name, and its WAM_ name from before release 4.0.0 read when the
/// new one is not set or empty): no server type but SettingNames calls Environment.GetEnvironmentVariable(s), directly or
/// as a delegate, apart from ExecutableResolver's PATH and PATHEXT, and SettingNames hands out no public delegate over the
/// environment, so an options class reads its variable through SettingNames.Read. Other ways to reach the environment
/// (Environment.ExpandEnvironmentVariables, a ProcessStartInfo's Environment) are not checked. Every HELMSMAN_ name in the
/// server's source is one of SettingNames.All, so a setting added later cannot be read, documented or named in a message
/// without being registered there. These tests read the server's IL, its public members and its source files only.
/// </summary>
public sealed partial class SettingNamesArchitectureTests
{
    private static readonly Assembly ServerAssembly = typeof(SettingNames).Assembly;

    /// <summary>The types that may read an environment variable by name, with the names they may read (null: any).</summary>
    private static readonly Dictionary<Type, string[]?> AllowedReaders = new()
    {
        // The registry itself: the one place a setting is read, under its new name and then its old one
        [typeof(SettingNames)] = null,
        // Where a command is looked up: the search path and the extensions the shell runs, not a setting of this server
        [typeof(ExecutableResolver)] = ["PATH", "PATHEXT"],
    };

    [GeneratedRegex(@"\bHELMSMAN_[A-Z][A-Z0-9_]*[A-Z0-9]\b")]
    private static partial Regex HelmsmanName();

    private static IEnumerable<Type> ServerTypes() =>
        ServerAssembly.GetTypes()
            .Where(t => t.DeclaringType == null)
            .Where(t => t.Namespace?.StartsWith("Coverlet.", StringComparison.Ordinal) != true);

    private static bool ReadsTheEnvironment(MethodBase? method) =>
        method?.DeclaringType == typeof(Environment) &&
        method.Name.StartsWith(nameof(Environment.GetEnvironmentVariable), StringComparison.Ordinal);

    [Fact]
    public void OnlySettingNames_ReadsAnEnvironmentVariableByName()
    {
        // Act - every type whose code (lambdas included) calls Environment.GetEnvironmentVariable(s) or takes it as a delegate
        var readers = ServerTypes()
            .Where(type => MethodsOf(type).SelectMany(CalledMethods).Any(ReadsTheEnvironment))
            .ToList();

        // Assert
        readers.Should().BeEquivalentTo(AllowedReaders.Keys,
            "an options class reads its variable through SettingNames.Read (passing a reader to test with), so a setting is " +
            "always read under its HELMSMAN_ name and then its old WAM_ name");
    }

    [Fact]
    public void SettingNames_HandsOutNoEnvironmentReader()
    {
        // A public delegate over the process environment (a field, a property, or a method that returns one) would let an
        // options class read its HELMSMAN_ name with no fallback, and the check above would not see it: it finds only calls
        // of Environment.GetEnvironmentVariable(s).
        PublicDelegateMembers(typeof(SettingNames)).Should().BeEmpty(
            "a caller passes null to SettingNames.Read for the process environment");
    }

    [Fact]
    public void TheDelegateCheck_FindsAPublicStaticField_Property_AndMethodThatReturnsADelegate()
    {
        // Guards the guard: each way a type could hand out a reader is seen, and a method returning a string is not
        PublicDelegateMembers(typeof(DelegateSamples)).Should().BeEquivalentTo(
            nameof(DelegateSamples.Field), nameof(DelegateSamples.Property), "get_" + nameof(DelegateSamples.Property),
            nameof(DelegateSamples.Method));
    }

    /// <summary>The names of the public static fields, properties and methods of <paramref name="type"/> that are or return a delegate.</summary>
    private static IEnumerable<string> PublicDelegateMembers(Type type) =>
        type.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(member => member switch
            {
                PropertyInfo property => typeof(Delegate).IsAssignableFrom(property.PropertyType),
                FieldInfo field => typeof(Delegate).IsAssignableFrom(field.FieldType),
                MethodInfo method => typeof(Delegate).IsAssignableFrom(method.ReturnType),
                _ => false
            })
            .Select(member => member.Name);

    private static class DelegateSamples
    {
        public static readonly Func<string, string?> Field = _ => null;

        public static Func<string, string?> Property { get; } = _ => null;

        public static Func<string, string?> Method() => _ => null;

        public static string? NotADelegate(string name) => name;
    }

    [Fact]
    public void ExecutableResolver_ReadsOnlyPathAndPathExt()
    {
        foreach (var (type, names) in AllowedReaders.Where(entry => entry.Value != null))
        {
            var read = MethodsOf(type)
                .Select(InstructionsOf)
                .SelectMany(instructions => instructions
                    .Select((instruction, index) => (instruction, index))
                    .Where(x => ReadsTheEnvironment(x.instruction.Method))
                    .Select(x => x.index > 0 ? instructions[x.index - 1].Text : null))
                .ToList();

            read.Should().NotBeEmpty().And.OnlyContain(name => names!.Contains(name), "{0} reads only {1}", type.Name, string.Join(", ", names!));
        }
    }

    [Fact]
    public void EveryHelmsmanNameInTheServersSource_IsARegisteredSetting()
    {
        // Arrange
        var sourceRoot = RepositoryFiles.PathOf("src");
        var files = Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(f => Path.GetExtension(f) is ".cs" or ".json" or ".md" or ".txt")
            .ToList();

        // Act
        var names = files
            .SelectMany(f => HelmsmanName().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetRelativePath(sourceRoot, f), Name: m.Value)))
            .ToList();
        var unregistered = names.Where(n => !SettingNames.All.Contains(n.Name)).Select(n => $"{n.File}: {n.Name}").Distinct();

        // Assert
        files.Should().Contain(f => f.EndsWith("SettingNames.cs", StringComparison.Ordinal), "the scan must reach the registry");
        names.Select(n => n.Name).Distinct().Should().Contain(SettingNames.All, "the scan finds every registered name");
        unregistered.Should().BeEmpty(
            "a HELMSMAN_ setting must be listed in SettingNames.All, so its WAM_ name is read when it is not set and " +
            "get_server_info warns about the old name");
    }

    [Fact]
    public void TheNamePattern_FindsASettingName_AndNotThePrefixAlone()
    {
        HelmsmanName().IsMatch("set HELMSMAN_NEW_THING=1").Should().BeTrue();
        HelmsmanName().IsMatch("the HELMSMAN_ name wins").Should().BeFalse();
        HelmsmanName().IsMatch("HELMSMAN_*").Should().BeFalse();
    }
}
