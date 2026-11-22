using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Helmsman.Infrastructure;
using Helmsman.Tools;

namespace Helmsman.Tests.Unit.Architecture;

/// <summary>
/// The direction of the server's layers: each type belongs to a layer by its namespace (<see cref="Layers"/>), and may depend
/// only on the types of its own layer and of the layers its row allows, so the code points inward: Tools, Prompts, Resources
/// and Infrastructure (outer) use the ports in Services.Interfaces and the Models; Services implement logic against those ports;
/// Native implements them; and the Models depend on nothing. Only the MCP-facing layers may name the MCP SDK
/// (ModelContextProtocol), so the logic below them can be reused and tested without it. The dependencies are read from the
/// built assembly, not from source (<see cref="TypeDependencies"/>): base types and interfaces, member signatures, generic
/// arguments, attributes and their arguments, locals, catch types and every member and type a method body names, with nested,
/// closure and state machine types counted as their declaring type's.
/// </summary>
/// <remarks>
/// <para>Today's violations are in a checked-in baseline, NamespaceDependencyBaseline.txt next to this file, one line per
/// (offending type -> type it depends on), or (source file -> type whose constants it names) for the "(constant)" rules,
/// grouped by rule with a line on how each group is meant to go away. A dependency that is not in it fails
/// <see cref="EveryDependency_FollowsTheLayering_OrIsInTheBaseline"/>, including a new target of a type that is already in
/// it; a line that no longer occurs fails <see cref="EveryBaselineLine_StillOccurs"/>, so the baseline only shrinks. With
/// UPDATE_SNAPSHOTS=1 (see TESTING.md; refused in CI) that test and <see cref="TheBaseline_IsGroupedAndSorted"/> delete the
/// stale lines and sort the file instead of failing; nothing ever adds a line. A new line is added by hand, with a reason,
/// only when the dependency cannot be avoided yet.</para>
/// <para>Blind spots: a constant (a const field, or an enum member used as a value) is copied into the code that uses it,
/// so a use of another layer's constant leaves no reference in the IL (<see cref="TheScan_DoesNotSeeAConstant"/>), as in
/// [ToolEnum(ToolErrorReporting.InvalidArgumentsErrorCode, ...)] on a model. So the source files are also searched for
/// Type.Constant names of the server's const fields (<see cref="ConstantViolationsIn"/>, the "(constant)" rules). That
/// search is textual: it misses a constant named through a using static or an alias, and an enum member that is only
/// converted to a number ((int)SomeEnum.Value); the IL sees an enum wherever a signature, local or field has its type. A
/// nameof is a string, and a type named only in a doc comment or a using directive is not compiled, so those are not seen
/// either. The signature of a calli (a call through a function pointer, an InlineSig token) is not read, so a type named only
/// there is not seen. The assembly's own attributes have no owner type and are not read.</para>
/// <para>The layers' namespaces are exact, and Host has none: Program and ServiceRegistration are placed by type
/// (<see cref="TypeLayers"/>), so a new namespace or a new type in the root namespace fails
/// <see cref="EveryServerType_IsInExactlyOneLayer"/> until it is given a layer. The MCP SDK is recognised by its namespaces and
/// by its assemblies, which also define types in Microsoft.Extensions.*.</para>
/// </remarks>
public class NamespaceDependencyTests
{
    private const string McpSdk = "MCP SDK";
    private const string ConstantSuffix = " (constant)";
    private const string Unknown = "(no layer)";

    private static readonly Assembly ServerAssembly = typeof(NativeWindowWrapper).Assembly;

    /// <summary>The baseline of today's violations, relative to the repository root.</summary>
    private static readonly string[] BaselineRelativePath =
        ["tests", "Helmsman.Tests", "Unit", "Architecture", "NamespaceDependencyBaseline.txt"];

    /// <summary>
    /// The layers, inner first: each one's namespaces (exact; a new namespace has no layer until it is added here), the layers it
    /// may depend on besides itself, whether it may name the MCP SDK, and why.
    /// </summary>
    private static readonly Layer[] Layers =
    [
        new("Models", ["Helmsman.Models", "Helmsman.Models.Results"], [], McpSdk: false,
            "The DTOs, enums and error codes every layer passes around describe data and know no code that acts on it."),
        new("Services.Interfaces", ["Helmsman.Services.Interfaces"], ["Models"], McpSdk: false,
            "The ports say what services need from the desktop and from each other in terms of models, so either side can be replaced and faked."),
        new("Native", ["Helmsman.Native"], ["Services.Interfaces", "Models"], McpSdk: false,
            "The P/Invoke and COM wrappers implement the ports and know nothing of the logic that uses them."),
        new("Services", ["Helmsman.Services"], ["Services.Interfaces", "Models"], McpSdk: false,
            "The logic reaches the desktop and other services only through ports, so it runs against fakes and knows nothing of MCP or of the wrappers."),
        new("Infrastructure", ["Helmsman.Infrastructure"], ["Services.Interfaces", "Models"], McpSdk: true,
            "The MCP plumbing (call-tool filters, JSON and error reporting, the step runner) works through ports, never a concrete service, wrapper or tool."),
        new("Tools", ["Helmsman.Tools"], ["Infrastructure", "Services.Interfaces", "Models"], McpSdk: true,
            "The tool classes bind arguments, call services through their ports and shape results with Infrastructure's helpers."),
        new("Prompts", ["Helmsman.Prompts"], ["Infrastructure", "Services.Interfaces", "Models"], McpSdk: true,
            "The MCP prompts are an outer layer like the tools."),
        new("Resources", ["Helmsman.Resources"], ["Infrastructure", "Services.Interfaces", "Models"], McpSdk: true,
            "The MCP resources are an outer layer like the tools."),
        new("Host", [], ["Models", "Services.Interfaces", "Native", "Services", "Infrastructure", "Tools", "Prompts", "Resources"], McpSdk: true,
            "Program and the composition root (ServiceRegistration) wire every layer together, so they may use all of them."),
    ];

