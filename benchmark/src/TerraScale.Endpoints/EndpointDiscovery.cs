using System.Collections.Concurrent;
using System.Reflection;

namespace TerraScale.Endpoints;

/// <summary>Discovers endpoint classes once per assembly and reuses the result.</summary>
public static class EndpointDiscovery
{
    private static readonly ConcurrentDictionary<Assembly, Lazy<Type[]>> Cache = new();

    /// <summary>Finds concrete endpoint classes in an assembly.</summary>
    /// <param name="assembly">The assembly to inspect.</param>
    /// <returns>Deterministically ordered endpoint types.</returns>
    public static IReadOnlyList<Type> Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Cache.GetOrAdd(assembly, static source => new Lazy<Type[]>(
            () => source
                .DefinedTypes
                .Where(type => !type.IsAbstract && !type.IsInterface && typeof(IEndpoint).IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .Select(type => type.AsType())
                .ToArray(),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}

/// <summary>An immutable endpoint catalog built during application startup.</summary>
public sealed class EndpointCatalog
{
    private readonly Type[] endpointTypes;

    /// <summary>Creates a catalog by scanning the supplied assemblies once.</summary>
    public EndpointCatalog(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        this.endpointTypes = assemblies
            .Distinct()
            .SelectMany(EndpointDiscovery.Discover)
            .Distinct()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Creates a catalog from a precomputed endpoint list.</summary>
    public EndpointCatalog(IEnumerable<Type> endpointTypes)
    {
        ArgumentNullException.ThrowIfNull(endpointTypes);
        this.endpointTypes = endpointTypes
            .Distinct()
            .Where(type => !type.IsAbstract && !type.IsInterface && typeof(IEndpoint).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The endpoint types in deterministic mapping order.</summary>
    public IReadOnlyList<Type> Types => this.endpointTypes;

    /// <summary>The number of discovered endpoints.</summary>
    public int Count => this.endpointTypes.Length;

    /// <summary>Maps every endpoint exactly once.</summary>
    public void Map(IEndpointRouteBuilder routes, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(services);
        using var scope = services.CreateScope();
        foreach (var endpointType in this.endpointTypes)
        {
            var endpoint = (IEndpoint)ActivatorUtilities.GetServiceOrCreateInstance(scope.ServiceProvider, endpointType);
            endpoint.Map(routes);
        }
    }
}
