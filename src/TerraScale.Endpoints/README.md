# TerraScale.Endpoints

A FastEndpoints-inspired authoring layer over ASP.NET Core minimal routing.
Endpoint discovery is attribute-free and cached per assembly; every endpoint is
one class in one file.

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

For FastEndpoints-style command handlers, derive from `Endpoint<TRequest>` (or
`EndpointWithoutRequest`) and send a result from the handler:

```csharp
public sealed class EchoEndpoint : Endpoint<EchoRequest>
{
    public override void Configure() => Post("/echo");

    public override async Task HandleAsync(EchoRequest request, CancellationToken ct)
    {
        await Send.OkAsync(new { request.Value });
    }
}
```

`Send` is request-scoped and supports `OkAsync`, `CreatedAsync`,
`AcceptedAsync`, `NotFoundAsync`, `NoContentAsync`, and `ResponseAsync`.

## Server-sent events and Datastar

Every endpoint can return a `text/event-stream` response. `Datastar.Stream`
implements the [Datastar](https://data-star.dev) SDK wire protocol natively —
patch elements and signals, remove them, or execute scripts in the browser —
with no extra package dependency:

```csharp
public sealed class CounterEndpoint : Endpoint<CounterSignals, IResult>
{
    public override void Configure()
    {
        Post("/counter");
        ProducesSse();
    }

    public override Task<IResult> HandleAsync(CounterSignals request, CancellationToken ct) =>
        Task.FromResult(Datastar.Stream((datastar, ct) =>
            datastar.PatchSignalsAsync(new { clicks = request.Clicks + 1 }, cancellationToken: ct)));
}
```

Long-lived streams keep writing until the handler completes or the browser
disconnects; the cancellation token passed to the stream fires on disconnect:

```csharp
public sealed class ClockEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure() { Get("/clock"); ProducesSse(); }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult(Datastar.Stream(async (datastar, ct) =>
        {
            while (!ct.IsCancellationRequested)
            {
                await datastar.PatchElementsAsync($"<time id=\"clock\">{DateTimeOffset.Now:HH:mm:ss}</time>", cancellationToken: ct);
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }));
}
```

The Datastar client covers the full protocol: `PatchElementsAsync`
(`selector`, `mode`, `useViewTransition`, `viewTransitionSelector`,
`namespace`), `PatchSignalsAsync`/`PatchSignalsJsonAsync` (JSON merge patch
semantics, web/camelCase JSON), `RemoveFragmentsAsync`, `RemoveSignalsAsync`
(dotted paths), and `ExecuteScriptAsync` (self-removing script tag). Default
options are omitted from the wire format per the SDK specification, and
`eventId`/`retry` are supported for replay and reconnection.

Browser signals come back two ways: JSON bodies bind to `TRequest` as usual
(POST/PUT/PATCH), and the `datastar` query parameter on GET/DELETE requests is
decoded by `ReadSignalsAsync<T>()`:

```csharp
public sealed class EchoEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure() { Get("/echo"); ProducesSse(); }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var signals = await ReadSignalsAsync<EchoSignals>(ct);
        return Datastar.Stream((datastar, ct) =>
            datastar.PatchSignalsAsync(new { output = $"echo: {signals.Input}" }, cancellationToken: ct));
    }
}
```

All endpoints also expose `HttpContext` while handling a request, and
`Datastar.ReadSignalsAsync<T>(request)` is available as a static for callers
outside the endpoint classes. For anything outside the Datastar protocol,
`Sse.Stream` writes raw events (`SendEventAsync` with event/id/retry/data
lines, plus `CommentAsync` keepalives), following the same header and flush
rules.

