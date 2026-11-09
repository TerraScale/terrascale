using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Observability
{
    public sealed class HealthzEndpoint : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Get("/healthz");
            Name("healthz");
            Summary("Liveness probe");
            Tags("Observability");
            AllowAnonymous();
        }

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Results.Text("ok"));
    }

    public sealed class ReadyzEndpoint(DependencyReadiness readiness) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Get("/readyz");
            Name("readyz");
            Summary("Dependency readiness probe");
            Tags("Observability");
            AllowAnonymous();
        }

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(readiness.CheckAsync(HttpContext, cancellationToken));
    }

    public sealed class HealthReadyEndpoint(DependencyReadiness readiness) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Get("/health/ready");
            Name("healthReady");
            Summary("Dependency readiness probe");
            Tags("Observability");
            AllowAnonymous();
        }

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(readiness.CheckAsync(HttpContext, cancellationToken));
    }

    public sealed class HealthLiveEndpoint(DependencyReadiness readiness) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Get("/health/live");
            Name("healthLive");
            Summary("Liveness with region and revision");
            Tags("Observability");
            AllowAnonymous();
        }

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Results.Json(new { status = "ok", region = readiness.Region, version = readiness.Revision }));
    }
}
