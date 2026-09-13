# TerraScale.Endpoints

`TerraScale.Endpoints` gives ASP.NET Core APIs a FastEndpoints-style authoring
model while keeping the native minimal-API pipeline.

```csharp
public sealed class GetHealthEndpoint : EndpointWithoutRequest<HealthResponse>
{
    public override string Route => "/health";
    public override string Name => "health";
    public override string Summary => "Returns service health";

    public override Task<HealthResponse> ExecuteAsync(CancellationToken ct) =>
        Task.FromResult(new HealthResponse("ok"));
}
```

One endpoint class belongs in one file. Register the containing assembly once:

```csharp
builder.Services.AddTerraScaleEndpoints(typeof(Program).Assembly);
var app = builder.Build();
app.MapTerraScaleEndpoints();
app.MapTerraScaleOpenApi();
```

Discovery is cached per assembly and sorted deterministically. Applications with
a generated or hand-maintained registry can pass endpoint types directly to
`AddTerraScaleEndpoints` and avoid reflection scanning altogether. OpenAPI is
served at `/openapi/v1.json` and Scalar at `/scalar` by default.

Override `Configure(EndpointDefinition)` when an endpoint needs advanced fluent
metadata; the route properties remain the concise default.
