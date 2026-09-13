# TerraScale.Endpoints example

Run it with `dotnet run --project TerraScale.Endpoints.Example.csproj`, then open
`/scalar` for the Scalar UI or `/openapi/v1.json` for the OpenAPI document.

Each file under `Endpoints/` owns one route. Endpoint classes are discovered once
when `AddTerraScaleEndpoints` runs and mapped in deterministic order. For build-time
registries, pass a precomputed `IEnumerable<Type>` to avoid assembly scanning.

The example shows:

- a no-request endpoint with concise route properties and OpenAPI metadata;
- a JSON request/response endpoint with constructor-injected services;
- a hand-written endpoint for custom binding and multiple response statuses;
- the fluent `Configure(EndpointDefinition)` escape hatch described in the package README;
- Scalar and OpenAPI wiring with one line each.