    /// <summary>
    /// Types whose layer is not their namespace's (or that have no namespace of a layer), and why. Keep it short: a type in the
    /// wrong folder should move. Host has no namespace of its own, so a new type in the root namespace has no layer until it is
    /// placed here.
    /// </summary>
    private static readonly Dictionary<string, (string Layer, string Why)> TypeLayers = new()
    {
        ["Program"] = ("Host", "Program.cs's top-level statements build and run the host"),
        ["Helmsman.Infrastructure.ServiceRegistration"] =
            ("Host", "the composition root: it registers every wrapper, service and tool, which is its job, not a layering violation"),
        ["Helmsman.ServerVersion"] =
            ("Infrastructure", "the version serverInfo reports, read from the server's and the MCP SDK's assemblies: MCP plumbing in the root namespace"),
    };

    /// <summary>
    /// Code in the server assembly that the server's sources do not declare, which is in no layer and is neither checked nor a
    /// violation to use: the WinRT projection (Windows.*, its ABI.* marshalers, WinRT.*) and CsWinRT's helpers in System.*, the
    /// regular expression source generator's types, and Coverlet's trackers when the tests collect coverage.
    /// </summary>
    private static readonly string[] GeneratedNamespaces =
    [
        "WinRT", "WinRT.GenericTypeInstantiations", "System", "System.Threading.Tasks", "System.Runtime.InteropServices.WindowsRuntime",
        "System.Text.RegularExpressions.Generated", "Coverlet.Core.Instrumentation.Tracker",
    ];

    /// <summary>How each group of the baseline is meant to go away. A rule without a line here gets <see cref="DefaultFix"/>.</summary>
    private static readonly Dictionary<string, string> Fixes = new()
    {
        ["Models -> Services.Interfaces"] =
            "move the data the models carry (the Uia*State enums, PixelView) to Models; LaunchRequest, which carries an IProcessHandle, belongs next to that port",
        ["Models -> Native (constant)"] =
            "move the Win32 values the models compute with (NativeEnums' WDA_* and input constants) to a Models type that NativeEnums refers to",
        ["Models -> Services"] =
            "move CaptureOutputMode to Models, and the modifier-name check KeyList calls (PressKeysParser.IsModifierName) to Models",
        ["Models -> Services (constant)"] = "move ElementReadService.MaxTextLength to a Models type that both use",
        ["Models -> Infrastructure"] = "move AppText (the cap on application text that results apply) to Models",
        ["Models -> Infrastructure (constant)"] =
            "move ToolErrorReporting's error codes (InvalidArgumentsErrorCode and the rest) to a Models type that ToolErrorReporting refers to",
        ["Models -> Tools"] = "move WindowInfoDto and LaunchPlacementResultDto from Tools to Models",
        ["Services.Interfaces -> Services"] =
            "move the types the ports name (ElementCriteria, SystemKeyboardHotkeys, OcrTextMatcher, MinimizedWindowRestore) to Models or Services.Interfaces",
        ["Services.Interfaces -> Services (constant)"] = "move CdpEndpointValidator.Host (the loopback address ICdpService's endpoints use) to Models",
        ["Services.Interfaces -> MCP SDK"] =
            "let IAuditLog take a model of the call's outcome, which AuditLogToolFilter (Infrastructure) builds from the CallToolResult",
        ["Native -> Services"] =
            "move the hotkey types NativeKeyboardLayoutWrapper returns (InputMethodHotkey, LanguageToggle, SystemKeyboardHotkeys) to Models or Services.Interfaces",
        ["Native -> Services (constant)"] = "move the constants the wrappers read (SystemKeyboardHotkeys', CaptureMethods') to Models",
        ["Services -> Native"] =
            "use the ports instead of the concrete wrappers' static helpers, and move UiaControlTypes and the NativeEnums values the logic needs to Models",
        ["Services -> Native (constant)"] =
            "move the Win32 constants the logic computes with (NativeEnums' VK_*, WS_*, WDA_* and the like) to a Models type that NativeEnums refers to",
        ["Services -> Infrastructure"] =
            "move ToolErrorException and AppText inward (Models), give services ports for what they take from the filters (InputGateToolFilter, AuditScope, ToolsetSelection, ToolJson, CommandLineRedactor), and pass them the version instead of ServerVersion",
        ["Services -> Infrastructure (constant)"] =
            "move ToolErrorReporting's error codes to Models (as for Models), and the AppText, AuditRecord, ServerIdentity and ToolsetSelection constants services read",
        ["Services -> Tools"] = "move UIElementTextFormat (it formats a model) from Tools to Services, where DesktopStateService and the tools can both use it",
        ["Services -> MCP SDK"] =
            "let AuditLog take a model of the outcome (see IAuditLog), and move what ServerStartupLog reads from the SDK's options to Infrastructure",
        ["Infrastructure -> Services"] =
            "use the ports of the input gate and audit log, and let a service behind a port parse and guard keys for StepRunner (KeySpecParser, PressKeysParser, SendKeysParser, HostGuard and the rest)",
        ["Infrastructure -> Services (constant)"] =
            "move the limits AuditRecord and StepRunner read (MonitorSelection's, MonitorIdentity's, ElementWaitService's, WindowWaitService's) to Models",
        ["Infrastructure -> Prompts"] = "ResourcesAndPrompts registers the prompts: move it to the composition root (Host), or its lists into Prompts and Resources",
        ["Infrastructure -> Prompts (constant)"] = "as for Infrastructure -> Prompts",
        ["Infrastructure -> Resources"] = "ResourcesAndPrompts registers the resources: move it to the composition root (Host), or its lists into Prompts and Resources",
        ["Infrastructure -> Resources (constant)"] = "as for Infrastructure -> Resources",
        ["Tools -> Services"] =
            "call services through their ports; move the helpers tools share with services (ElementRefs, ElementCriteria, WindowTitles, BrowserNavigationUrls, CaptureOutputMode...) to Models, and IWorkspaceService to Services.Interfaces",
        ["Tools -> Services (constant)"] =
            "move the limits and defaults the tools state (ElementWaitService's, WindowWaitService's, InputSimulationService's and the rest) to Models",
        ["Resources -> Tools"] =
            "move the DTOs DesktopResources returns (WindowInfoDto, MonitorsResult, LayoutDetailDto) to Models, and call the services, not the tool classes",
    };

