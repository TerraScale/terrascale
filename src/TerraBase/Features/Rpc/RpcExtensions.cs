using Microsoft.AspNetCore.Connections.Features;
using TerraBase.Hosting;
using TerraScale.TerraBase.Rpc;

namespace TerraBase.Features.Rpc;

public static class RpcExtensions
{
    public static IServiceCollection AddTerraBaseRpc(this IServiceCollection services) =>
        services.AddTerraBaseRestRpc(static (features, user) =>
            features.Set<IConnectionItemsFeature>(Listeners.AcceptedBy(user ? TerraBaseListener.Client : TerraBaseListener.Internal)));
}
