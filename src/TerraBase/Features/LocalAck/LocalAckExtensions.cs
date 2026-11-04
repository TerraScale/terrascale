using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraBase.Features.LocalAck;

public static class LocalAckExtensions
{
    public static IServiceCollection AddTerraBaseLocalAck(this IServiceCollection services)
    {
        services.TryAddSingleton<IRegionPartners, PeersFileRegionPartners>();
        services.AddSingleton<HoldStore>();
        services.AddSingleton<InRegionAck>();
        services.AddSingleton<IInRegionAck>(provider => provider.GetRequiredService<InRegionAck>());
        return services.AddHostedService(provider => provider.GetRequiredService<InRegionAck>());
    }
}
