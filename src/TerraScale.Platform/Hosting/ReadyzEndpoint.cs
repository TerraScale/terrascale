namespace TerraScale.Platform.Hosting;

public sealed class ReadyzEndpoint(PlatformOptions options, PlatformReadiness readiness, TerraBaseClient terraBase)
    : EndpointWithoutRequest<Result<HealthStatus, Unavailable>>
{
    public override void Configure()
    {
        Get("/readyz");
        Name("readyz");
        Summary("Readiness: the platform schema is in place and the regional TerraBase answers");
        Tags("Health");
        AllowAnonymous();
    }

    public override async ValueTask<Result<HealthStatus, Unavailable>> HandleAsync(CancellationToken cancellationToken)
    {
        if (!readiness.SchemaReady)
        {
            return new Unavailable("the platform schema is not in place yet", "not_ready");
        }

        return await terraBase.PingAsync(cancellationToken) switch
        {
            Done => new HealthStatus("ready", options.Region, options.Node),
            Unavailable unavailable => unavailable,
        };
    }
}
