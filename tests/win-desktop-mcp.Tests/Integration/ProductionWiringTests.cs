using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using WinDesktopMcp.Infrastructure;

namespace WinDesktopMcp.Tests.Integration;

/// <summary>
/// Every collaborator a server object takes is registered, so none silently gets null in production.
/// </summary>
/// <remarks>
/// Microsoft's container (and ActivatorUtilities, which creates the tool, resource and prompt classes for every call, see
/// ToolJson.CreateTarget) passes an optional parameter's default when its service is not registered. A dropped or mistyped
/// registration would then start a server whose check that used the service quietly does nothing. These tests build the
/// server's container as Program.cs does and fail instead: the constructor checks look at the constructor the container would
/// choose, the instance check at what the factory registrations (which pick their own arguments) really passed.
/// In the <see cref="RealUiAutomationCollection"/>, as <see cref="DependencyInjectionTests"/>: the instance check resolves the
/// production UI Automation adapters (resolving only constructs them; nothing here reads, sends input or changes a window).
/// </remarks>
[Collection(RealUiAutomationCollection.Name)]
public sealed class ProductionWiringTests : IDisposable
{
    private static readonly Assembly Server = typeof(ServiceRegistration).Assembly;

    /// <summary>
    /// The server's types that a constructor may take without the container serving them, each with the reason. Keep it short:
    /// a collaborator belongs in ServiceRegistration.
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, string> NotServed = new Dictionary<Type, string>
    {
        [typeof(ForegroundTiming)] = "ForegroundService's settle and poll times, a test seam; the default is ForegroundTiming.Default",
        [typeof(CdpTransportLimits)] = "CdpTransport's size and time limits, a test seam; the default is the production limits"
    };

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"WiringTests_{Guid.NewGuid()}");
    private readonly ServiceCollection _services;
    private readonly ServiceProvider _provider;
    private readonly IServiceProviderIsService _isService;

    public ProductionWiringTests()
    {
        _services = new ServiceCollection();
        // The host (Host.CreateApplicationBuilder in Program.cs) registers logging; layouts and the audit log go to a temp folder
        _services.AddLogging();
        _services.AddWindowsAppManagerServices(
            () => LayoutStorageOptions.ForPath(_folder),
            () => AuditLogOptions.ForFolder(Path.Combine(_folder, "audit")));
        _services.AddMcpServer().AddWindowsAppManagerTools();
        _provider = _services.BuildServiceProvider();
        _isService = _provider.GetRequiredService<IServiceProviderIsService>();
    }

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>The server's types the container constructs: every registration by implementation type.</summary>
    public static TheoryData<Type> RegisteredImplementations()
    {
        var data = new TheoryData<Type>();
        var services = new ServiceCollection();
        services.AddWindowsAppManagerServices();
        services.AddMcpServer().AddWindowsAppManagerTools();
        foreach (var type in services
                     .Where(d => !d.IsKeyedService && d.ImplementationType?.Assembly == Server)
                     .Select(d => d.ImplementationType!)
                     .Distinct()
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            data.Add(type);
        }

        return data;
    }

    /// <summary>The tool, resource and prompt classes, which ActivatorUtilities creates from the request's services for every call.</summary>
    public static TheoryData<Type> PerCallClasses()
    {
        var data = new TheoryData<Type>();
        foreach (var type in Server.GetTypes()
                     .Where(t => t.IsDefined(typeof(McpServerToolTypeAttribute), inherit: false)
                                 || t.IsDefined(typeof(McpServerResourceTypeAttribute), inherit: false)
                                 || t.IsDefined(typeof(McpServerPromptTypeAttribute), inherit: false))
                     .Where(t => !t.IsAbstract)
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            data.Add(type);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RegisteredImplementations))]
    public void EveryRegisteredService_GetsEveryServerCollaboratorItsConstructorTakes(Type implementation)
    {
        Unserved(implementation).Should().BeEmpty(
            "the container would pass null (an optional parameter's default) for these collaborators of {0}", implementation.Name);
    }

    [Theory]
    [MemberData(nameof(PerCallClasses))]
    public void EveryToolResourceAndPromptClass_GetsEveryServerCollaboratorItsConstructorTakes(Type perCall)
    {
        Unserved(perCall).Should().BeEmpty(
            "ActivatorUtilities would pass null (an optional parameter's default) for these collaborators of {0}", perCall.Name);
        ActivatorUtilities.CreateInstance(_provider, perCall).Should().BeOfType(perCall);
    }

    [Fact]
    public void EveryFactoryRegisteredService_HasEveryOptionalServerCollaboratorItsConstructorTakesServed()
    {
        // A factory registration picks its own arguments, so which constructor it calls cannot be read here; but an optional
        // collaborator of its type that the container does not serve is one the factory cannot have taken from the container
        var unserved = new List<string>();
        foreach (var descriptor in _services.Where(d => !d.IsKeyedService && d.ImplementationFactory != null))
        {
            var instance = _provider.GetRequiredService(descriptor.ServiceType);
            var type = instance.GetType();
            if (type.Assembly != Server)
            {
                continue;
            }

            unserved.AddRange(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Where(p => p.HasDefaultValue && IsServerCollaborator(p.ParameterType) && !_isService.IsService(p.ParameterType)
                            && !NotServed.ContainsKey(p.ParameterType))
                .Select(p => $"{type.Name}: {p.ParameterType.Name} {p.Name}")
                .Distinct());
        }

        unserved.Should().BeEmpty("a factory registration can only pass a collaborator the container serves");
    }

    [Fact]
    public void EveryResolvedServerObject_HoldsEveryCollaboratorTheContainerServes()
    {
        // The factory registrations call a constructor themselves and may leave an optional collaborator out; so may a service
        // that creates a helper itself. Every server object the container hands out, and the server objects it holds, must have
        // a value in each field whose type the container serves
        var empty = new List<string>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var descriptor in _services.Where(d => !d.IsKeyedService && d.ServiceType.Assembly == Server && !d.ServiceType.IsGenericTypeDefinition))
        {
            Inspect(_provider.GetRequiredService(descriptor.ServiceType), descriptor.ServiceType.Name, empty, visited);
        }

        foreach (var hosted in _provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>())
        {
            Inspect(hosted, hosted.GetType().Name, empty, visited);
        }

        foreach (var perCall in PerCallClasses().Select(row => (Type)row[0]))
        {
            Inspect(ActivatorUtilities.CreateInstance(_provider, perCall), perCall.Name, empty, visited);
        }

        empty.Should().BeEmpty("a server object built without a collaborator the container serves silently goes without it");
    }

    [Fact]
    public void TheNotServedList_NamesOnlyTypesTheContainerReallyDoesNotServe()
    {
        // An entry for a type that is now registered would hide nothing, but would leave a stale reason behind
        NotServed.Keys.Where(type => _isService.IsService(type)).Should().BeEmpty();
    }

    /// <summary>
    /// The parameters of the constructor the container would choose whose type is one of the server's own classes or
    /// interfaces and which the container does not serve.
    /// </summary>
    private List<string> Unserved(Type type)
    {
        var constructor = ChosenConstructor(type);
        return constructor.GetParameters()
            .Where(p => IsServerCollaborator(p.ParameterType) && !_isService.IsService(p.ParameterType)
                        && !NotServed.ContainsKey(p.ParameterType))
            .Select(p => $"{p.ParameterType.Name} {p.Name}{(p.HasDefaultValue ? " (optional)" : "")}")
            .ToList();
    }

    /// <summary>
    /// The constructor the container and ActivatorUtilities choose: an [ActivatorUtilitiesConstructor] one, or else the longest
    /// public one whose every parameter is served or has a default.
    /// </summary>
    private ConstructorInfo ChosenConstructor(Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        return constructors.SingleOrDefault(c => c.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), inherit: false))
               ?? constructors
                   .OrderByDescending(c => c.GetParameters().Length)
                   .FirstOrDefault(c => c.GetParameters().All(p => p.HasDefaultValue || _isService.IsService(p.ParameterType)))
               ?? throw new InvalidOperationException($"The container can call no constructor of {type.Name}.");
    }

    /// <summary>One of the server's classes or interfaces: not a value, an enum, a delegate or a type of another assembly.</summary>
    private static bool IsServerCollaborator(Type type) =>
        type.Assembly == Server && !type.IsValueType && !typeof(Delegate).IsAssignableFrom(type);

    private void Inspect(object? instance, string path, List<string> empty, HashSet<object> visited)
    {
        if (instance == null || instance.GetType().Assembly != Server || !visited.Add(instance))
        {
            return;
        }

        for (var type = instance.GetType(); type != null && type.Assembly == Server; type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (!IsServerCollaborator(field.FieldType))
                {
                    continue;
                }

                var value = field.GetValue(instance);
                if (value == null)
                {
                    if (_isService.IsService(field.FieldType))
                    {
                        empty.Add($"{path}.{field.Name} ({field.FieldType.Name})");
                    }

                    continue;
                }

                // A helper the object made itself (not one the container hands out) is looked into as well
                if (!_isService.IsService(field.FieldType))
                {
                    Inspect(value, $"{path}.{field.Name}", empty, visited);
                }
            }
        }
    }
}
