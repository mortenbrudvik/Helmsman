using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Helmsman.Infrastructure;

namespace Helmsman.Tests.Integration;

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

    /// <summary>
    /// The fields ("Type.field") that may hold an instance of a served type other than the container's singleton, each with
    /// the reason. Keep it short: a collaborator the container serves belongs to the container.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PrivateInstances = new Dictionary<string, string>
    {
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
        _services.AddHelmsmanServices(
            () => LayoutStorageOptions.ForPath(_folder),
            () => AuditLogOptions.ForFolder(Path.Combine(_folder, "audit")));
        _services.AddMcpServer().AddHelmsmanTools();
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
        services.AddHelmsmanServices();
        services.AddMcpServer().AddHelmsmanTools();
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
            var instance = InstanceOf(_services, _provider, descriptor);
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
        var (empty, _) = InspectAll();

        empty.Should().BeEmpty("a server object built without a collaborator the container serves silently goes without it");
    }

    [Fact]
    public void EveryResolvedServerObject_HoldsTheContainersOwnSingleton_NotACopy()
    {
        // A collaborator the container holds as a singleton must be that singleton: a factory or a service that builds its own
        // (a policy, a guard, a session) would pass every check above and still not be the one the user configured
        var (_, copies) = InspectAll();

        copies.Should().BeEmpty("a server object must hold the container's singleton of each collaborator it serves");
    }

    [Fact]
    public void InstanceOf_GivesTheInstanceOfEachRegistration_NotOnlyTheLastOfItsServiceType()
    {
        // Arrange - two registrations of one service type, as AddHostedService makes for IHostedService
        var first = new object();
        var second = new object();
        var services = new ServiceCollection();
        services.AddSingleton<object>(_ => first);
        services.AddSingleton<object>(_ => second);
        using var provider = services.BuildServiceProvider();

        // Act & Assert
        InstanceOf(services, provider, services[0]).Should().BeSameAs(first);
        InstanceOf(services, provider, services[1]).Should().BeSameAs(second);
    }

    /// <summary>
    /// The instance the container holds for <paramref name="descriptor"/>: for a service type registered more than once (every
    /// hosted service is an IHostedService) the one of this registration, not the last.
    /// </summary>
    private static object InstanceOf(IServiceCollection services, IServiceProvider provider, ServiceDescriptor descriptor)
    {
        var index = services.Where(d => !d.IsKeyedService && d.ServiceType == descriptor.ServiceType).ToList().IndexOf(descriptor);
        return provider.GetServices(descriptor.ServiceType).ElementAt(index)!;
    }

    /// <summary>
    /// Inspects every server object the container hands out (each registration's own instance), the hosted services and the
    /// tool, resource and prompt classes: the served collaborators they hold as null, and those that are not the container's
    /// singleton.
    /// </summary>
    private (List<string> Empty, List<string> Copies) InspectAll()
    {
        var empty = new List<string>();
        var copies = new List<string>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var descriptor in _services.Where(d => !d.IsKeyedService && !d.ServiceType.IsGenericTypeDefinition &&
                                                         (d.ServiceType.Assembly == Server || d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))))
        {
            var instance = InstanceOf(_services, _provider, descriptor);
            Inspect(instance, instance.GetType().Name, empty, copies, visited);
        }

        foreach (var perCall in PerCallClasses().Select(row => (Type)row[0]))
        {
            Inspect(ActivatorUtilities.CreateInstance(_provider, perCall), perCall.Name, empty, copies, visited);
        }

        return (empty, copies);
    }

    [Theory]
    [InlineData(typeof(IInputSession))] // InputGateToolFilter: the user's pause of every gated tool
    [InlineData(typeof(IAuditLog))] // AuditLogToolFilter: the record of every audited tool
    public void WhatTheCallToolFiltersResolveForEachCall_IsServed(Type service)
    {
        // The filters take these from the request's services, not through a constructor, so the checks above do not see them
        _isService.IsService(service).Should().BeTrue();
        _provider.GetRequiredService(service).Should().NotBeNull();
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

    /// <summary>Whether the container's (last) registration of <paramref name="type"/> is a singleton.</summary>
    private bool IsSingleton(Type type) =>
        _services.LastOrDefault(d => !d.IsKeyedService && d.ServiceType == type)?.Lifetime == ServiceLifetime.Singleton;

    /// <summary>One of the server's classes or interfaces: not a value, an enum, a delegate or a type of another assembly.</summary>
    private static bool IsServerCollaborator(Type type) =>
        type.Assembly == Server && !type.IsValueType && !typeof(Delegate).IsAssignableFrom(type);

    private void Inspect(object? instance, string path, List<string> empty, List<string> copies, HashSet<object> visited)
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
                    Inspect(value, $"{path}.{field.Name}", empty, copies, visited);
                    continue;
                }

                if (IsSingleton(field.FieldType) && !ReferenceEquals(value, _provider.GetService(field.FieldType)) &&
                    !PrivateInstances.ContainsKey($"{type.Name}.{field.Name}"))
                {
                    copies.Add($"{path}.{field.Name} ({field.FieldType.Name}: a {value.GetType().Name} of its own)");
                }
            }
        }
    }
}
