---
name: add-endpoint
description: Add an HTTP endpoint to a TerraScale .NET service (TerraBase, ts-auth, Dashboard) using TerraScale.Endpoints, with OpenAPI, auth scope, JSON context registration and a test. Use when adding or changing a REST route.
---

# Add an HTTP endpoint

Every HTTP endpoint, including health and admin, uses `TerraScale.Endpoints` (`src/TerraScale.Endpoints/README.md`). No minimal-API lambdas or controllers.

## 1. One class per file

Put it in the feature folder: `src/TerraBase/Features/<Feature>/<Verb><Thing>Endpoint.cs`. Copy a sibling, e.g. `src/TerraBase/Features/ApiKeys/CreateApiKeyEndpoint.cs`.

```csharp
namespace TerraBase.Features.Widgets;

/// <summary>A widget to create.</summary>
/// <param name="Name">Label, 1 to 100 characters.</param>
public sealed record CreateWidgetRequest(string Name);

/// <summary>A created widget.</summary>
public sealed record WidgetBody(string Id, string Name);

/// <summary><c>POST /v1/.../widgets</c>: creates a widget.</summary>
public sealed class CreateWidgetEndpoint(IWidgetStore widgets) : Endpoint<CreateWidgetRequest, IResult>
{
    public override void Configure()
    {
        Post(DatabaseRoute.Template + "/widgets");   // public routes live under /v1
        Name("createWidget");                         // stable, unique operation id
        Summary("Creates a widget");
        Tags("Widgets");
        Produces<WidgetBody>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        RequireAuthorization(ApiKeyScopes.SqlWrite); // the narrowest existing scope
        Metadata(ListenerMetadata.Client);           // TerraBase: serve on the client listener
    }

    public override async ValueTask<IResult> HandleAsync(CreateWidgetRequest request, CancellationToken ct)
    {
        // validate -> call the service -> map the result; keep logic in the service
    }
}
```

TerraBase listener rules: endpoints without `ListenerMetadata.Client` are admin-only (loopback). Client endpoints need `RequireAuthorization(<scope>)`. Wrong-listener requests return 404 (`src/TerraBase/Hosting/Listeners.cs`).

## 2. Register the JSON types

Add `[JsonSerializable(typeof(...))]` for every request and response record to the service's source-generated context:

- TerraBase: `src/TerraBase/Hosting/TerraBaseJsonContext.cs`
- ts-auth: the context next to the feature (e.g. `src/TerraScale.Authentication/Features/Platform/PlatformEndpoints.cs`)

Discovery is compile-time; no manual route registration is needed.

## 3. Contract details

- XML doc comments on the records and endpoint become the OpenAPI text (rendered by Scalar). Describe limits and errors there.
- IDs are ULIDs. Errors use the service's `ErrorBody`/`ErrorResponse`. Don't leak whether another tenant's resource exists.
- Validate everything; return 400 with a clear message. Check the caller owns the resource.
- ts-auth: OpenAPI is checked by `make auth-openapi-check`; change implementation, OpenAPI, examples and tests together.

## 4. Test

One happy-path test in `tests/<Project>.Fast/<Feature>/` that calls the real endpoint over HTTP. Add one critical-failure test only for auth, tenant isolation or data-loss risks. Copy the fixture use from a sibling test file.

## 5. Then

- If customers call it, add it to the SDKs (`sdk-parity` skill) and update the API doc in `docs/<area>/`.
- Build: `make terrabase-build` / `make auth-build` / `make dashboard-build`. Open a PR; CI runs the tests.
