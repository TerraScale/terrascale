# Development contract

The workspace [GOAL.md](../../GOAL.md) is the product and architecture
authority;
[docs/adr/README.md](adr/README.md) indexes durable local context, and
[AGENTS.md](../AGENTS.md) contains the repository rules. Source and tests are
the implementation authority.

TerraBase is a .NET 10 C# host with a single runtime project. The host serves
the typed API on a loopback ASP.NET Core listener and uses TBEP MessagePack over
the configured Unix socket for its managed or attached engine boundary. SQL is
the only query language, document writes retain causal and history semantics,
and the current replication and sync designs are ADR 0044.

The runtime layout is intentionally small: `src/TerraBase/TerraBase.csproj`
contains the host, API feature slices, protocol, storage boundary, and
observability; `src/TerraScale.Endpoints` contains shared endpoint discovery;
and `src/TerraScale.Endpoints.Example` contains the example host. The solution
also includes `tests/TerraBase.Tests`, `tests/TerraBase.Api.Tests`,
`tests/TerraBase.Protocol.Tests`, and `tests/TerraScale.Endpoints.Tests`.

Run the same solution build and test locally that CI runs:

```bash
nice -n 19 dotnet build TerraBase.slnx --configuration Release
nice -n 19 dotnet format style TerraBase.slnx --verify-no-changes --no-restore
nice -n 19 dotnet test TerraBase.slnx --configuration Release --no-build
```

After a successful build, run the loopback host with an attached or managed
TBEP engine using:

```bash
nice -n 19 dotnet run --project src/TerraBase/TerraBase.csproj --no-build
```

Set `TERRABASE_TBEP_SOCKET` and, when the host should supervise an engine,
`TERRABASE_TBEP_ENGINE`. `TERRABASE_HOST_URL` selects the loopback listener.
Engine-backed test rows require the separately supplied engine source or
executable and are explicitly skipped when it is unavailable.

When changing the published OpenAPI, protobuf, or MessagePack contract, update
the checked-in contract archive and regenerate consumer output in the affected
SDK repository. Do not hand-edit generated output. Keep changes small, remove
retired code and its documentation in the same series, and update links when a
document is removed.

Builds and tests should use the lowest available CPU priority. The host uses
bounded asynchronous I/O and bounded CPU parallelism derived from admitted
resources; blocking work must not run on request threads. Retain deterministic
results, durability, authorization, tenant isolation, fairness, backpressure,
and recovery while changing performance-sensitive code.
