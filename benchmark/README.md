# TerraBase

TerraBase is TerraScale's .NET 11 C# database API host. The single
`src/TerraBase/TerraBase.csproj` contains the ASP.NET Core host, typed API,
feature-sliced endpoints, storage boundary, observability, and TBEP protocol
client. It serves the REST and reactive surface through the shared
`TerraScale.Endpoints` library. SQL and document operations use the checked-in
typed contracts; multi-Region durability and engine-backed behavior remain
subject to the qualification gates in the workspace goal.

The workspace [GOAL.md](../GOAL.md#terrabase) defines the production target; [AGENTS.md](AGENTS.md)
contains repository rules and [docs/adr/README.md](docs/adr/README.md) indexes
durable local decisions. `GlobalDurable` is accepted only with at least two
active selected Regions; a request with fewer Regions fails before
acknowledgement.

## Current solution layout

TerraBase is intentionally a consolidated .NET solution. The runtime project
is `src/TerraBase/TerraBase.csproj`; its `Api`, `Engine`, `Protocol`,
`Storage`, and `Observability` feature folders contain the C# host and its
engine-facing boundaries. `src/TerraScale.Endpoints` is the shared endpoint
mapping library, and `src/TerraScale.Endpoints.Example` is its small example
host. The test projects under `tests/` cover the host, protocol, core engine,
and endpoint library. `TerraBase.slnx` is the entry point for all of them.

## Runtime boundary

```text
customer SDK / dashboard
        -> edge routing and authentication
        -> loopback ASP.NET Core TerraBase host (REST + reactive API)
TerraBase host
        -> TBEP MessagePack over the configured Unix socket
        -> managed or attached engine process
```

The host binds only to a loopback HTTP/HTTPS URL. Edge routing, public
authentication, and production deployment remain outside this process. TBEP is
the private host-to-engine boundary; its socket path and optional managed
engine binary are configured with `TERRABASE_TBEP_SOCKET` and
`TERRABASE_TBEP_ENGINE`. The host owns endpoint discovery, request mapping,
stream lifecycle, protocol validation, and observability around that boundary.

## Honest implementation status

The C# host, protocol, storage, and endpoint tests are under active
qualification. Engine-backed tests require the separately supplied engine
source or executable; when it is unavailable, those rows are explicitly
skipped rather than treated as passing engine evidence. Local tests, fixtures,
and benchmark tables do not by themselves qualify an OCI or multi-Region
production deployment. The current launch blockers and required evidence are
tracked in the workspace [GOAL.md](../GOAL.md).

The SQLite path includes an in-process Litestream-style backup manager
(online snapshots, WAL generations, resumable segments, checksummed manifests,
restore verification, and retention), a bounded WAL compactor, stable hash
sharding, and a leaderless causal merge engine. Sync clients accept HTTPS and
upgrade to WSS, with SSE parsing for the same stream. These are runtime
building blocks; object-store qualification and WAN failure evidence remain
deployment gates.

## Development

Build and test the solution directly for local verification:

```sh
nice -n 19 dotnet build TerraBase.slnx --configuration Release
nice -n 19 dotnet format style TerraBase.slnx --verify-no-changes --no-restore
nice -n 19 dotnet test TerraBase.slnx --configuration Release --no-build
```

These commands build the consolidated TerraBase host, endpoint library,
examples, and test projects. The checked-in contract archive remains the
source of truth for protocol changes; update its generated consumer metadata
in the affected SDK repositories when changing the published contract. See
[docs/development.md](docs/development.md) for the contributor workflow.

To run the host after configuring an attached or managed TBEP engine, use the
same project directly:

```sh
nice -n 19 dotnet run --project src/TerraBase/TerraBase.csproj --no-build
```

The host listens on its loopback URL and reads `TERRABASE_TBEP_SOCKET`,
`TERRABASE_TBEP_ENGINE`, and `TERRABASE_HOST_URL` for its engine and listener
configuration.

TerraBase is licensed under the [MIT License](LICENSE.md).

## TerraScale.Endpoints

The C# HTTP surface uses the `TerraScale.Endpoints` components from
`src/TerraScale.Endpoints`. TerraBase endpoint classes live in individual files
under `src/TerraBase/Api/Endpoints` and can be mapped with:

```csharp
builder.Services.AddTerraScaleEndpoints(typeof(Program).Assembly);
var app = builder.Build();
app.MapTerraScaleEndpoints();
app.MapTerraScaleOpenApi();
```

Discovery scans an assembly once, caches the deterministically ordered endpoint
types, and activates them through dependency injection. Services that need zero
reflection at startup can pass a precomputed endpoint type list instead. The
integration serves OpenAPI at `/openapi/v1.json` and Scalar at `/scalar`.

Edge-case coverage is in
[`tests/TerraScale.Endpoints.Tests`](tests/TerraScale.Endpoints.Tests).
