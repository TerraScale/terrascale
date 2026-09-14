# TerraBase

TerraBase is TerraScale's .NET 11 C# storage and observability library. The
runtime project contains the storage boundary, document store, leaderless
replication engine, HTTPS sync transport, metrics, health reporting, and a
loopback ASP.NET Core host for those observability surfaces.

## Current solution layout

`src/TerraBase/TerraBase.csproj` contains the C# runtime. The independent
`TerraScale.Endpoints` project maps the host's health and Prometheus endpoints.
`TerraBaseEngine.Open()` provides in-memory or file-backed storage through the
`IStorageProvider` abstraction. `TerraBase.slnx` is the build and test entry
point.

## Runtime boundary

```text
operator or local process
        -> loopback ASP.NET Core host
        -> local metrics and health registry

application code
        -> TerraBaseEngine
        -> abstract storage, replication, and HTTPS sync components
```

The host binds to one loopback URL selected by `TERRABASE_HOST_URL` (default
`http://127.0.0.1:8420`). It exposes `/health`, `/internal/metrics`, and the
`/live`, `/startup`, and `/ready` probes. No engine process or private transport
is started by the host.

## Development

```sh
nice -n 19 dotnet build TerraBase.slnx --configuration Release
nice -n 19 dotnet format style TerraBase.slnx --verify-no-changes --no-restore
nice -n 19 dotnet test TerraBase.slnx --configuration Release --no-build
nice -n 19 dotnet run --project tests/TerraBase.Tests/TerraBase.Tests.csproj --configuration Release --no-build
```

The host can be run directly with `dotnet run --project
src/TerraBase/TerraBase.csproj`. Storage and replication tests are in
`tests/TerraBase.Tests`; host and endpoint tests use TUnit.

TerraBase is licensed under the [MIT License](LICENSE.md).
