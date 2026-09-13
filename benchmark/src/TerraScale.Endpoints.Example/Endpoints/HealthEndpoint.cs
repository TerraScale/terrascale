using TerraScale.Endpoints;
namespace TerraScale.Endpoints.Example.Endpoints;
public sealed class HealthEndpoint : EndpointWithoutRequest<HealthResponse>
{
    public override void Configure() { Get("/health"); Name("health"); Summary("Returns service health"); Tags("System"); AllowAnonymous(); }
    public override Task<HealthResponse> HandleAsync(CancellationToken cancellationToken) => Task.FromResult(new HealthResponse("ok"));
}
public sealed record HealthResponse(string Status);
