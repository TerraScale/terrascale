using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraScale.Endpoints;

public sealed class EndpointRegistration
{
    private readonly Action<IServiceCollection> register;

    private EndpointRegistration(Type endpointType, Action<IServiceCollection> register)
    {
        EndpointType = endpointType;
        this.register = register;
    }

    public Type EndpointType { get; }

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

    internal void AddTo(IServiceCollection services) => register(services);
}
