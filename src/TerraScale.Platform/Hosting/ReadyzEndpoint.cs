namespace TerraScale.Platform.Hosting;

public readonly union ReadyzReply(HealthStatus, PlatformUnavailable);

public sealed class ReadyzEndpoint(PlatformOptions options, PlatformReadiness readiness, TerraBaseClient terraBase)
    : EndpointWithoutRequest<ReadyzReply>
{
    public override void Configure()
    {
        Get("/readyz");
        Name("readyz");
        Summary("Readiness: the platform schema is in place and the regional TerraBase answers");
        Tags("Health");
        AllowAnonymous();
    }

    public override async ValueTask<ReadyzReply> HandleAsync(CancellationToken cancellationToken)
    {
        if (!readiness.SchemaReady)
        {
            return new PlatformUnavailable("the platform schema is not in place yet", "not_ready");
        }

        var outcome = await terraBase.PingAsync(cancellationToken);
        return outcome switch
        {
            TerraBaseHealthy => new HealthStatus("ready", options.Region, options.Node),
            PlatformUnavailable unavailable => unavailable,
        };
    }
}
