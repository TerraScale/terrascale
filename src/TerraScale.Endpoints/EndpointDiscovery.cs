using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace TerraScale.Endpoints;

public static class EndpointDiscovery
{
    internal const string ReflectionMessage =
        "Scanning for endpoints uses reflection and is not trim or AOT safe. Use the generated AddTerraScaleEndpoints() or EndpointRegistration.Create<T>() instead.";

    private static readonly ConcurrentDictionary<Assembly, Lazy<Type[]>> Cache = new();

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

public sealed class EndpointCatalog
{
    private readonly Dictionary<Type, EndpointRegistration> registrations = [];
    private Type[] endpointTypes = [];

    internal EndpointCatalog()
    {
    }

    public IReadOnlyList<Type> Types => endpointTypes;

    public int Count => endpointTypes.Length;

    internal void Add(IServiceCollection services, IEnumerable<EndpointRegistration> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoint, nameof(endpoints));
            if (registrations.TryAdd(endpoint.EndpointType, endpoint))
            {
                endpoint.AddTo(services);
            }
        }

        endpointTypes = [.. registrations.Keys.OrderBy(type => type.FullName, StringComparer.Ordinal)];
    }

    public void Map(IEndpointRouteBuilder routes, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(services);

        var scope = services.CreateAsyncScope();
        try
        {
            foreach (var endpointType in endpointTypes)
            {
                var provider = typeof(EndpointBase).IsAssignableFrom(endpointType) ? scope.ServiceProvider : services;
                var endpoint = (IEndpoint)provider.GetRequiredService(endpointType);
                endpoint.Map(routes);
            }
        }
        finally
        {
            scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
