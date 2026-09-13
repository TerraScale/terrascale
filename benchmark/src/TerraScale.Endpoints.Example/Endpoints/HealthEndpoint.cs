using TerraScale.Endpoints;

namespace TerraScale.Endpoints.Example.Endpoints;

[Endpoint]
public sealed class HealthEndpoint : EndpointWithoutRequest<HealthResponse>
{
    public override string Route => "/health";
    public override string Name => "health";
    public override string Summary => "Returns service health";
    public override IReadOnlyList<string> Tags => ["System"];

    public override Task<HealthResponse> ExecuteAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new HealthResponse("ok"));
}

public sealed record HealthResponse(string Status);
