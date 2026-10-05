# TerraScale.Endpoints

A FastEndpoints-inspired authoring layer over ASP.NET Core minimal routing for
.NET 11. Endpoint discovery is attribute-free and happens at compile time
through a bundled source generator; every endpoint is one class in one file.
The library is trim-safe and works in native AOT apps (`PublishAot`).

CI verifies the packed NuGet package in an isolated `linux-x64` NativeAOT
consumer, including generated registration and a JSON endpoint round trip.
To run the same check locally (requires clang and the native linker libraries):

```sh
nice -n 19 dotnet pack TerraScale.Libs.slnx -c Release -o artifacts/packages
nice -n 19 bash scripts/check-nativeaot-package.sh artifacts/packages
```

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
`Metadata`. Endpoints without `Tags` are tagged with their first route segment.
OpenAPI is served at `/openapi/v1.json`; Scalar is served at
`/scalar`.

## Binding and serialization

Object request models bind from the JSON body, the query string, and route
values, matched to the model's JSON property names (route wins over query, query
over body). Values are converted by the model's own JSON contract, so `Guid`,
numbers, enums, and arrays (`?tag=a&tag=b`) just work:

```csharp
public sealed record GetOrderRequest(Guid Id, int Page = 1, string[]? Tags = null);

public sealed class GetOrderEndpoint : Endpoint<GetOrderRequest, Order>
{
    public override void Configure() => Get("/orders/{id:guid}");
    public override ValueTask<Order> HandleAsync(GetOrderRequest request, CancellationToken ct) => /* ... */;
}
```

On `GET`, `HEAD`, `DELETE`, and `OPTIONS` the properties are documented in
OpenAPI as path and query parameters; other verbs keep the JSON body and the body
becomes optional when route or query values supply the model. A non-JSON body is
answered with 415, malformed JSON or an unconvertible value with 400. Every
rejection carries an `application/problem+json` body. A UTF-8 body is assumed
when route or query values are merged in.

Non-object models (`int`, arrays) still bind from the JSON body only. Override
`BindAsync` to bind from somewhere else, and throw `BadHttpRequestException` to
reject the request with its status code:

```csharp
public sealed class GetOrderByNumberEndpoint : Endpoint<int, Order>
{
    public override void Configure() => Get("/orders/by-number/{number}");

    protected override ValueTask<int> BindAsync(CancellationToken ct) =>
        int.TryParse(HttpContext.Request.RouteValues["number"] as string, out var number)
            ? ValueTask.FromResult(number)
            : throw new BadHttpRequestException("number must be an integer");

    public override ValueTask<Order> HandleAsync(int number, CancellationToken ct) => /* ... */;
}
```

## Validation

Validation runs after binding (also after a custom `BindAsync`) and before
`HandleAsync`. Every error from every source is collected into one 400
validation problem. Endpoints that validate nothing keep a synchronous,
allocation-free pipeline.

**Data annotations.** Call `builder.Services.AddValidation()` and mark request
types with `[ValidatableType]`. The validation source generator ships with the
`Microsoft.NET.Sdk.Web` SDK and is AOT safe. The .NET 11 async validation
types are awaited, never run synchronously: `AsyncValidationAttribute`
subclasses and `IAsyncValidatableObject` models. Both get the request's services
through `ValidationContext.GetService`:

```csharp
public sealed class UniqueEmailAttribute : AsyncValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context) =>
        throw new NotSupportedException("Validate asynchronously.");

    protected override async Task<ValidationResult?> IsValidAsync(object? value, ValidationContext context, CancellationToken ct) =>
        await context.GetRequiredService<UserStore>().EmailExistsAsync((string)value!, ct)
            ? new ValidationResult("Email is taken.", [context.MemberName!])
            : ValidationResult.Success;
}

[ValidatableType]
public sealed record SignUpRequest([property: Required, EmailAddress, UniqueEmail] string Email);
```

**Endpoint rules.** Override `ValidateAsync` for checks that need the endpoint's
injected services. It has the same shape as `IAsyncValidatableObject.ValidateAsync`,
works without `AddValidation()`, and its errors merge with the attribute errors:

```csharp
public sealed class CreateWidgetEndpoint(WidgetStore store) : Endpoint<CreateWidgetRequest, Widget>
{
    public override void Configure() => Post("/widgets");

    protected override async IAsyncEnumerable<ValidationResult> ValidateAsync(
        CreateWidgetRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        if (await store.NameExistsAsync(request.Name, ct))
            yield return new ValidationResult("Name is taken.", [nameof(request.Name)]);
    }

    public override ValueTask<Widget> HandleAsync(CreateWidgetRequest request, CancellationToken ct) => /* ... */;
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
`AcceptedAsync`, `NotFoundAsync`, `NoContentAsync`, `BadRequestAsync`,
`UnauthorizedAsync`, `ForbiddenAsync`, `ConflictAsync`, `StatusCodeAsync`,
`RedirectAsync`, `ProblemAsync`, `ValidationProblemAsync`, and `ResponseAsync`. A
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
