# TerraScale.Endpoints example

From the repository root, run `dotnet run --project src/TerraScale.Endpoints.Example/TerraScale.Endpoints.Example.csproj` and open `/scalar` or `/openapi/v1.json`.

The endpoint files demonstrate FastEndpoints-style `Configure()` declarations,
`HandleAsync()` handlers, DI, response metadata, anonymous access, and a raw
`IEndpoint`. The generated `AddTerraScaleEndpoints()` registers them without
reflection scanning.

The project sets `PublishAot`, so every build also runs the trim and AOT
analyzers. `ExampleJsonContext` supplies the source-generated JSON contracts.
To produce a native binary, run
`dotnet publish src/TerraScale.Endpoints.Example -c Release`.
