using Microsoft.AspNetCore.Connections.Features;
using TerraBase.Hosting;
using TerraBase.Features.Callers;
using TerraScale.TerraBase.Rpc;

namespace TerraBase.Features.Rpc;

public static class RpcExtensions
{
    public static IServiceCollection AddTerraBaseRpc(this IServiceCollection services) =>
        services.AddTerraBaseRestRpc(static (features, user) =>
        {
            var caller = features.Get<HttpContext>()?.Features.Get<CallerIdentity>();
            features.Set<IConnectionItemsFeature>(Listeners.AcceptedBy(user && caller is null ? TerraBaseListener.Client : TerraBaseListener.Internal));
        });
}