    /// <summary>What each outermost server type (with its nested types) depends on, read once per test run.</summary>
    private static readonly Lazy<Dictionary<Type, IReadOnlySet<Type>>> ServerDependencies =
        new(() => ServerTypes().ToDictionary(owner => owner, TypeDependencies.Of));

    private static readonly Lazy<IReadOnlyList<Violation>> CurrentViolations = new(FindViolations);

    private static string BaselinePath => RepositoryFiles.PathOf(BaselineRelativePath);

    private const string BaselineHeader =
        "# Today's layering violations, which NamespaceDependencyTests lets pass until they are fixed: one line per (offending\n" +
        "# type -> type it depends on), grouped under \"## rule: how the group is meant to go away\". Do not add a line to make\n" +
        "# the test pass: move the type to the layer it belongs in, or depend on an interface in Services.Interfaces. Only when the\n" +
        "# dependency cannot be avoided yet, add \"Owner -> Target  # reason\" under its rule. The test fails on a line that no\n" +
        "# longer occurs: delete it, or run the tests with UPDATE_SNAPSHOTS=1, which deletes stale lines and sorts the file but\n" +
        "# never adds one (see TESTING.md).\n";

    [Fact]
    public void EveryDependency_FollowsTheLayering_OrIsInTheBaseline()
    {
        // Arrange
        var baseline = ReadBaseline().Select(e => e.Key).ToHashSet();

        // Act
        var added = CurrentViolations.Value.Where(v => !baseline.Contains(v)).ToList();

        // Assert
        if (added.Count == 0)
        {
            return;
        }

        var lines = added
            .GroupBy(v => v.Rule)
            .OrderBy(g => RuleRank(g.Key))
            .Select(g => $"## {g.Key}: {FixOf(g.Key)}\n" +
                         string.Join("\n", g.OrderBy(v => v.Owner, StringComparer.Ordinal).ThenBy(v => v.Target, StringComparer.Ordinal)
                             .Select(v => $"{v.Owner} -> {v.Target}  # <why it cannot be avoided yet>")));
        Assert.Fail(
            $"{added.Count} dependencies go against the direction of the layers (NamespaceDependencyTests.Layers):\n" +
            string.Join("\n", added.Select(Describe)) + "\n\n" +
            "Move the type to the layer it belongs in, or depend on an interface in Services.Interfaces (or a model) instead. Only " +
            $"when that cannot be done yet, paste these lines at the end of {string.Join('/', BaselineRelativePath)}, replace each " +
            $"<why ...> with the reason, and run the tests with {Snapshots.UpdateVariable}=1 once to merge them into their groups:\n\n" +
            string.Join("\n\n", lines));
    }

    [Fact]
    public void EveryBaselineLine_StillOccurs()
    {
        // Arrange
        var entries = ReadBaseline();
        var current = CurrentViolations.Value.ToHashSet();

        // Act
        var stale = entries.Where(e => !current.Contains(e.Key)).ToList();

        // Assert
        if (stale.Count == 0)
        {
            return;
        }

        if (Snapshots.UpdateRequested)
        {
            RewriteBaseline(entries.Where(e => current.Contains(e.Key)));
            return;
        }

        Assert.Fail(
            $"{stale.Count} lines of {string.Join('/', BaselineRelativePath)} no longer occur, so the baseline must shrink. Delete " +
            $"them, or run the tests with {Snapshots.UpdateVariable}=1 (see TESTING.md), which deletes stale lines and never adds one:\n" +
            string.Join("\n", stale.Select(e => $"  {e.Key.Rule}: {e.Key.Owner} -> {e.Key.Target}")));
    }

    [Fact]
    public void TheBaseline_IsGroupedAndSorted()
    {
        // A deterministic order keeps the diff of a fix to the lines it removes
        var text = Snapshots.ReadNormalized(BaselinePath);
        text.Should().NotBeNull("{0} must exist", BaselinePath);
        var entries = ParseBaseline(text!);
        var canonical = RenderBaseline(entries);
        if (text == canonical)
        {
            return;
        }

        if (Snapshots.UpdateRequested)
        {
            var current = CurrentViolations.Value.ToHashSet();
            RewriteBaseline(entries.Where(e => current.Contains(e.Key)));
            return;
        }

        var (have, want) = (text!.Split('\n'), canonical.Split('\n'));
        var line = Enumerable.Range(0, Math.Max(have.Length, want.Length))
            .First(i => i >= have.Length || i >= want.Length || have[i] != want[i]);
        Assert.Fail(
            $"{string.Join('/', BaselineRelativePath)} is not in its canonical form: the header as NamespaceDependencyTests writes " +
            "it, then one '## rule: fix' group per rule in the order of the layers, each with its lines sorted and none repeated. " +
            $"Run the tests with {Snapshots.UpdateVariable}=1 (see TESTING.md) to sort it; that never adds a line.\n" +
            $"First difference, line {line + 1}:\n  is:        {(line < have.Length ? have[line] : "(end of file)")}\n" +
            $"  should be: {(line < want.Length ? want[line] : "(end of file)")}");
    }

