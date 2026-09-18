# TerraScale.Endpoints example

From the repository root, run `nice -n 19 dotnet run --project src/TerraScale.Endpoints.Example/TerraScale.Endpoints.Example.csproj` and open `/scalar` or `/openapi/v1.json`.

The endpoint files demonstrate FastEndpoints-style `Configure()` declarations,
`HandleAsync()` handlers, DI, custom binding, response metadata, anonymous access,
and the cached assembly discovery. Use a precomputed `EndpointCatalog` type list
when a build-time registry should avoid reflection entirely.
