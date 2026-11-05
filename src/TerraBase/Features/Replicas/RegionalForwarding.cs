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
            if (Listeners.ArrivedOn(context) != TerraBaseListener.Client || owners.Writer
                || ReferenceEquals(context.GetEndpoint()?.Metadata.GetMetadata<ListenerMetadata>(), ListenerMetadata.Probe))
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

            await HomeForwarding.ForwardAsync(context, new HomeNode(options.Region,
                new UriBuilder(Uri.UriSchemeHttp, owner.Address.ToString(), options.InternalPort).Uri)).ConfigureAwait(false);
        });
        return app;
    }
}
