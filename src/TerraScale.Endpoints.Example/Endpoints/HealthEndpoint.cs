namespace TerraScale.Endpoints.Example.Endpoints;

public sealed class HealthEndpoint : EndpointWithoutRequest<HealthResponse>
{
    public override void Configure()
    {
        Get("/health");
        Name("health");
        Summary("Returns service health");
        Tags("System");
        AllowAnonymous();
    }

    public override ValueTask<HealthResponse> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new HealthResponse("ok"));
}

public sealed record HealthResponse(string Status);
