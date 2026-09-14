# Development contract

The workspace product authority and [AGENTS.md](../AGENTS.md) contain the
repository rules. Source and tests are the implementation authority.

TerraBase is a .NET 11 C# library and loopback observability host. The runtime
contains the storage boundary, document store, replication and HTTPS sync
components, health, and Prometheus metrics. Endpoint discovery is provided by
`src/TerraScale.Endpoints`.

Run the solution checks with the lowest available CPU priority:

```bash
nice -n 19 dotnet build TerraBase.slnx --configuration Release
nice -n 19 dotnet format style TerraBase.slnx --verify-no-changes --no-restore
nice -n 19 dotnet test TerraBase.slnx --configuration Release --no-build
nice -n 19 dotnet run --project tests/TerraBase.Tests/TerraBase.Tests.csproj --configuration Release --no-build
```

Set `TERRABASE_HOST_URL` to select the loopback listener. Remove retired source,
tests, generated outputs, and their documentation in the same change. Keep
storage abstractions and bounded asynchronous I/O intact.