    [Fact]
    public void EveryServerType_IsInExactlyOneLayer()
    {
        // Each namespace names one layer, and each override names a server type and a layer
        Layers.SelectMany(l => l.Namespaces).Should().OnlyHaveUniqueItems("a namespace belongs to one layer");
        Layers.Select(l => l.Name).Should().OnlyHaveUniqueItems();
        foreach (var (name, (layer, _)) in TypeLayers)
        {
            ServerAssembly.GetType(name).Should().NotBeNull("the override {0} must name a server type", name);
            Layers.Select(l => l.Name).Should().Contain(layer);
        }

        // Every type of the server's own code has a layer: a new namespace fails until it is added to Layers
        var unplaced = ServerAssembly.GetTypes()
            .Where(t => LayerOf(t) == Unknown)
            .Select(t => $"{IlCalls.OutermostType(t).Namespace ?? "(global)"}: {IlCalls.OutermostType(t).FullName}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        unplaced.Should().BeEmpty(
            "every server type belongs to exactly one layer by its namespace; add a new namespace to NamespaceDependencyTests.Layers " +
            "(with what it may use), or to GeneratedNamespaces if the compiler or a generator writes it");
        ServerAssembly.GetTypes().Count(t => LayerOf(t) is { } layer && layer != Unknown).Should().BeGreaterThan(1000);
    }

    [Fact]
    public void TheAllowedDirections_NameKnownLayers_AndHaveNoCycle()
    {
        var names = Layers.Select(l => l.Name).ToHashSet();
        Layers.SelectMany(l => l.MayUse).Should().BeSubsetOf(names);
        Layers.Should().OnlyContain(l => !l.MayUse.Contains(l.Name), "a layer may always use itself");
        Fixes.Keys.Should().OnlyContain(rule => RuleRank(rule) < int.MaxValue, "each fix is for a rule 'From -> To' of known layers");

        // Inner first: each layer may use only layers listed before it, so no two layers may use each other
        for (var i = 0; i < Layers.Length; i++)
        {
            var earlier = Layers.Take(i).Select(l => l.Name).ToList();
            Layers[i].MayUse.Should().BeSubsetOf(earlier, "{0} may only use layers inside it", Layers[i].Name);
        }
    }

    [Theory]
    [InlineData(typeof(LayeringSamples.ThroughATypeofAttributeArgument), typeof(UIElementTextFormat))]
    [InlineData(typeof(LayeringSamples.ThroughAnEnumAttributeArgument), typeof(NativeClipboardWrapper.FormatPresence))]
    [InlineData(typeof(LayeringSamples.ThroughAMethodBodyOnly), typeof(BrowserNavigationUrls))]
    [InlineData(typeof(LayeringSamples.ThroughAGenericArgumentOnly), typeof(ProcessSnapshotEntry))]
    [InlineData(typeof(LayeringSamples.ThroughALambdaClosure), typeof(BrowserNavigationUrls))]
    [InlineData(typeof(LayeringSamples.ThroughAnAsyncStateMachine), typeof(ServerVersion))]
    [InlineData(typeof(LayeringSamples.ThroughACatchClause), typeof(ToolErrorException))]
    [InlineData(typeof(LayeringSamples.ThroughATypeTokenOnly), typeof(UIElementTextFormat))]
    [InlineData(typeof(LayeringSamples.ThroughAnMcpSdkGenericArgument), typeof(ModelContextProtocol.Protocol.CallToolResult))]
    [InlineData(typeof(LayeringSamples.ThroughAFieldTypeOnly), typeof(NativeMonitorWrapper))]
    [InlineData(typeof(LayeringSamples.ThroughAPropertyTypeOnly), typeof(UiaTree))]
    [InlineData(typeof(LayeringSamples.ThroughAnEventTypeOnly), typeof(ProcessSnapshotEntry))]
    [InlineData(typeof(LayeringSamples.ThroughABaseClass), typeof(ToolErrorException))]
    [InlineData(typeof(LayeringSamples.ThroughAGenericConstraint), typeof(INativeWindowWrapper))]
    [InlineData(typeof(LayeringSamples.ThroughANamedAttributeArgument), typeof(WindowManagerTools))]
    [InlineData(typeof(LayeringSamples.ThroughAStaticFieldRead), typeof(AccessibilityWarmup))]
    [InlineData(typeof(LayeringSamples.ThroughACast), typeof(OcrEngineWrapper))]
    [InlineData(typeof(LayeringSamples.ThroughAnIsCheck), typeof(Uia3Tree))]
    [InlineData(typeof(LayeringSamples.ThroughABox), typeof(ProcessSnapshotEntry))]
    [InlineData(typeof(LayeringSamples.ThroughAnIterator), typeof(ServerVersion))]
    [InlineData(typeof(LayeringSamples.ThroughANestedGenericArgument), typeof(NativeProcessWrapper))]
    [InlineData(typeof(LayeringSamples.ThroughAnArray), typeof(NativeMonitorWrapper))]
    [InlineData(typeof(LayeringSamples.ThroughAnMcpSdkExtensionType), typeof(Microsoft.Extensions.DependencyInjection.IMcpServerBuilder))]
    public void TheScan_SeesADependency_AndReportsItAsAViolation(Type sample, Type target)
    {
        // Arrange - the sample stands in for a Models type, which may use no other layer and not the MCP SDK
        var outer = IlCalls.OutermostType(target);
        var expectedRule = target.Assembly == ServerAssembly ? $"Models -> {LayerOf(target)}" : $"Models -> {McpSdk}";

        // Act
        var dependencies = TypeDependencies.Of(sample);
        var violations = ViolationsOf(sample, "Models").ToList();

        // Assert
        dependencies.Should().Contain(target.IsGenericType ? target.GetGenericTypeDefinition() : target);
        violations.Should().Contain(new Violation(expectedRule, DisplayName(sample), DisplayName(target.Assembly == ServerAssembly ? outer : target)));
    }

