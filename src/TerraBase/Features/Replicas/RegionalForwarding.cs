using Grpc.AspNetCore.Server;
using TerraBase.Configuration;
using TerraBase.Features.Regions;
using TerraBase.Hosting;

namespace TerraBase.Features.Replicas;

internal static class RegionalForwarding
{
    internal static WebApplication UseTerraBaseRegionalWriter(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var services = context.RequestServices;
            var owners = services.GetRequiredService<RegionalOwners>();
            var metadata = context.GetEndpoint()?.Metadata.GetMetadata<ListenerMetadata>();
            var served = metadata?.Listeners.Contains(TerraBaseListener.Client) == true
                || ReferenceEquals(metadata, ListenerMetadata.Management)
                || context.Request.Path.StartsWithSegments("/admin");
            if (!served || ReferenceEquals(metadata, ListenerMetadata.Probe))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var ingress = services.GetRequiredService<RegionalIngress>();
            using var request = await ingress.EnterAsync(context.RequestAborted).ConfigureAwait(false);
            if (owners.Writer)
            {
                await next(context).ConfigureAwait(false);
                return;
            }
            var options = services.GetRequiredService<TerraBaseOptions>();
            var owner = (await owners.NodesAsync(context.RequestAborted).ConfigureAwait(false))
                .FirstOrDefault(node => node.Region == options.Region && node.Name != options.MachineId);
            if (owner is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            var writer = new HomeNode(options.Region, owner.InternalListener(options.InternalPort));
            if (context.WebSockets.IsWebSocketRequest)
            {
                await services.GetRequiredService<HomeSocketRelay>().RunAsync(context, writer, context.RequestAborted).ConfigureAwait(false);
            }
            else if (Listeners.ArrivedOn(context) == TerraBaseListener.Client)
            {
                await HomeForwarding.ForwardAsync(context, writer).ConfigureAwait(false);
            }
            else
            {
                await ingress.ForwardServiceAsync(context, writer).ConfigureAwait(false);
            }
        });
        return app;
    }
}
