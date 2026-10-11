using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraScale.Endpoints;

/// <summary>A trim- and AOT-safe handle for one endpoint type.</summary>
/// <remarks>
/// The source generator emits one registration per endpoint in <c>GeneratedEndpoints.All</c>;
/// use <see cref="Create{TEndpoint}"/> to register endpoints by hand.
/// </remarks>
public sealed class EndpointRegistration
{
    private readonly Action<IServiceCollection> register;

    private EndpointRegistration(Type endpointType, Action<IServiceCollection> register)
    {
        this.EndpointType = endpointType;
        this.register = register;
    }

    /// <summary>The concrete endpoint type.</summary>
    public Type EndpointType { get; }

    /// <summary>Creates a registration for <typeparamref name="TEndpoint"/>.</summary>
    public static EndpointRegistration Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TEndpoint>()
        where TEndpoint : class, IEndpoint =>
        new(typeof(TEndpoint), static services => services.TryAddTransient<TEndpoint>());

    [RequiresUnreferencedCode(EndpointDiscovery.ReflectionMessage)]
    internal static EndpointRegistration FromType(Type endpointType)
    {
        if (!EndpointDiscovery.IsEndpointType(endpointType))
        {
            throw new ArgumentException($"'{endpointType.FullName}' is not a concrete {nameof(IEndpoint)} class.", nameof(endpointType));
        }

        return new(endpointType, services => services.TryAddTransient(endpointType));
    }

    internal void AddTo(IServiceCollection services) => this.register(services);
}
