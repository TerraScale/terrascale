using TerraScale.Endpoints;

namespace TerraScale.Endpoints.Example.Endpoints;

[Endpoint]
public sealed class HealthEndpoint : EndpointWithoutRequest<HealthResponse>
{
    public override void Configure(EndpointDefinition definition) => definition
        .Get("/health")
        .WithName("health")
        .WithSummary("Returns service health")
        .WithTags("System");

    public override Task<HealthResponse> ExecuteAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new HealthResponse("ok"));
}

public sealed record HealthResponse(string Status);