    [Fact]
    public void TheScan_DoesNotSeeAConstant()
    {
        // The blind spot the source check covers: a const is copied into the IL as a value (ldstr "InvalidArguments")
        TypeDependencies.Of(typeof(LayeringSamples.ThroughAConstantOnly)).Should().NotContain(typeof(ToolErrorReporting));
    }

    [Fact]
    public void TheSourceCheck_SeesAnotherLayersConstant_InTheLayersThatMayNotUseIt()
    {
        // Arrange - a Models file naming Infrastructure and Services constants (qualified, unqualified, in an attribute, in an
        // interpolation hole after a string's "//", after a string's "/*" and a char literal's quote), others in comments, and
        // its own layer's; in a Tools file only the Services ones are against the layering
        const string Uses = """
            [ToolEnum(Helmsman.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "value")]
            public enum Sample { A }
            public static class Limits
            {
                public const int Max = Services.ElementReadService.MaxTextLength; // NativeEnums.WDA_NONE
                public static string Url() => $"http://{Services.CdpEndpointValidator.Host}:9222"; // ServerIdentity.Name
                public static string Accept() => "image/*" + AppText.MaxLength + @"C:\" + '"' + "x";
                /* ToolErrorReporting.ToolErrorCode */
                public static int Plain() => BrowserContentOptions.MaxMaxLength + ElementReadService . MaxTextLength;
            }
            """;

        // Act
        var models = ConstantViolationsIn("src/Helmsman/Models/Sample.cs", "namespace Helmsman.Models;\n" + Uses).ToList();
        var tools = ConstantViolationsIn("src/Helmsman/Tools/Sample.cs", "namespace Helmsman.Tools;\n" + Uses).ToList();

        // Assert
        models.Should().BeEquivalentTo(
        [
            new Violation("Models -> Infrastructure (constant)", "src/Helmsman/Models/Sample.cs", "Infrastructure.ToolErrorReporting"),
            new Violation("Models -> Infrastructure (constant)", "src/Helmsman/Models/Sample.cs", "Infrastructure.AppText"),
            new Violation("Models -> Services (constant)", "src/Helmsman/Models/Sample.cs", "Services.ElementReadService"),
            new Violation("Models -> Services (constant)", "src/Helmsman/Models/Sample.cs", "Services.CdpEndpointValidator"),
        ]);
        tools.Should().BeEquivalentTo(
        [
            new Violation("Tools -> Services (constant)", "src/Helmsman/Tools/Sample.cs", "Services.ElementReadService"),
            new Violation("Tools -> Services (constant)", "src/Helmsman/Tools/Sample.cs", "Services.CdpEndpointValidator"),
        ], "Tools may use Infrastructure and Models, not a concrete service");
        ConstantUses("var a = \"x//y\"; var b = Limits.Max; // Other.Name\nvar c = '/'; /* Hidden.Value */ var d = Shown.Value;")
            .Should().Equal(("Limits", "Max"), ("Shown", "Value"));
    }

    [Fact]
    public void TheScan_SeesTheServersDependencies_IntoEveryLayer()
    {
        // Known dependencies that the layering allows, so they stay: an implemented port, a constructor parameter, the
        // composition root's registrations, and an Infrastructure helper a tool calls
        TypeDependencies.Of(typeof(NativeWindowWrapper)).Should().Contain(typeof(INativeWindowWrapper));
        TypeDependencies.Of(typeof(WindowManagerTools)).Should().Contain(typeof(IWindowService));
        TypeDependencies.Of(typeof(ServiceRegistration)).Should().Contain(typeof(NativeWindowWrapper)).And.Contain(typeof(WindowManagerTools));

        // Every layer that a layer other than Host may use is the target of a dependency the layering allows, so a scan that
        // saw nothing could not pass, and fixing the baseline's violations cannot make it fail
        var allowedTargets = ServerDependencies.Value
            .Where(owner => LayerOf(owner.Key) != "Host")
            .SelectMany(owner => owner.Value
                .Where(d => d.Assembly == ServerAssembly)
                .Select(d => (From: LayerOf(owner.Key)!, To: LayerOf(d))))
            .Where(edge => edge.To is not null && edge.To != Unknown && LayerNamed(edge.From).MayUse.Contains(edge.To))
            .Select(edge => edge.To!)
            .ToHashSet();
        allowedTargets.Should().BeEquivalentTo(Layers.Where(l => l.Name != "Host").SelectMany(l => l.MayUse).Distinct());

        // Host is no layer's to use; the composition root itself reaches the layers it wires. Prompts and Resources are wired
        // through Infrastructure.ResourcesAndPrompts today (a baseline line), so they are not required here
        var program = ServerAssembly.GetType("Program");
        program.Should().NotBeNull();
        TypeDependencies.Of(typeof(ServiceRegistration)).Concat(TypeDependencies.Of(program!))
            .Where(d => d.Assembly == ServerAssembly)
            .Select(LayerOf)
            .Should().Contain(["Host", "Models", "Services.Interfaces", "Native", "Services", "Infrastructure", "Tools"]);

        // The MCP SDK, by namespace and by assembly (AddMcpServer's IMcpServerBuilder is in Microsoft.Extensions.DependencyInjection)
        TypeDependencies.Of(typeof(ServiceRegistration)).Should().Contain(t => IsMcpSdk(t) && t.Namespace == "Microsoft.Extensions.DependencyInjection");
        ServerDependencies.Value.Values.SelectMany(d => d).Should().Contain(t => IsMcpSdk(t) && t.Namespace!.StartsWith("ModelContextProtocol", StringComparison.Ordinal));
    }

