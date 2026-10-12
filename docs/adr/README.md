# TerraScale.DotNet ADR index

These records define the public .NET SDK boundary. The workspace
[`../../../GOAL.md`](../../../GOAL.md)
controls the product target. An accepted SDK boundary is not live service or
production evidence.

| ADR | Status | Decision |
| --- | --- | --- |
| [0001](0001-support-dotnet-10-lts.md) | Accepted; qualification pending | Support the pinned .NET 10 LTS target and verify the exact package set. |
| [0004](0004-public-contract-parity-and-compatibility.md) | Accepted boundary | Clients follow their owner-published contracts (the native and Atlas parts are superseded by 0005's update). |
| [0005](0005-terrabase-client.md) | Accepted boundary | The TerraBase client is the database client; Native was removed (2026-10-12). |
