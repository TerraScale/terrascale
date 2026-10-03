# TerraScale.Endpoints

A FastEndpoints-inspired authoring layer over ASP.NET Core minimal routing for
.NET 11. Endpoint discovery is attribute-free and happens at compile time
through a bundled source generator; every endpoint is one class in one file.
The library is trim-safe and works in native AOT apps (`PublishAot`).

The authoring model follows the FastEndpoints endpoint definitions documented at
<https://fast-endpoints.com/docs/get-started> and
<https://fast-endpoints.com/docs/endpoint-definitions>: configure the verb and
route in `Configure()`, keep handling code in `HandleAsync()`, use constructor
injection, and let startup discovery find endpoint classes.

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

    public override ValueTask<Widget> HandleAsync(
        CreateWidgetRequest request, CancellationToken ct) =>
        ValueTask.FromResult(store.Create(request.Name));
}
```

Register once in `Program.cs`. The parameterless `AddTerraScaleEndpoints()` is
generated into your project and lists every concrete endpoint class, so there is
no reflection scanning at startup:

```csharp
builder.Services.AddTerraScaleEndpoints();
var app = builder.Build();
app.MapTerraScaleEndpoints();
app.MapTerraScaleOpenApi();
```

The generated `GeneratedEndpoints.All` list can be combined with endpoints
registered by hand (`EndpointRegistration.Create<T>()`), and repeated
`AddTerraScaleEndpoints` calls add to the same catalog. The generator warns
(`TSE001`, `TSE002`) about endpoints it cannot register: private nested classes
and open generics. The reflection overloads that take an `Assembly` or `Type`
list still exist for compatibility but are marked `[RequiresUnreferencedCode]`.

Endpoints are registered as transient services. Each request gets a fresh
instance that is resolved from the request scope and disposed with it, so
constructor injection of scoped services just works.

`Configure()` exposes `Get`, `Post`, `Put`, `Patch`, `Delete`, `Verbs`,
`Route`, `Name`, `Summary`, `Description`, `Tags`, `Produces<T>`,
`ProducesSse`, `Accepts<T>`, `AllowAnonymous`, `RequireAuthorization`, and
`Metadata`. OpenAPI is served at `/openapi/v1.json`; Scalar is served at
`/scalar`.

## Binding and serialization

Request models are read from the JSON body. A non-JSON content type is answered
with 415, and malformed or `null` JSON with 400. Override `BindAsync` to bind
from somewhere else. Throw `BadHttpRequestException` to reject the request with
its status code:

```csharp
public sealed class GetOrderEndpoint : Endpoint<int, Order>
{
    public override void Configure() => Get("/orders/{id}");

    protected override ValueTask<int> BindAsync(CancellationToken ct) =>
        int.TryParse(HttpContext.Request.RouteValues["id"] as string, out var id)
            ? ValueTask.FromResult(id)
            : throw new BadHttpRequestException("id must be an integer");

    public override ValueTask<Order> HandleAsync(int id, CancellationToken ct) => /* ... */;
}
```

Responses that are `IResult` execute as-is, `string` is written as
`text/plain`, and everything else is serialized as JSON with the declared
response type. Requests and responses go through the app's
`Microsoft.AspNetCore.Http.Json.JsonOptions`. For native AOT, add every request
and response type to a `JsonSerializerContext` and insert it into
`TypeInfoResolverChain`. A missing type fails at startup, when endpoints are
mapped, rather than on the first request:

```csharp
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
```

For FastEndpoints-style command handlers, derive from `Endpoint<TRequest>` (or
`EndpointWithoutRequest`) and send a result from the handler:

```csharp
public sealed class EchoEndpoint : Endpoint<EchoRequest>
{
    public override void Configure() => Post("/echo");

    public override ValueTask HandleAsync(EchoRequest request, CancellationToken ct) =>
        Send.OkAsync(new EchoResponse(request.Value));
}
```

The request and response records are plain types:

```csharp
public sealed record EchoRequest(string Value);
public sealed record EchoResponse(string Value);
```

`Send` is request-scoped and supports `OkAsync`, `CreatedAsync`,
`AcceptedAsync`, `NotFoundAsync`, `NoContentAsync`, and `ResponseAsync`. A
handler that never calls `Send` answers 204. The `Send` methods return
`ValueTask`, so `=> Send.OkAsync(...)` works as an expression-bodied handler and
`await Send.OkAsync(...)` works inside `async` ones.

## Server-sent events

Every endpoint can return a `text/event-stream` response. `Sse.Stream` runs a
handler for the request lifetime and writes standard event, id, retry, and data
lines:

```csharp
public sealed class EventsEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/events");
        ProducesSse();
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken ct) =>
        ValueTask.FromResult(Sse.Stream(async (sse, ct) =>
        {
            await sse.SendEventAsync("ready", ["connected"], cancellationToken: ct);
            await sse.CommentAsync("keepalive", ct);
        }));
}
```

The stream sets `text/event-stream`, disables response caching and proxy
buffering, and passes the request cancellation token to the handler. Use
`SendEventAsync` for events and `CommentAsync` for keepalive comments. Events
are encoded straight into the response pipe without intermediate strings. An
`SseClient` must not be written from several tasks at once.

## Upgrading from 2.0 and earlier

3.0 is the first release on .NET 11. It ships as `3.0.<n>-rc.1` prerelease
packages until .NET 11 is released.

- Targets `net11.0`.
- `AddTerraScaleEndpoints()` without arguments is now generated per project
  instead of using `Assembly.GetCallingAssembly()`.
- `EndpointCatalog` is built by `AddTerraScaleEndpoints`, and its constructors
  are no longer public. Register endpoints with `EndpointRegistration.Create<T>()`
  instead of `new EndpointCatalog(types)`.
- Endpoints are always transient DI services. Do not register them as
  singletons.
- `HandleAsync` returns `ValueTask<TResponse>` (or `ValueTask` for
  `Send`-style endpoints) instead of `Task`. `async` handlers only change their
  return type; replace `Task.FromResult(x)` with `ValueTask.FromResult(x)`.
  Synchronous handlers then complete without allocating a task.
- `EndpointSender` is a struct and its methods return `ValueTask`.
- `EndpointRequestContext` and `EndpointSenderEndpoint` are replaced by
  `EndpointBase`.
- Request binding no longer goes through `RequestDelegateFactory`. Models come
  from the JSON body or a `BindAsync` override.