    /// <summary>The layer a server type belongs to (its outermost type's), null for generated code, or <see cref="Unknown"/>.</summary>
    private static string? LayerOf(Type type)
    {
        var outer = IlCalls.OutermostType(type);
        var ns = outer.Namespace ?? string.Empty;
        if ((ns.Length == 0 && outer.Name.StartsWith('<')) || GeneratedNamespaces.Contains(ns) ||
            ns.StartsWith("Windows.", StringComparison.Ordinal) || ns.StartsWith("ABI.Windows.", StringComparison.Ordinal))
        {
            return null;
        }

        if (outer.FullName is { } name && TypeLayers.TryGetValue(name, out var placed))
        {
            return placed.Layer;
        }

        return Layers.FirstOrDefault(l => l.Namespaces.Contains(ns))?.Name ?? Unknown;
    }

    private static Layer LayerNamed(string name) => Layers.Single(l => l.Name == name);

    /// <summary>The outermost types of the server's own code, each with a layer.</summary>
    private static IEnumerable<Type> ServerTypes() =>
        ServerAssembly.GetTypes().Where(t => !t.IsNested && LayerOf(t) is { } layer && layer != Unknown);

    private static List<Violation> FindViolations() =>
        ServerDependencies.Value
            .SelectMany(owner => ViolationsOf(owner.Key, LayerOf(owner.Key)!, owner.Value))
            .Concat(ConstantViolations())
            .Distinct()
            .ToList();

    /// <summary>The dependencies of <paramref name="owner"/> (and its nested types) that <paramref name="ownerLayer"/> may not have.</summary>
    private static IEnumerable<Violation> ViolationsOf(Type owner, string ownerLayer, IReadOnlySet<Type>? dependencies = null)
    {
        var layer = LayerNamed(ownerLayer);
        foreach (var dependency in dependencies ?? TypeDependencies.Of(owner))
        {
            if (dependency.Assembly == ServerAssembly)
            {
                var target = LayerOf(dependency);
                if (target is not null && target != Unknown && target != ownerLayer && !layer.MayUse.Contains(target))
                {
                    yield return new Violation($"{ownerLayer} -> {target}", DisplayName(owner), DisplayName(IlCalls.OutermostType(dependency)));
                }
            }
            else if (!layer.McpSdk && IsMcpSdk(dependency))
            {
                yield return new Violation($"{ownerLayer} -> {McpSdk}", DisplayName(owner), DisplayName(dependency));
            }
        }
    }

    /// <summary>
    /// A type of the MCP SDK: in its namespaces, or defined in one of its assemblies (ModelContextProtocol,
    /// ModelContextProtocol.Core, ...), which also put extension types such as IMcpServerBuilder in Microsoft.Extensions.*.
    /// </summary>
    private static bool IsMcpSdk(Type type) =>
        IsSdkName(type.Namespace) || IsSdkName(type.Assembly.GetName().Name);

    private static bool IsSdkName(string? name) =>
        name is not null && (name == "ModelContextProtocol" || name.StartsWith("ModelContextProtocol.", StringComparison.Ordinal));

    /// <summary>
    /// The uses of other layers' constants in the server's source files, which the IL cannot show
    /// (<see cref="TheScan_DoesNotSeeAConstant"/>): one line per file and type declaring the constants, under the rule
    /// "From -> To (constant)".
    /// </summary>
    private static IEnumerable<Violation> ConstantViolations()
    {
        var root = RepositoryFiles.PathOf("src", "Helmsman");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Where(relative => !relative.StartsWith("obj/", StringComparison.Ordinal) && !relative.StartsWith("bin/", StringComparison.Ordinal))
            .SelectMany(relative => ConstantViolationsIn($"src/Helmsman/{relative}", File.ReadAllText(Path.Combine(root, relative))));
    }

    /// <summary>
    /// The types of other layers whose constants a source file names as Type.Constant (qualified or not), one per type, its
    /// layer being its namespace's (or a <see cref="TypeLayers"/> type's, for the file named after it). A name that more than
    /// one server type declares counts only when none of them is in a layer the file may use.
    /// </summary>
    private static IEnumerable<Violation> ConstantViolationsIn(string path, string source) => ConstantUsesAgainstTheLayering(path, source).Distinct();

