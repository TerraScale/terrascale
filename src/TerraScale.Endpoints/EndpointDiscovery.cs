using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace TerraScale.Endpoints;

/// <summary>Reflection-based endpoint discovery, cached per assembly.</summary>
/// <remarks>Prefer the generated <c>AddTerraScaleEndpoints()</c>; it needs no scanning and works with native AOT.</remarks>
public static class EndpointDiscovery
{
    internal const string ReflectionMessage =
        "Scanning for endpoints uses reflection and is not trim or AOT safe. Use the generated AddTerraScaleEndpoints() or EndpointRegistration.Create<T>() instead.";

    private static readonly ConcurrentDictionary<Assembly, Lazy<Type[]>> Cache = new();

    /// <summary>Finds concrete endpoint classes in an assembly.</summary>
    /// <param name="assembly">The assembly to inspect.</param>
    /// <returns>Deterministically ordered endpoint types.</returns>
    [RequiresUnreferencedCode(ReflectionMessage)]
    public static IReadOnlyList<Type> Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Cache.GetOrAdd(assembly, static source => new Lazy<Type[]>(
            () => source
                .DefinedTypes
                .Where(IsEndpointType)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .Select(type => type.AsType())
                .ToArray(),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    internal static bool IsEndpointType(Type type) =>
        type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } && typeof(IEndpoint).IsAssignableFrom(type);
}

/// <summary>The endpoints registered through <c>AddTerraScaleEndpoints</c>, in deterministic mapping order.</summary>
public sealed class EndpointCatalog
{
    private readonly Dictionary<Type, EndpointRegistration> registrations = [];
    private Type[] endpointTypes = [];

    internal EndpointCatalog()
    {
    }

    /// <summary>The endpoint types in deterministic mapping order.</summary>
    public IReadOnlyList<Type> Types => this.endpointTypes;

    /// <summary>The number of registered endpoints.</summary>
    public int Count => this.endpointTypes.Length;

    internal void Add(IServiceCollection services, IEnumerable<EndpointRegistration> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoint, nameof(endpoints));
            if (this.registrations.TryAdd(endpoint.EndpointType, endpoint))
            {
                endpoint.AddTo(services);
            }
        }

        this.endpointTypes = [.. this.registrations.Keys.OrderBy(type => type.FullName, StringComparer.Ordinal)];
    }

    /// <summary>Maps every endpoint exactly once.</summary>
    public void Map(IEndpointRouteBuilder routes, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(services);

        // Base-class endpoints are only instantiated to run Configure(), so they may use scoped
        // dependencies. Raw IEndpoint implementations keep their dependencies for the app lifetime
        // and are resolved from the root provider so scope validation can catch captive scopes.
        var scope = services.CreateAsyncScope();
        try
        {
            foreach (var endpointType in this.endpointTypes)
            {
                var provider = typeof(EndpointBase).IsAssignableFrom(endpointType) ? scope.ServiceProvider : services;
                var endpoint = (IEndpoint)provider.GetRequiredService(endpointType);
                endpoint.Map(routes);
            }
        }
        finally
        {
            // Endpoints may implement only IAsyncDisposable; mapping is synchronous startup code.
            scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
