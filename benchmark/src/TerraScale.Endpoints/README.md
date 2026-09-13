# TerraScale.Endpoints

A FastEndpoints-inspired authoring layer over ASP.NET Core minimal routing.
Endpoint discovery is attribute-free and cached per assembly; every endpoint is
one class in one file.

```csharp
public sealed class CreateWidgetEndpoint(WidgetStore store)
    : Endpoint<CreateWidgetRequest, Widget>
{
    public override void Configure()
    {
        Post("/widgets");
        Summary("Creates a widget");
        Tags("Widgets");
        AllowAnonymous();
    }

    public override Task<Widget> HandleAsync(
        CreateWidgetRequest request, CancellationToken ct) =>
        Task.FromResult(store.Create(request.Name));
}
```

Register once in `Program.cs`:

```csharp
builder.Services.AddTerraScaleEndpoints(typeof(Program).Assembly);
var app = builder.Build();
app.MapTerraScaleEndpoints();
app.MapTerraScaleOpenApi();
```

`Configure()` exposes `Get`, `Post`, `Put`, `Patch`, `Delete`, `Name`,
`Summary`, `Description`, `Tags`, `Produces<T>`, `Accepts<T>`,
`AllowAnonymous`, `RequireAuthorization`, and `Metadata`. A precomputed
`IEnumerable<Type>` can be supplied to avoid reflection scanning completely.
OpenAPI is served at `/openapi/v1.json`; Scalar is served at `/scalar`.