    private static IEnumerable<Violation> ConstantUsesAgainstTheLayering(string path, string source)
    {
        var fileName = Path.GetFileName(path);
        var placed = TypeLayers.FirstOrDefault(t => fileName.StartsWith(t.Key[(t.Key.LastIndexOf('.') + 1)..] + ".", StringComparison.Ordinal));
        var ns = Regex.Match(source, @"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline) is { Success: true } match ? match.Groups[1].Value : string.Empty;
        var layerName = placed.Key != null ? placed.Value.Layer : Layers.FirstOrDefault(l => l.Namespaces.Contains(ns))?.Name;
        if (layerName == null)
        {
            yield break;
        }

        var layer = LayerNamed(layerName);
        foreach (var (typeName, constant) in ConstantUses(source).Distinct())
        {
            var declaring = ServerConstants.Value[(typeName, constant)].OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
            var layers = declaring.Select(t => LayerOf(t)!).ToList();
            if (declaring.Count == 0 || layers.Any(l => l == layerName || layer.MayUse.Contains(l)))
            {
                continue;
            }

            yield return new Violation($"{layerName} -> {layers[0]}{ConstantSuffix}", path, DisplayName(declaring[0]));
        }
    }

    /// <summary>The const fields of the server's types (not enum members), by (the type's simple name, the constant's name).</summary>
    private static readonly Lazy<ILookup<(string Type, string Name), Type>> ServerConstants = new(() =>
        ServerAssembly.GetTypes()
            .Where(t => !t.IsEnum && LayerOf(t) is { } layer && layer != Unknown)
            .SelectMany(t => t.GetFields(IlCalls.DeclaredMembers).Where(f => f.IsLiteral).Select(f => (Type: t, Field: f)))
            .ToLookup(c => (Regex.Replace(c.Type.Name, @"`\d+$", string.Empty), c.Field.Name), c => c.Type));

    /// <summary>
    /// The tokens of C# source that can hold "//" or "/*" without starting a comment, and the comments, matched left to right
    /// so that a string's "http://" or "image/*" is not taken for one: raw string literals (any number of quotes, interpolated
    /// or not), verbatim and interpolated verbatim strings, regular and interpolated strings, char literals, then the comments.
    /// An interpolated string whose hole holds another string may end early; the text after it is then read as code, which
    /// only matters if it holds a "//" or "/*".
    /// </summary>
    private static readonly Regex SourceTokens = new(
        @"(?<string>\$*(?<quotes>""{3,})[\s\S]*?\k<quotes>(?!"")" +
        @"|(?:\$@|@\$|@)""(?:[^""]|"""")*""" +
        @"|\$?""(?:[^""\\\n]|\\.)*""" +
        @"|'(?:[^'\\\n]|\\.)+')" +
        @"|(?<comment>//[^\n]*|/\*[\s\S]*?\*/)",
        RegexOptions.Compiled);

    /// <summary>
    /// Every Type.Member pair that <paramref name="source"/> names outside comments, in order. Strings are kept, because an
    /// interpolation hole is code; a string whose text spells out Type.Constant of the server would count too.
    /// </summary>
    private static List<(string Type, string Member)> ConstantUses(string source)
    {
        var code = SourceTokens.Replace(source, m => m.Groups["comment"].Success ? Regex.Replace(m.Value, @"[^\n]", " ") : m.Value);
        return Regex.Matches(code, @"(?<![\w@])([A-Za-z_]\w*)(?=\s*\.\s*([A-Za-z_]\w*))")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
            .ToList();
    }

    /// <summary>A type's name in the baseline: its full name without the root namespace (and a file-local type's hash).</summary>
    private static string DisplayName(Type type)
    {
        var name = type.FullName ?? type.Name;
        name = name.StartsWith("Helmsman.", StringComparison.Ordinal) ? name["Helmsman.".Length..] : name;
        return Regex.Replace(name, @"<(\w+)>F[0-9A-F]{64}__", "<$1>F__");
    }

    private static string Describe(Violation violation)
    {
        var arrow = violation.Rule.IndexOf(" -> ", StringComparison.Ordinal);
        var (from, to) = (violation.Rule[..arrow], violation.Rule[(arrow + 4)..].Replace(ConstantSuffix, ", through a constant", StringComparison.Ordinal));
        var layer = LayerNamed(from);
        var allowed = layer.MayUse.Length == 0 ? "no other layer" : string.Join(", ", layer.MayUse);
        var sdk = layer.McpSdk ? " and the MCP SDK" : ", not the MCP SDK";
        return $"  {violation.Owner} ({from}) depends on {violation.Target} ({to}); {from} may use {allowed}{sdk}. {layer.Why}";
    }

    /// <summary>The order of the rules in the baseline: by the depending layer, then the depended-on layer, inner first.</summary>
    private static int RuleRank(string rule)
    {
        var rules = Layers.SelectMany(from => Layers.Select(to => to.Name).Append(McpSdk)
                .SelectMany(to => new[] { $"{from.Name} -> {to}", $"{from.Name} -> {to}{ConstantSuffix}" }))
            .ToList();
        var rank = rules.IndexOf(rule);
        return rank < 0 ? int.MaxValue : rank;
    }

    private static string FixOf(string rule) => Fixes.TryGetValue(rule, out var fix) ? fix : DefaultFix(rule);

    private static string DefaultFix(string rule)
    {
        var target = rule[(rule.IndexOf(" -> ", StringComparison.Ordinal) + 4)..].Replace(ConstantSuffix, string.Empty, StringComparison.Ordinal);
        return target switch
        {
            McpSdk => "return a model or throw a domain exception, and let Tools or Infrastructure turn it into the SDK's type",
            "Services" => "depend on the service's port in Services.Interfaces, or move the helper inward to Models",
            "Native" => "depend on the wrapper's port in Services.Interfaces, and move the structs and enums the ports pass to Models",
            _ => $"move what is used inward (to Models or Services.Interfaces), or move the depending type out to {target}",
        };
    }

    private static List<BaselineEntry> ReadBaseline()
    {
        var text = Snapshots.ReadNormalized(BaselinePath)
            ?? throw new InvalidOperationException($"{BaselinePath} does not exist.");
        return ParseBaseline(text);
    }

    /// <summary>
    /// The lines of a baseline: "## rule: fix" opens a group, "Owner -> Target" (optionally "  # reason") is an entry of the
    /// group above it, and other lines starting with '#' are comments.
    /// </summary>
    private static List<BaselineEntry> ParseBaseline(string text)
    {
        var entries = new List<BaselineEntry>();
        string? rule = null;
        foreach (var (line, number) in text.Split('\n').Select((line, i) => (line.Trim(), i + 1)))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var colon = line.IndexOf(": ", StringComparison.Ordinal);
                rule = colon < 0 ? line[3..] : line[3..colon];
                continue;
            }

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var hash = line.IndexOf(" #", StringComparison.Ordinal);
            var reason = hash < 0 ? null : line[(hash + 2)..].Trim();
            var pair = (hash < 0 ? line : line[..hash]).Trim();
            var arrow = pair.IndexOf(" -> ", StringComparison.Ordinal);
            if (rule == null || arrow < 0)
            {
                throw new InvalidOperationException(
                    $"{BaselinePath}:{number}: expected 'Owner -> Target' under a '## rule: fix' line, found '{line}'.");
            }

            entries.Add(new BaselineEntry(new Violation(rule, pair[..arrow].Trim(), pair[(arrow + 4)..].Trim()), string.IsNullOrEmpty(reason) ? null : reason));
        }

        return entries;
    }

    /// <summary>The canonical text of a baseline: the header, then each rule's group in <see cref="RuleRank"/> order, sorted.</summary>
    private static string RenderBaseline(IEnumerable<BaselineEntry> entries)
    {
        var text = new StringBuilder(BaselineHeader);
        foreach (var group in entries.DistinctBy(e => e.Key).GroupBy(e => e.Key.Rule).OrderBy(g => RuleRank(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            text.Append('\n').Append($"## {group.Key}: {FixOf(group.Key)}\n");
            foreach (var entry in group.OrderBy(e => e.Key.Owner, StringComparer.Ordinal).ThenBy(e => e.Key.Target, StringComparer.Ordinal))
            {
                text.Append($"{entry.Key.Owner} -> {entry.Key.Target}").Append(entry.Reason == null ? string.Empty : $"  # {entry.Reason}").Append('\n');
            }
        }

        return text.ToString();
    }

    private static void RewriteBaseline(IEnumerable<BaselineEntry> entries) =>
        Snapshots.WritePreservingFormat(BaselinePath, RenderBaseline(entries));

    /// <summary>A layer: its namespaces, the layers it may use besides itself, whether it may name the MCP SDK, and why.</summary>
    private sealed record Layer(string Name, string[] Namespaces, string[] MayUse, bool McpSdk, string Why);

    /// <summary>A dependency against the layering: the rule ("From -> To"), the depending type and the type it depends on.</summary>
    private sealed record Violation(string Rule, string Owner, string Target);

    private sealed record BaselineEntry(Violation Key, string? Reason);
}

/// <summary>An attribute for <see cref="LayeringSamples"/>: its argument is what the scan must see.</summary>
[AttributeUsage(AttributeTargets.All)]
internal sealed class LayeringSampleAttribute(object value) : Attribute
{
    public object Value { get; } = value;

    public Type? Named { get; set; }
}

/// <summary>
/// Samples for <see cref="NamespaceDependencyTests"/>' canaries: each names a server type in one way only, so a scan that
/// missed that way would let the same dependency in a server type pass unseen. Nothing calls them.
/// </summary>
internal static class LayeringSamples
{
    [LayeringSample(typeof(UIElementTextFormat))]
    internal sealed class ThroughATypeofAttributeArgument;

    [LayeringSample(NativeClipboardWrapper.FormatPresence.Present)]
    internal sealed class ThroughAnEnumAttributeArgument;

    internal sealed class ThroughAMethodBodyOnly
    {
        public static bool Refuses(string url) => BrowserNavigationUrls.Refusal(url) != null;
    }

    internal sealed class ThroughAGenericArgumentOnly
    {
        public static int Count() => Enumerable.Empty<ProcessSnapshotEntry>().Count();
    }

    internal sealed class ThroughALambdaClosure
    {
        public static Func<string, string?> Refusal(string tool) => url => BrowserNavigationUrls.Refusal(url, tool, "nothing");
    }

    internal sealed class ThroughAnAsyncStateMachine
    {
        public static async Task<string?> ShaAsync()
        {
            await Task.Yield();
            return ServerVersion.GitShaOf("1.0.0+abc");
        }
    }

    internal sealed class ThroughACatchClause
    {
        public static bool Fails(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (ToolErrorException)
            {
                return true;
            }
        }
    }

    internal sealed class ThroughATypeTokenOnly
    {
        public static string Name() => typeof(UIElementTextFormat).Name;
    }

    internal sealed class ThroughAnMcpSdkGenericArgument
    {
        public static int Count(IReadOnlyList<ModelContextProtocol.Protocol.CallToolResult> results) => results.Count;
    }

    internal sealed class ThroughAnMcpSdkExtensionType
    {
        public static bool Has(Microsoft.Extensions.DependencyInjection.IMcpServerBuilder? builder) => builder != null;
    }

    internal sealed class ThroughAFieldTypeOnly
    {
        public NativeMonitorWrapper? Field = null;
    }

    internal sealed class ThroughAPropertyTypeOnly
    {
        public UiaTree? Property => null;
    }

    internal sealed class ThroughAnEventTypeOnly
    {
        public event Action<ProcessSnapshotEntry>? Changed { add { } remove { } }
    }

    internal sealed class ThroughABaseClass() : ToolErrorException("code", "message");

    internal static class ThroughAGenericConstraint
    {
        public static int Count<T>(T[] items) where T : INativeWindowWrapper => items.Length;
    }

    [LayeringSample(0, Named = typeof(WindowManagerTools))]
    internal sealed class ThroughANamedAttributeArgument;

    internal static class ThroughAStaticFieldRead
    {
        public static TimeSpan Wait() => AccessibilityWarmup.MaxWait;
    }

    internal static class ThroughACast
    {
        public static object Cast(object value) => (OcrEngineWrapper)value;
    }

    internal static class ThroughAnIsCheck
    {
        public static bool Is(object value) => value is Uia3Tree;
    }

    internal static class ThroughABox
    {
        public static object Box() => default(ProcessSnapshotEntry);
    }

    internal static class ThroughAnIterator
    {
        public static IEnumerable<int> Lengths()
        {
            yield return 1;
            yield return ServerVersion.WithoutBuildMetadata("1.0.0+abc").Length;
        }
    }

    internal static class ThroughANestedGenericArgument
    {
        public static int Count() => Enumerable.Empty<IReadOnlyList<KeyValuePair<string, NativeProcessWrapper>>>().Count();
    }

    internal static class ThroughAnArray
    {
        public static int Count(object value) => value is NativeMonitorWrapper[][] rows ? rows.Length : 0;
    }

    internal sealed class ThroughAConstantOnly
    {
        public static string Code() => ToolErrorReporting.InvalidArgumentsErrorCode;
    }
}
